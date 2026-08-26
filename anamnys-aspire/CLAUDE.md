# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this directory is

`anamnys-aspire/` is **the application** — a single Aspire-orchestrated solution, committed
to git. All new work happens here.

```
anamnys/                     <- git root (branch: develop)
├── backend/                 <- LEGACY reference only (do not extend)
├── frontend/                <- LEGACY reference only (Next.js; do not extend)
└── anamnys-aspire/          <- THIS DIR: the real project
    ├── anamnys-aspire.AppHost/   AppHost.cs — resource graph
    ├── anamnys-aspire.Server/    minimal API + Extensions.cs (service defaults)
    ├── frontend/                 Vite + React + TanStack Router SPA
    └── design/specs/             design documents
```

`backend/` and `frontend/` are the original project structure. They are kept for reference
while their functionality is **gradually migrated** into this directory. Do not add features
to them. Their code is a source to port *from*, not a place to work.

Note there are two `frontend/` directories. `anamnys-aspire/frontend` (Vite SPA) is the live
one; `../frontend` (Next.js) is legacy. Never confuse them.

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
- **Everything self-hosted.** The same rule governs every dependency that touches PHI:
  database, identity provider, object storage. Managed/hosted services are not an option
  for PHI-bearing components without a signed BAA.
- **Provider-scoping is enforced at the query level.** The authenticated user's id is
  resolved server-side and appended to every database query, so a provider cannot read
  another provider's patients or notes even with a valid session. This is the system's
  primary access control — any new data-access code must preserve it.
- Every note mutation appends an `AuditEntry` to the `AuditTrail` JSONB column (§164.312(b)).
- Exported PDFs are ePHI: private bucket, access only via 1-hour presigned URLs.
- **The application never stores credentials.** Keycloak owns passwords, hashing, reset
  flows, and lockout. There is no password column in the application database.
- **No PHI in tokens, logs, or URLs.** Tokens carry subject, roles, and scopes only.

## Tech Stack

Split into what is **wired up today** and what is **planned**. Do not write code against a
planned package without adding the reference first.

### Backend — present

- .NET 10, ASP.NET Core Minimal APIs, C# 14
- Redis (Aspire-managed container) for output caching
- OpenTelemetry via `Extensions.cs` service defaults

### Backend — planned

- Entity Framework Core 10.0
- PostgreSQL 18 — runs as an **Aspire-managed Docker container**, both locally and in
  production. Not a hosted service (see HIPAA constraints).
- Keycloak for identity — see `design/specs/2026-08-24-authentication-design.md`
- FluentValidation for request validation
- Scalar for OpenAPI documentation
- xUnit + FluentAssertions for testing

### Frontend — present

- React 19, Vite 8
- TanStack Router (file-based routing)
- Tailwind CSS 4
- TypeScript **6.0.3** — see the TypeScript version note below

### Frontend — planned

- TanStack Query
- Shadcn UI components

## Common Commands

**Docker must be running** — the AppHost starts container-backed resources (Redis today,
Postgres and Keycloak later).

### Aspire (from this directory)

```bash
aspire start        # background start — the form agents should use
aspire run          # foreground + dashboard — for a human at the terminal
aspire stop         # do this BEFORE any rebuild
aspire doctor
```

A running Aspire app holds file locks on `bin/` and `obj/`. An `MSB3491` or `CS2012` during
a build means "run `aspire stop` first" — not that the project is broken.

### Frontend (from `frontend/`)

```bash
npm run dev     # vite
npm run build   # tsc -b && vite build
npm run lint    # eslint
```

## Architecture

- Clean Architecture for the backend API.
- The SPA is served by the .NET server. `AddViteApp` + `PublishWithContainerFiles` bakes the
  built assets into the server's `wwwroot`, so there is **one container in production** and
  the SPA is same-origin with the API. This is deliberate: it is the only frontend shape in
  Aspire 13.5.2 with **zero experimental APIs**, it keeps PHI off any Node runtime, and
  same-origin is what makes the cookie-based auth design work. Do not replace it with an
  SSR framework without revisiting all three consequences.
- Authentication uses a **Backend-for-Frontend**: the .NET server holds tokens, the browser
  holds only an `HttpOnly` `SameSite=Strict` session cookie. Tokens never enter JavaScript.

## Gotchas

- **`frontend/src/routeTree.gen.ts` is generated but MUST stay committed.** `npm run build`
  runs `tsc -b` *before* Vite generates the route tree, so a clean checkout fails without
  it. If it is ever missing, regenerate with a bare `npx vite build`.
- **Aspire CLI version.** `AspireUseCliBundle` makes the launcher run a bundle matching the
  AppHost SDK version. Trust `aspire --version`, never the Homebrew install path.
- **TypeScript is held at 6.0.3, not the latest 7.0.2.** No released or canary
  `typescript-eslint` supports TypeScript 7 (peer range is `>=4.8.4 <6.1.0`), so upgrading
  breaks `npm run lint`. The `package.json` range is `~6.0.3` (patch-only) rather than
  `^6.0.3` on purpose — a caret would let `npm install` pull 6.1+ and silently re-break
  linting. Widen it only once typescript-eslint raises its peer range.
- **`anamnys-aspire/frontend/obj*` is gitignored.** The esproj SDK emits a directory whose
  name contains a literal backslash on macOS. Committing it would make `git clone` fail on
  Windows, where `\` is an illegal filename character.
- **Aspire's `WithRealmImport` (Keycloak) is development-only** and is silently dropped by
  `aspire publish`/`deploy`. Production realm seeding needs a custom image.

## Workflow

- Branch naming: `feature/`, `bugfix/`, `hotfix/`
- Commit format: `type: description` (feat, fix, refactor, test, docs, chore)
- Always create a branch before changes
- Run tests before committing

### Creating/Modifying API Endpoints

1. Plan the endpoint changes — new/updated methods, paths, request payloads
2. Confirm proposed changes with the user
3. Implement the endpoint
4. Add/update the endpoint in `anamnys-aspire.Server/anamnys-aspire.Server.http`, documenting
   the endpoint and its payloads
5. Run the requests in that `.http` file against a running AppHost to verify

## Code Style

- General:
  - Prefer writing clear code and use inline comments sparingly
- C#:
  - 4-space indent
  - `PascalCase` for classes/methods
  - `_camelCase` for private fields
  - `camelCase` for local variables, parameters
  - Primary constructors, including for DI
  - File-scoped namespaces
  - Always pass `CancellationToken` to async methods
  - Use records for complex incoming request parameters
  - Use auto-properties, and `field` if necessary
  - No XML documentation comments
- Tests:
  - `<ClassName>Tests` for test class
  - `<MethodName>_<Conditions>_<AssertedOutcome>` for test methods (never `Async` suffix)
  - Arrange, Act, Assert pattern (comment each section in method)
- TypeScript/JavaScript/CSS:
  - 2-space indent (matches the existing code)
  - Keep `*.test.ts` files in the same directory as the corresponding `*.ts` file

### Patterns We DON'T Use (Never Suggest)

- Repository pattern (use EF Core directly)
- AutoMapper (write explicit mappings)
- Exceptions for business logic errors
- Stored procedures
