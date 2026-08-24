# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this directory is

`anamnys-aspire/` is a **fresh, still-untracked `aspire init` scaffold** — not yet the real
application. It contains the stock Aspire starter (a `WeatherForecast` minimal API and a
blank Vite/React page). The actual product lives in two sibling directories of the same git
repo:

```
anamnys/                     <- git root (branch: develop)
├── backend/                 <- REAL API: ASP.NET Core 10, Clean Architecture
├── frontend/                <- REAL UI: Next.js 16 + React 19 + Tailwind 4
└── anamnys-aspire/          <- THIS DIR: Aspire scaffold (untracked)
    ├── anamnys-aspire.AppHost/   AppHost.cs — resource graph
    ├── anamnys-aspire.Server/    template minimal API + Extensions.cs (service defaults)
    └── frontend/                 template Vite app (NOT the real frontend)
```

The intended trajectory is to wire this AppHost to orchestrate `../backend` and
`../frontend`. Until that happens, **`anamnys-aspire/frontend` and `../frontend` are
different apps** (Vite vs Next.js) — never confuse them. Same for the two `Server`/`Api`
projects.

## The product

Anamnys is a **specialty-native clinical note drafting system** for solo/small-group mental
health and physical therapy practices. Provider dictates audio → Whisper transcribes →
LLaMA structures into a clinical note (SOAP/DAP) → billing engine derives CPT/ICD-10 codes
with a denial-risk score → provider reviews, signs, exports to PDF.

### HIPAA constraints — treat as hard requirements

- **All AI inference runs in-process on the same server.** Whisper.net (STT) and LLamaSharp
  (structuring) are .NET libraries, not sidecars. **Never** propose routing patient audio,
  transcripts, or notes to a cloud AI API (OpenAI, Azure OpenAI, Gemini, Anthropic) — doing
  so puts PHI outside the network boundary and requires a BAA.
- Every controller extracts the provider id from the JWT `sub` claim and appends it to every
  DB query. Provider-scoping is enforced at the **query** level, not just the route level.
  Any new data-access code must preserve this.
- Every note mutation appends an `AuditEntry` to the `AuditTrail` JSONB column (§164.312(b)).
- Exported PDFs are ePHI: private bucket, access only via 1-hour presigned URLs.
- Passwords are bcrypt (cost 11). Never log, store, or return plaintext.

## Backend architecture (`../backend`)

Four projects, strict dependency direction (`Anamnys.sln`):

| Project | Role | Depends on |
| --- | --- | --- |
| `Anamnys.Domain` | Entities (`Note`, `Patient`, `Provider`), enums | nothing |
| `Anamnys.Application` | Interfaces, CQRS commands, DTOs | Domain |
| `Anamnys.Infrastructure` | EF Core `AppDbContext`, Whisper/LLaMA/Billing/S3 services | Application |
| `Anamnys.Api` | Controllers, SignalR hubs, Hangfire jobs, QuestPDF | all |

Infrastructure implements the interfaces Application declares. Keep I/O out of
Domain/Application.

### The pipeline

`POST /api/transcribe` saves audio, creates a `Note(Processing)`, enqueues a Hangfire
`PipelineJob`, returns `202 {jobId}` immediately. The job runs 5 steps (transcribe →
structure → entity extraction *(stub)* → billing → QA guard *(stub)*), pushing SignalR
progress to the `jobId` group at each step, then sets `ReadyForReview`.

- **Hangfire is not optional plumbing** — it persists to PostgreSQL so a mid-transcription
  crash retries rather than losing work, and it serializes LLM access.
- **LLamaSharp is not thread-safe.** `LlamaStructuringService` guards inference with a
  `SemaphoreSlim`. Do not parallelize inference or raise worker count expecting speedup.
- Two hubs: `/hubs/progress` (job events, group-per-job) and `/hubs/transcription` (live
  dictation, binary chunks buffered per-connection until `FinalizeStream`).
- SignalR WebSocket upgrades cannot send an `Authorization` header, so `Program.cs` has an
  `OnMessageReceived` handler reading the JWT from `?access_token=`. This is intentional —
  don't "fix" it.

### Billing engine

`IBillingEngineStrategy` per country; `BillingEngineRouter` implements `IBillingEngine` and
dispatches on `Provider.BillingSystem` (`UsCpt`, `CanadaOhip`, `BrazilTuss`, `EuropeGeneric`).
**Adding a country = adding one strategy class.** No changes to the pipeline, controllers, or
interface. Current code selection is keyword heuristics over structured note text —
acknowledged as Phase 1.

## Running things

### Aspire (this directory)

```bash
aspire start        # background start — the form agents should use
aspire run          # foreground + dashboard — for a human at the terminal
aspire stop         # do this BEFORE any rebuild
aspire doctor
```

Use the Aspire CLI, not `dotnet run` on the AppHost. A running Aspire app holds file locks on
`bin/`/`obj/`; `MSB3491` or `CS2012` during a build means "run `aspire stop` first", **not**
that the project is broken.

`aspire.config.json` points the CLI at the AppHost csproj. An `aspire` MCP server is wired up
in `.mcp.json`, `.vscode/mcp.json`, and `opencode.jsonc` — prefer its tools (`list_resources`,
`list_console_logs`, `list_traces`) over shelling out for orchestration state.

**CLI version resolution:** the AppHost sets `<AspireUseCliBundle>true</AspireUseCliBundle>`,
so the `aspire` launcher downloads a bundle matching the AppHost SDK version and runs that —
CLI and packages stay in lockstep automatically (both 13.5.2 today). The Homebrew cask
directory name (`Caskroom/aspire/13.4.6/`) is the launcher's install version and does **not**
reflect what runs; trust `aspire --version`, not the path.

`.agents/skills/` holds Microsoft's Aspire workflow skills (`aspire`, `aspireify`,
`aspire-orchestration`, `aspire-deployment`, `aspire-monitoring`, `dotnet-inspect`,
`playwright-cli`). Claude Code does **not** auto-load these — read the relevant `SKILL.md`
directly when doing Aspire work, especially `aspireify/SKILL.md` before wiring the real apps
into the AppHost.

### Backend standalone (`../backend`)

```bash
docker compose up minio minio-init -d     # only MinIO is uncommented; postgres/api are not
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=clinical_draft_dev;Username=postgres;Password=postgres"
cd src/Anamnys.Api && dotnet run
```

API `:5000` · Swagger `/swagger` · Hangfire dashboard `/jobs` · MinIO console `:9001`.
PostgreSQL 16 must be running separately — its compose service is commented out.

Seed data: `scripts/seed_dev.sql` (5 providers, one per billing system; password `Dev1234!`).

### Real frontend (`../frontend`)

```bash
npm run dev     # next dev
npm run build
npm run lint
```

`frontend/AGENTS.md` (imported by `frontend/CLAUDE.md`) warns that **Next.js 16 has breaking
changes vs. training data** — read `node_modules/next/dist/docs/` before writing Next code
there. That block is regenerated by `next dev`; commit it rather than reverting it.

API layer lives in `src/api/` (axios client + per-domain modules), with `src/api-mock/` as a
parallel mock implementation. Route groups: `(app)`, `(auth)`, `(marketing)`.
State: Zustand + TanStack Query. SignalR via `@microsoft/signalr`. UI: shadcn + Base UI.

## Conventions

- .NET 10 across every project; `ImplicitUsings` and `Nullable` enabled.
- Config keys use the double-underscore env form (`ConnectionStrings__Default`,
  `Jwt__Key`, `Storage__AccessKey`). See `backend/.env.example` — never commit real values.
- AI model files (`.bin`, `.gguf`) live outside the repo under `models/` and are bind-mounted
  or path-referenced via `Whisper:ModelPath` / `Llama:ModelPath`.
- `mobile/` and `docs/` are gitignored at the repo root; a React Native client is referenced
  throughout the backend README but is not in this tree.
