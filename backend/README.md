# Anamnys AI — Backend API

ASP.NET Core 10 backend for Anamnys, a clinical note drafting system for solo psychologists and small mental-health practices in Brazil.

---

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [How the Pieces Communicate](#how-the-pieces-communicate)
- [Technology Choices and Legal Basis](#technology-choices-and-legal-basis)
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

## Technology Choices and Legal Basis

Anamnys serves psychology practices in Brazil. The LGPD (Lei nº 13.709/2018) classifies health data as *dado pessoal sensível* and subjects it to the law's strictest regime; the CFP resolutions govern what a clinical record must contain and how long it must be kept — 001/2009 (registration, five-year minimum), 006/2019 (required elements of written documents), 09/2024 (technology-mediated practice), 13/2022 (session recording). Professional confidentiality under the Código de Ética Profissional do Psicólogo applies over all of it.

Under the LGPD the practice is the **controlador** of its patients' data and Anamnys is the **operador**, a relationship formalised in an operator contract with obligations of security, confidentiality, retention, and return or deletion at termination. Every technology choice below exists to keep that processing chain as short as possible and to make each remaining link documentable.

### Self-hosted AI (Whisper + LLaMA) — no clinical content leaves the server

This is the most important architectural decision. Cloud AI APIs (OpenAI, Google, Azure OpenAI) require sending patient audio and clinical notes to a third-party server. That adds a **suboperador** to the processing chain, places sensitive health data outside Brazilian jurisdiction, and — under most standard terms of service — permits the content to be used for model training.

By running Whisper.net and LLamaSharp locally, clinical content never leaves our infrastructure. There is no AI vendor in the chain at all: nothing to contract, nothing to audit, no subprocessor to disclose. The models are static — there is no continuous-learning mechanism in the system, and training use is contractually barred besides.

Whisper (MIT license) handles speech-to-text. LLamaSharp with Phi-4 14B (or Mistral 7B for dev) handles note structuring. Both run as in-process .NET services — no sidecar, no network call, no API key.

### PostgreSQL — structured clinical storage with audit trail

PostgreSQL stores all clinical data (notes, patients, providers). The `Notes` table uses JSONB columns for `StructuredContent`, `BillingCodes`, and `AuditTrail` — this gives schema flexibility for multiple note formats while keeping all data in one encrypted database.

Every mutation to a note appends an `AuditEntry` to the `AuditTrail` JSONB array with a UTC timestamp, action name, and actor ID. This serves the LGPD's accountability principle (art. 6, X) and produces exactly the evidence a CRP disciplinary proceeding or a judicial request would ask for: who drafted, who edited, what changed, and when it was signed. The AI draft and the professional's edits stay distinguishable forever.

Note that the trail is currently a JSONB array on the note, which cannot be queried across notes by date or action. The retention dashboard, the denial history and the per-patient dossier all need it as a table — see the data-model document. Extract it before the first paying subscriber, not after.

In production, enable PostgreSQL's `pgaudit` extension and **encryption at rest** (either filesystem-level encryption or Transparent Data Encryption via the hosting platform).

### JWT Authentication — stateless, provider-scoped access

JWTs are signed with HMAC-SHA256. The token contains the provider's ID, which every controller extracts and uses to scope every query — a provider physically cannot query another provider's patients or notes, even with a valid token. This enforces need-to-know isolation at the query level, not just the route level — the technical half of the operator contract's commitment that our team does not access clinical content in normal operation. Any access outside that path is a support request the professional authorised, and it is recorded in the audit trail where they can see it.

Tokens expire in 30 days. For stricter environments, reduce this and implement refresh tokens.

### bcrypt Password Hashing

Provider passwords are stored as bcrypt hashes (cost factor 11) using `BCrypt.Net`. bcrypt is intentionally slow and resistant to GPU-accelerated cracking. Plaintext passwords are never stored, logged, or returned in any API response.

### Hangfire — durable job queue for long AI pipelines

A Whisper transcription can take 30–60 seconds. Running this synchronously in an HTTP request would time out on mobile. Hangfire persists jobs in PostgreSQL, so even if the server crashes mid-transcription, the job is retried on restart. This prevents data loss and ensures the pipeline always completes.

Hangfire's job queue also serializes access to the LLM. Because LLamaSharp is not thread-safe, only one inference runs at a time (2-worker pool, but the `SemaphoreSlim` inside `LlamaStructuringService` ensures sequential inference).

### SignalR — real-time progress without polling

Polling the server every second for job status wastes bandwidth and creates unnecessary HTTP request logs, which carry metadata about a patient's clinical activity even when they carry no clinical content. SignalR uses a persistent WebSocket, so the server pushes updates only when something actually changes. The connection is authenticated via JWT before any data flows.

### S3-compatible Object Storage — presigned URLs for exported documents

Exported PDFs contain full clinical notes — sensitive personal data under the LGPD, protected in transit and at rest. `S3StorageService` uploads PDFs to a private bucket (no public ACL). Access is granted only via **time-limited presigned URLs** (1 hour by default). This means:

- No clinical content or patient identifier sits behind a permanent public URL
- The link cannot be shared indefinitely — it expires
- The storage bucket itself remains private

For local development, **MinIO** is used (zero cost, runs in Docker, fully S3-compatible). For production, **Cloudflare R2** (no egress fees) or **Backblaze B2** are recommended. Both support S3-compatible presigned URLs with the same API. Switching providers requires only a config change — no code change.

In production, enable **server-side encryption** on the bucket (AES-256, which `S3StorageService` already requests via `ServerSideEncryptionMethod.AES256`).

**Data residency is an open decision.** The commercial proposal states that clinical records are hosted in Brazil. The storage layer is deliberately provider-agnostic, and the production choice that actually satisfies that statement has not been fixed in any document. Settle it before the first real patient record — the claim is already in writing to customers.

### QuestPDF — PDF generation without third-party rendering services

PDF rendering services send document content to an external server. QuestPDF renders PDFs entirely in-process (MIT/Community license for non-commercial use). The generated PDF includes the full note, billing codes, audit trail, and provider signature block — everything the professional needs to attach to a chart, send to an operadora, or produce in an inspection.

---

## Billing Codes and Clinical Notes

Submitting to a Brazilian *operadora* requires two code systems on every guide. Anamnys derives both automatically from the structured note content and presents them **for the professional to confirm** — the codes are a suggestion, never an automatic submission.

### TUSS codes — what was done

TUSS (Terminologia Unificada da Saúde Suplementar) is the 8-digit ANS-standardised procedure terminology required by every private health insurer in Brazil. The billing engine selects a candidate code from the note content, keyed on the recorded modality and session duration.

### CID-10 codes — why it was necessary

CID-10 gives the diagnosis that justifies the procedure. A guide needs a TUSS code and a CID-10 that make clinical sense together; a mismatch is a routine cause of *glosa*. Primary and secondary diagnoses are both mandatory on submission.

> **D-04 — verify before any pilot.** The TUSS codes currently in `BrazilBillingEngine` were written as plausible examples and have **not** been checked against the official ANS table. Do that before a single real guide is generated. This blocks any pilot involving billing.

### Denial Risk Score

Every billing code gets a denial risk score (0–100) estimating the probability an insurer will reject the claim. The score rises when the note is missing documentation that payers require:

| Missing documentation            | Risk increase |
| -------------------------------- | ------------- |
| No diagnosis statement           | +30 points    |
| No treatment plan or goal update | +30 points    |

The mobile app displays this score color-coded on `BillingCodeCard` — green (<25), amber (25–49), red (≥50) — so the provider can correct the note before signing. A signed note with a high denial risk is still valid; the score is advisory only.

### Missing Documentation Warnings

When the billing engine detects that required fields are absent, it populates `MissingDocumentation` on each `BillingCode`. These surface in the mobile UI and are printed in the exported PDF in a highlighted warning block. Common examples:

- "Diagnosis statement required to justify the procedure"
- "Treatment plan or goal update required"

### Routing architecture, and the engines that are not the product

The billing layer uses a strategy interface (`IBillingEngineStrategy`) with one implementation per country. `BillingEngineRouter` implements the public `IBillingEngine` and dispatches on the `BillingSystem` field of `Provider`.

`BrazilTuss` is the product. The enum also carries `UsCpt`, `CanadaOhip` and `EuropeGeneric`, and those strategies remain in the codebase — but they are **not maintained, not documented for customers, and not sold.** Treat them the way `NoteFormat.PtFunctional` is treated: the code path stays, and it appears in no customer-facing text. Do not extend them, and do not cite the router's multi-country capability as a product feature.

`BillingSystem` defaults to `UsCpt` for backward compatibility with the earliest migrations. New providers must be created as `BrazilTuss`; the default is a leftover, not an intention.

**Brazilian specifics** — TUSS codes are 8-digit ANS-standardised identifiers required for every operadora submission. CID-10 primary and secondary diagnoses are mandatory. The engine flags ANS prior authorisation (prévia autorização) for extended treatment courses. SUS (public system, SIGTAP codes) is not implemented and is not planned.

**Test credentials** (seeded by `seed_dev.sql`). Only the `BrazilTuss` account reflects the product; the others exercise strategies that are retained but unmaintained.

| Provider         | Email                           | Billing System            |
| ---------------- | ------------------------------- | ------------------------- |
| Dr. Carlos Souza | `carlos.br@clinicaldraft.local` | Brazil TUSS — **the one** |
| Dr. Sarah Mendez | `sarah.mh@clinicaldraft.local`  | US CPT (legacy fixture)   |
| Dr. James Okafor | `james.pt@clinicaldraft.local`  | US CPT (legacy fixture)   |
| Dr. Ana Silva    | `ana.ca@clinicaldraft.local`    | Canada OHIP (legacy)      |
| Dr. Marie Dupont | `marie.eu@clinicaldraft.local`  | Europe Generic (legacy)   |

All use password `Dev1234!`. The `clinicaldraft.local` domain in these fixtures predates the rename to Anamnys.

### Current Limitations and Phase 2

The current billing engine uses keyword heuristics over the structured note text. A production-grade implementation would:

- Use an operadora rules database — each insurer sets its own documentation requirements, session limits and authorisation rules
- Pull the patient's active diagnoses directly from the chart to select CID-10 codes
- Support the `payerName` parameter already present in `IBillingEngine.DeriveCodesAsync` to apply per-operadora rules
- Take session duration from the `session` entity (F-04) rather than from text mentions, which is what makes the duration-versus-code check in F-20 possible
- Generate the submission-ready TISS guide itself (F-19), not just the codes

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
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=anamnys_dev;Username=postgres;Password=postgres"

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
| Legacy fixture provider   | `james.pt@clinicaldraft.local` | `Dev1234!` |

Dev-only seeder (`dev@clinicaldraft.local`) is also created by `DevDataSeeder` if no providers exist.
