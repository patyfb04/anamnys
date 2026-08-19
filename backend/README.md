# Anamnys AI — Backend API

ASP.NET Core 10 backend for Anamnys, a specialty-native clinical note drafting system for solo and small-group mental health and physical therapy practices.

---

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [How the Pieces Communicate](#how-the-pieces-communicate)
- [Technology Choices and HIPAA Rationale](#technology-choices-and-hipaa-rationale)
- [Billing Codes and Clinical Notes](#billing-codes-and-clinical-notes)
- [Note Pipeline — Step by Step](#note-pipeline--step-by-step)
- [Project Structure](#project-structure)
- [Running Locally](#running-locally)
- [Environment Configuration](#environment-configuration)
- [Database Migrations](#database-migrations)
- [Test Credentials](#test-credentials)

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│  Mobile App (React Native / Expo)                                │
│  authStore · TanStack Query · SignalR client · expo-av           │
└────────────┬──────────────────────────────┬─────────────────────┘
             │ HTTPS + JWT                  │ WSS (SignalR)
             ▼                              ▼
┌────────────────────────────────────────────────────────────────┐
│  ASP.NET Core 10 API                                           │
│                                                                │
│  Controllers          SignalR Hubs         Hangfire Jobs       │
│  ─────────────        ──────────────       ─────────────────── │
│  AuthController       ProgressHub          PipelineJob         │
│  TranscribeController TranscriptionHub       ├─ Whisper STT    │
│  NotesController                             ├─ LLaMA LLM      │
│  PatientsController                          ├─ BillingEngine  │
│                                              └─ SignalR push   │
└───┬───────────────────────────────────────┬────────────────────┘
    │                                       │
    ▼                                       ▼
┌──────────────┐                   ┌────────────────────┐
│  PostgreSQL  │                   │  MinIO / R2 / B2   │
│  (EF Core)   │                   │  (S3-compatible)   │
│              │                   │  Exported PDFs     │
│  Providers   │                   └────────────────────┘
│  Patients    │
│  Notes       │
│  Hangfire    │
└──────────────┘
```

All AI inference (Whisper, LLaMA) runs **on the same server** as the API. No patient data is sent to any external AI service.

---

## How the Pieces Communicate

### 1. Mobile → API (REST over HTTPS)

Every HTTP request carries a **JWT Bearer token** in the `Authorization` header. The API validates the token's HMAC-SHA256 signature on every request — no session state is stored server-side (stateless auth).

The JWT contains the provider's ID (`sub` claim), which is extracted in every controller action and appended to every database query so that a provider can only ever read or modify their own patients and notes.

### 2. Mobile ↔ API (SignalR over WebSocket)

Two persistent WebSocket connections power real-time features:

**ProgressHub** (`/hubs/progress`) — job pipeline events

After submitting audio, the mobile app connects to ProgressHub and calls `SubscribeToJob(jobId)`. This adds the connection to a SignalR group named after the job. As PipelineJob advances through its steps, it calls `_hub.Clients.Group(jobId).SendAsync(...)` to broadcast progress to all subscribers. The mobile ProgressBar updates in real time without polling.

```
Mobile                         PipelineJob (background)
  │                                │
  ├─ connect /hubs/progress        │
  ├─ SubscribeToJob("job-xyz") ──► │ (adds to group "job-xyz")
  │                                │
  │  ◄── JobProgress (10%) ────────┤  step: transcribing
  │  ◄── JobProgress (40%) ────────┤  step: structuring
  │  ◄── JobProgress (75%) ────────┤  step: billing
  │  ◄── JobComplete ──────────────┤  noteId returned
```

Because SignalR sends the JWT via query string (`?access_token=...`) for WebSocket upgrades (standard browser limitation), `Program.cs` has a special `OnMessageReceived` handler that reads the token from the query string and sets it on the context — this is the standard ASP.NET Core SignalR pattern for authenticated hubs.

**TranscriptionHub** (`/hubs/transcription`) — live dictation (Mode B)

The mobile client sends raw audio chunks as binary messages. The server buffers them per-connection. When the provider says "done", `FinalizeStream` is called, Whisper runs on the accumulated buffer, and the final transcript is pushed back.

### 3. API → Background Worker (Hangfire)

When `TranscribeController` receives an audio upload, it:

1. Saves the audio to a temp path
2. Creates a `Note` record in PostgreSQL with status `Processing`
3. Calls `BackgroundJob.Enqueue<PipelineJob>(...)` — this serializes the job parameters and inserts a row into Hangfire's PostgreSQL tables
4. Returns `202 Accepted` immediately with the `jobId`

Hangfire's server-side worker (2 threads, configured in `Program.cs`) picks up the job and runs `PipelineJob.ExecuteAsync`. If the server restarts mid-job, Hangfire marks the job as failed and retries it automatically — the note record in PostgreSQL survives the restart.

### 4. API → Storage (S3-compatible)

Only the Export flow uses storage. After PDF generation, `S3StorageService.UploadAsync` calls the AWS SDK with `ForcePathStyle = true`, which makes it compatible with MinIO, Cloudflare R2, and Backblaze B2 — the same code works against all three. The file is stored under `exports/{providerId}/{noteId}/{timestamp}.pdf`. A **presigned URL** valid for 1 hour is returned to the mobile client. The URL expires automatically — no need to manage access control lists.

---

## Technology Choices and HIPAA Rationale

HIPAA's Security Rule (45 CFR §164.312) requires covered entities to protect electronic PHI (ePHI) through access controls, audit controls, integrity controls, and transmission security. Every major technology choice below is made with those requirements in mind.

### Self-hosted AI (Whisper + LLaMA) — no PHI leaves the server

This is the most important architectural decision. Cloud AI APIs (OpenAI, Google, Azure OpenAI) require sending patient audio and clinical notes to a third-party server. Under HIPAA, this requires a signed **Business Associate Agreement (BAA)** with the AI provider, and even then the data traverses external networks and is processed on hardware outside your control.

By running Whisper.net and LLamaSharp locally, **all PHI stays on your infrastructure**. There is no BAA required for the AI layer. The risk surface is entirely within your network boundary.

Whisper (MIT license) handles speech-to-text. LLamaSharp with Phi-4 14B (or Mistral 7B for dev) handles note structuring. Both run as in-process .NET services — no sidecar, no network call, no API key.

### PostgreSQL — structured ePHI storage with audit trail

PostgreSQL stores all ePHI (notes, patients, providers). The `Notes` table uses JSONB columns for `StructuredContent`, `BillingCodes`, and `AuditTrail` — this gives schema flexibility for multiple note formats while keeping all data in one encrypted database.

Every mutation to a note appends an `AuditEntry` to the `AuditTrail` JSONB array with a UTC timestamp, action name, and actor ID. This satisfies HIPAA's Audit Control requirement (§164.312(b)) — a complete, tamper-evident history of who accessed or changed each note.

In production, enable PostgreSQL's `pgaudit` extension and **encryption at rest** (either filesystem-level encryption or Transparent Data Encryption via the hosting platform).

### JWT Authentication — stateless, provider-scoped access

JWTs are signed with HMAC-SHA256. The token contains the provider's ID, which every controller extracts and uses to scope every query — a provider physically cannot query another provider's patients or notes, even with a valid token. This enforces HIPAA's Access Control requirement (§164.312(a)(1)) at the query level, not just the route level.

Tokens expire in 30 days. For stricter environments, reduce this and implement refresh tokens.

### bcrypt Password Hashing

Provider passwords are stored as bcrypt hashes (cost factor 11) using `BCrypt.Net`. bcrypt is intentionally slow and resistant to GPU-accelerated cracking — the standard for healthcare credential storage. Plaintext passwords are never stored, logged, or returned in any API response.

### Hangfire — durable job queue for long AI pipelines

A Whisper transcription can take 30–60 seconds. Running this synchronously in an HTTP request would time out on mobile. Hangfire persists jobs in PostgreSQL, so even if the server crashes mid-transcription, the job is retried on restart. This prevents data loss and ensures the pipeline always completes.

Hangfire's job queue also serializes access to the LLM. Because LLamaSharp is not thread-safe, only one inference runs at a time (2-worker pool, but the `SemaphoreSlim` inside `LlamaStructuringService` ensures sequential inference).

### SignalR — real-time progress without polling

Polling the server every second for job status wastes bandwidth and creates unnecessary HTTP request logs (which contain PHI-adjacent metadata). SignalR uses a persistent WebSocket, so the server pushes updates only when something actually changes. The connection is authenticated via JWT before any data flows.

### S3-compatible Object Storage — presigned URLs for PHI exports

Exported PDFs contain full clinical notes — they are ePHI and must be protected in transit and at rest. `S3StorageService` uploads PDFs to a private bucket (no public ACL). Access is granted only via **time-limited presigned URLs** (1 hour by default). This means:

- No PHI is embedded in a permanent public URL
- The link cannot be shared indefinitely — it expires
- The storage bucket itself remains private

For local development, **MinIO** is used (zero cost, runs in Docker, fully S3-compatible). For production, **Cloudflare R2** (no egress fees) or **Backblaze B2** are recommended. Both support S3-compatible presigned URLs with the same API. Switching providers requires only a config change — no code change.

In production, enable **server-side encryption** on the bucket (AES-256, which `S3StorageService` already requests via `ServerSideEncryptionMethod.AES256`).

### QuestPDF — PDF generation without third-party rendering services

PDF rendering services send document content to an external server. QuestPDF renders PDFs entirely in-process (MIT/Community license for non-commercial use). The generated PDF includes the full note, billing codes, audit trail, and provider signature block — everything needed for a compliant clinical record.

---

## Billing Codes and Clinical Notes

US healthcare billing requires two code systems on every insurance claim. Anamnys derives both automatically from the structured note content.

### CPT Codes — what was done

CPT (Current Procedural Terminology) codes describe the service the provider performed. The billing engine selects the appropriate code based on note content:

| CPT     | Description               | Trigger                                                     |
| ------- | ------------------------- | ----------------------------------------------------------- |
| `90837` | Psychotherapy, 60 minutes | Note contains "60 min", "60-minute", or "one hour"          |
| `90834` | Psychotherapy, 45 minutes | Default for mental health sessions                          |
| `97110` | Therapeutic exercises     | Note mentions exercise, strengthening, or stretching        |
| `97140` | Manual therapy            | Note mentions manual therapy, mobilization, or manipulation |
| `97530` | Therapeutic activities    | Fallback when no specific PT intervention detected          |

### ICD-10 Codes — why it was medically necessary

ICD-10 codes describe the diagnosis that justifies the service. Every insurance claim needs both a CPT and an ICD-10 that make clinical sense together — a mismatch (e.g. a PT exercise code billed against a psychiatric diagnosis) triggers an automatic denial.

The billing engine currently maps to example ICD-10 codes (`F33.1` for MDD, `M54.5` for low back pain). In production this is extended with a payer rules database where each provider's active diagnoses from the patient record drive the ICD-10 selection.

### Denial Risk Score

Every billing code gets a denial risk score (0–100) estimating the probability an insurer will reject the claim. The score rises when the note is missing documentation that payers require:

| Missing documentation                | Risk increase      |
| ------------------------------------ | ------------------ |
| No diagnosis statement               | +30 points         |
| No treatment plan or goal update     | +30 points         |
| PT: missing time units for 97110     | flagged as warning |
| PT: missing GP modifier for Medicare | flagged as warning |

The mobile app displays this score color-coded on `BillingCodeCard` — green (<25), amber (25–49), red (≥50) — so the provider can correct the note before signing. A signed note with a high denial risk is still valid; the score is advisory only.

### Missing Documentation Warnings

When the billing engine detects that required fields are absent, it populates `MissingDocumentation` on each `BillingCode`. These surface in the mobile UI and are printed in the exported PDF in a highlighted warning block. Common examples:

- "Diagnosis statement required for medical necessity"
- "Treatment plan or goal update required"
- "Document time units for 97110"
- "Ensure GP modifier appended for Medicare"

### Multi-Country Billing System

Anamnys supports four billing systems out of the box, selected per provider via the `BillingSystem` field on the `Provider` entity. The pipeline reads this field at job enqueue time and passes it through to the billing engine — no code changes needed to switch a provider's country.

| BillingSystem   | Country                 | Procedure Codes                          | Diagnosis Codes               |
| --------------- | ----------------------- | ---------------------------------------- | ----------------------------- |
| `UsCpt`         | United States           | CPT (AMA)                                | ICD-10-CM                     |
| `CanadaOhip`    | Canada (OHIP baseline)  | K029/K030/K031 (MH), P001/P002/P003 (PT) | ICD-10-CA                     |
| `BrazilTuss`    | Brazil (private health) | TUSS 8-digit codes                       | CID-10                        |
| `EuropeGeneric` | Europe (generic)        | SNOMED CT procedure concepts             | ICD-10 national modifications |

**Architecture** — the routing pattern uses a strategy interface (`IBillingEngineStrategy`) with one implementation per country. `BillingEngineRouter` implements the public `IBillingEngine` interface and dispatches based on `BillingSystem`. Adding a new country requires only a new strategy class — no changes to the pipeline, controllers, or interface.

**Canadian specifics** — OHIP K-codes distinguish session duration at three thresholds (≤45 min, 46–75 min, 76+ min). Provincial consent documentation and treatment goal recording are flagged as requirements. Other provinces (BC MSP, AB Health, RAMQ) use equivalent schedule structures.

**Brazilian specifics** — TUSS codes are 8-digit ANS-standardized identifiers required for all private health insurer (operadora) submissions. CID-10 primary and secondary diagnoses are mandatory. The engine also flags ANS pre-authorization (prévia autorização) requirements for extended treatment courses. Eletroterapia (electrotherapy) is detected and coded separately — it's commonly co-billed in Brazilian PT practice. SUS (public system, SIGTAP codes) is not yet implemented.

**European specifics** — The generic engine uses SNOMED CT procedure concepts as a common denominator across national systems. Country-specific modifiers are detected from language indicators in the note (NHS/IAPT → UK, Krankenkasse/GKV → Germany, Sécurité Sociale → France). GDPR consent documentation is flagged as a requirement for all European providers.

**Test credentials for each billing system** (seeded by `seed_dev.sql`):

| Provider         | Email                           | Billing System |
| ---------------- | ------------------------------- | -------------- |
| Dr. Sarah Mendez | `sarah.mh@clinicaldraft.local`  | US CPT         |
| Dr. James Okafor | `james.pt@clinicaldraft.local`  | US CPT         |
| Dr. Ana Silva    | `ana.ca@clinicaldraft.local`    | Canada OHIP    |
| Dr. Carlos Souza | `carlos.br@clinicaldraft.local` | Brazil TUSS    |
| Dr. Marie Dupont | `marie.eu@clinicaldraft.local`  | Europe Generic |

All use password `Dev1234!`.

### Current Limitations and Phase 2

The current billing engine uses keyword heuristics over the structured note text. A production-grade implementation would:

- Use a payer rules database (Medicare, Medicaid, commercial payers have different documentation requirements, unit limits, and modifier rules)
- Pull the patient's active diagnoses directly from the patient record to auto-select ICD-10 codes
- Support the `payerName` parameter already present in `IBillingEngine.DeriveCodesAsync` to apply payer-specific rules
- Detect session duration from the audit trail timestamps rather than relying on text mentions

---

## Note Pipeline — Step by Step

```
Provider submits audio (POST /api/transcribe)
    │
    ├─ 1. Save audio to /temp/{guid}.wav
    ├─ 2. Create Note(status: Processing) in DB
    ├─ 3. Enqueue PipelineJob → Hangfire
    └─ 4. Return 202 {jobId}

Background: PipelineJob.ExecuteAsync(jobId, noteId, audioPath, format, specialty)
    │
    ├─ Step 1: WhisperTranscriptionService.TranscribeAsync(audioPath)
    │           → note.RawTranscript = "Patient reports..."
    │           → push SignalR: JobProgress 30%
    │
    ├─ Step 2: LlamaStructuringService.StructureAsync(transcript, DAP, MentalHealth)
    │           → note.StructuredContent = { Data: "...", Assessment: "...", Plan: "..." }
    │           → push SignalR: JobProgress 60%
    │
    ├─ Step 3: Entity extraction (stub — Phase 2)
    │           → push SignalR: JobProgress 65%
    │
    ├─ Step 4: BillingEngineService.DeriveCodesAsync(structuredNote, MentalHealth)
    │           → note.BillingCodes = [{ CPT: "90837", DenialRisk: 15 }]
    │           → push SignalR: JobProgress 85%
    │
    ├─ Step 5: QA / hallucination guard (stub — Phase 2)
    │           → push SignalR: JobProgress 90%
    │
    ├─ note.Status = ReadyForReview
    ├─ AuditTrail.Add("ai_draft", actorId: Guid.Empty)
    └─ push SignalR: JobComplete { noteId }

Provider reviews note (GET /api/notes/{id})
    └─ edits sections → PATCH /api/notes/{id}   (AuditTrail: provider_edit)
    └─ reviews billing codes + denial risk
    └─ signs → POST /api/notes/{id}/sign         (AuditTrail: signed, status: Signed)

Provider exports → POST /api/notes/{id}/export
    ├─ QuestPDF: render note + billing + audit trail → PDF bytes
    ├─ S3StorageService.UploadAsync → MinIO/R2 bucket
    ├─ GetPresignedUrlAsync → 1-hour expiring URL
    ├─ note.Status = Exported
    └─ AuditTrail.Add("exported")
```

---

## Project Structure

```
clinical-draft-api/
├── src/
│   ├── Anamnys.Domain/           # Entities, Enums — no dependencies
│   │   ├── Entities/
│   │   │   ├── Note.cs                 # Note, StructuredNote, BillingCode, AuditEntry
│   │   │   ├── Patient.cs
│   │   │   └── Provider.cs
│   │   └── Enums/                      # NoteStatus, NoteFormat, Specialty, InputMode
│   │
│   ├── Anamnys.Application/      # Use-case interfaces + CQRS commands
│   │   ├── Interfaces/
│   │   │   ├── ITranscriptionService.cs
│   │   │   ├── INoteStructuringService.cs
│   │   │   ├── IBillingEngine.cs
│   │   │   └── IStorageService.cs
│   │   ├── Commands/
│   │   │   ├── TranscribeAudio/
│   │   │   └── StructureNote/
│   │   └── DTOs/                       # API response shapes
│   │
│   ├── Anamnys.Infrastructure/   # Implements interfaces — all I/O
│   │   ├── Data/
│   │   │   ├── AppDbContext.cs         # EF Core context (JSONB columns)
│   │   │   ├── AppDbContextFactory.cs  # Design-time factory for migrations
│   │   │   └── DevDataSeeder.cs        # Seeds test provider on startup (dev only)
│   │   └── Services/
│   │       ├── WhisperTranscriptionService.cs   # Whisper.net STT
│   │       ├── LlamaStructuringService.cs        # LLamaSharp note structuring
│   │       ├── BillingEngineService.cs           # CPT/ICD-10 rule engine
│   │       └── S3StorageService.cs               # S3-compatible file storage
│   │
│   └── Anamnys.Api/              # ASP.NET Core host — wires everything together
│       ├── Controllers/
│       │   ├── AuthController.cs       # Login, /me
│       │   ├── TranscribeController.cs # Audio/text submission → Hangfire
│       │   ├── NotesController.cs      # CRUD, sign, export
│       │   └── PatientsController.cs   # Patient list/detail
│       ├── Hubs/
│       │   ├── ProgressHub.cs          # Pipeline progress events (batch)
│       │   └── TranscriptionHub.cs     # Real-time audio streaming (live)
│       ├── Jobs/
│       │   └── PipelineJob.cs          # Hangfire job — 5-step AI pipeline
│       ├── Pdf/
│       │   └── NotePdfDocument.cs      # QuestPDF clinical note document
│       ├── Program.cs                  # DI wiring, middleware, startup
│       ├── appsettings.json
│       └── appsettings.Development.json
│
├── scripts/
│   └── seed_dev.sql                    # SQL seed: 2 providers, 10 patients, 2 signed notes
│
├── docker-compose.yml                  # PostgreSQL + MinIO + API
├── Anamnys.sln
└── README.md
```

---

## Running Locally

**Prerequisites:** .NET 10 SDK, PostgreSQL 16, Docker (for MinIO)

```bash
# 1. Start MinIO (local S3-compatible storage)
docker compose up minio minio-init -d

# 2. Set environment (or use appsettings.Development.json)
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=clinical_draft_dev;Username=postgres;Password=postgres"

# 3. Run migrations and start API
cd src/Anamnys.Api
dotnet run
```

API runs at `http://localhost:5000`. Swagger UI at `http://localhost:5000/swagger`. Hangfire dashboard at `http://localhost:5000/jobs`. MinIO console at `http://localhost:9001`.

---

## Environment Configuration

| Key                         | Description                                                      | Dev default                           |
| --------------------------- | ---------------------------------------------------------------- | ------------------------------------- |
| `ConnectionStrings:Default` | PostgreSQL connection string                                     | `Host=localhost;...`                  |
| `Jwt:Key`                   | HMAC-SHA256 signing key (min 32 chars)                           | `dev-secret-key-...`                  |
| `Whisper:ModelPath`         | Path to GGML model file (must be multilingual — no `.en` suffix) | `../models/ggml-medium.bin`           |
| `Whisper:ModelType`         | GGML model type (for auto-download)                              | `Medium`                              |
| `Llama:ModelPath`           | Path to GGUF model file                                          | `../models/mistral-7b-v0.1.Q2_K.gguf` |
| `Llama:GpuLayerCount`       | GPU layers offloaded (0 = CPU only)                              | `0`                                   |
| `Storage:ServiceUrl`        | S3-compatible endpoint                                           | `http://localhost:9000`               |
| `Storage:AccessKey`         | Storage access key                                               | `minioadmin`                          |
| `Storage:SecretKey`         | Storage secret key                                               | `minioadmin`                          |
| `Storage:BucketName`        | Target bucket name                                               | `clinical-draft-audio`                |

**Never commit real credentials.** Use environment variables or a secrets manager in production.

---

## Database Migrations

Migrations run automatically on startup (`db.Database.MigrateAsync()`).

To create a new migration manually:

```bash
cd src/Anamnys.Infrastructure
dotnet ef migrations add <MigrationName> \
  --startup-project ../Anamnys.Api \
  --output-dir Data/Migrations
```

`AppDbContextFactory` handles design-time context creation — it reads connection strings from the Api project's `appsettings.json` so the EF tools work without a running app.

---

## Test Credentials

Seeded by `scripts/seed_dev.sql` or `DevDataSeeder` on first startup:

| Role                      | Email                          | Password   |
| ------------------------- | ------------------------------ | ---------- |
| Mental Health Provider    | `sarah.mh@clinicaldraft.local` | `Dev1234!` |
| Physical Therapy Provider | `james.pt@clinicaldraft.local` | `Dev1234!` |

Dev-only seeder (`dev@clinicaldraft.local`) is also created by `DevDataSeeder` if no providers exist.
