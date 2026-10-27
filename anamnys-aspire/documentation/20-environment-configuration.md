# Chapter 20: Environment Configuration Checklist

Everything that has to be configured per environment, in one place, so nothing is forgotten
when a new machine, staging or production is set up. Last checked against the code on
2026-10-18 (branch `feature/psychometric-scales`).

## How environments differ in this codebase

The code knows only two cases:

- **Development** (`ASPNETCORE_ENVIRONMENT=Development`, a local `aspire run` / `aspire start`):
  dev seed users and test clients are baked into Keycloak, Mailpit catches e-mail, every
  container publishes a localhost port, and the production startup gates are off.
- **Everything else** (Staging, Production, any other name): the startup gates are on, the
  dev seed is stripped from the Keycloak image (`INCLUDE_DEV_SEED` is only `true` in
  Development), HTTPS metadata is required, and `/health` / `/alive` are not mapped.

So **staging is configured exactly like production**, with its own values. Never share a
secret, key, certificate, bucket, database or Keycloak realm data between staging and
production.

---

## 1. Development (each developer machine)

| What | How | Notes |
|---|---|---|
| Docker running | — | Redis, Postgres, Keycloak, SeaweedFS and Mailpit are containers. |
| Apache Maven | install | `keycloakify build` shells out to `mvn` for the login theme. |
| Keycloak theme jar | `npm run build-keycloak-theme -w @anamnys/keycloak-theme` | The Keycloak image build fails without it (on purpose). |
| `Parameters:provider-client-secret` | `dotnet user-secrets --project anamnys-aspire.AppHost set ... "$(openssl rand -hex 32)"` | Generate your own; never copy a teammate's. |
| `Parameters:patient-client-secret` | same | |
| `Parameters:owner-client-secret` | same | |
| `Parameters:keycloak-admin-password` | same | |
| `Parameters:storage-access-key` | same, `openssl rand -hex 16` | Hex/alphanumeric only (written into JSON unescaped). |
| `Parameters:storage-secret-key` | same, `openssl rand -hex 32` | |
| `Parameters:resend-api-key` | optional user secret | Only to receive real e-mail in your own inbox (Resend sandbox delivers only to the Resend account's address). Without it Mailpit is used. |
| `Parameters:storage-public-url` | optional user secret | Only to test PDF downloads through a tunnel; default is the localhost endpoint. |
| Database migrations | apply each new file in `design/migrations/` to the dev Postgres (command in each file's header) | The server bootstraps the schema only on an empty database; existing dev databases need every new migration, in date order. Current list: 2026-10-01 … 2026-10-17-note-pdf, 2026-10-18-scales, 2026-10-19-documents, 2026-10-20-documents-team, 2026-10-21-theme-vocabulary, 2026-10-23-account-export, 2026-10-24-excerpt-refs. |
| AI models | nothing to set | Whisper and Qwen files download on first use into the default model directories (≈ 4.7 GB for Qwen). |

If the AppHost ever ran before the client secrets were set, remove the Keycloak and Postgres
data volumes once (one `docker volume rm` per volume) — the realm import only runs on an empty
realm. The storage volume (`anamnys-storage-data`) is unrelated to that.

Optional clean-up: objects written to `anamnys-storage-data` before `-s3.encryptVolumeData`
was added are stored unencrypted; recreate that volume to clear them (it only holds test
PDFs in dev).

---

## 2. Staging and production

Each item below is required unless marked optional. "Gate" means the server refuses to
start without it.

### 2.1 AppHost parameters (supplied by the deployment's secret store)

| Parameter | Value | Gate / effect |
|---|---|---|
| `provider-client-secret`, `patient-client-secret`, `owner-client-secret` | new random values per environment | Keycloak substitutes them into the realm at import; the server uses them for the token exchange. |
| `keycloak-admin-password` | new random value | Used by the startup guard that checks the dev-only test clients are gone. |
| `keycloak-admin-username` | optional (default `admin`) | |
| `storage-access-key`, `storage-secret-key` | new random hex values | SeaweedFS identity + server S3 client. |
| `storage-public-url` | `https://<storage host>` (see 2.4) | Gate: outside Development the server refuses to start unless it is an absolute https URL. Publish mode requires it. |

### 2.2 Server settings

| Setting | Value | Gate / effect |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Staging` or `Production` | Turns the gates on. |
| `DataProtection:CertificateThumbprint` | thumbprint of a certificate installed in the server's CurrentUser/My store | Gate. Encrypts the DataProtection keys kept in Redis (they decrypt every session's tokens). |
| `KEYCLOAK_ADMIN_USERNAME`, `KEYCLOAK_ADMIN_PASSWORD`, `KEYCLOAK_ADMIN_BASE_ADDRESS` | admin credential + an absolute URL to Keycloak reachable from the server (not an Aspire `https+http://` service name) | Gate (`DevOnlyTestClientGuard`). |
| `Email:Provider`, `Email:ResendApiKey`, `Email:From` | the production mail provider settings | Without them invitations and appointment e-mails answer 503. Needs a verified sending domain and a processor agreement under the LGPD. |
| `PatientPortal:BaseUrl` | public URL of the patient app (e.g. `https://<host>/patient`) | Gate when an e-mail provider is set. Links in e-mails point here. |
| `Notifications:WorkerEnabled` | `true` (default) | The appointment e-mail worker. |
| `NoteStructuring:WarmSuggestionOnStart` | `true` (default) | Loads the Qwen weights and builds the theme-suggestion cache after startup (≈ 5 GB resident from the start). `false` defers it to the first suggestion (30-60 s wait). |
| `ExternalDocuments:Scanner`, `ExternalDocuments:ClamAvHost`, `ExternalDocuments:ClamAvPort` | `clamav` (default), the clamd host and port (3310) | The antivirus for external documents (2.4b). `fake` is accepted only in Development (the test fixture). The AppHost passes all three (host and port from the `clamav` container; `fake` when the fixture sets it). |
| `DocumentFacts:Extractor`, `DocumentReading:*` | `llama` (default) or `none`; reading limits and Tesseract data (2.4c) | Reading and fact extraction for external documents (chapter 27). |
| `LiveSession:ModelDirectory`, `NoteStructuring:ModelDirectory` | paths on a **persistent volume** | Otherwise every deploy re-downloads Whisper and the two Qwen files (≈ 4.7 GB). |
| `LiveSession:ModelSha256` | verify the committed `medium` hash by computing it locally once | The committed value came from the Hugging Face LFS pointer. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `APPLICATIONINSIGHTS_CONNECTION_STRING` | optional | Telemetry export. Must stay self-hosted or covered by a processor agreement (spans may carry route values). |

### 2.3 Keycloak

- Image built with `INCLUDE_DEV_SEED` unset/false (the default outside Development): dev users,
  test clients and `localhost` redirect URIs are stripped, `sslRequired` becomes `all`.
- **SMTP relay for Keycloak** (verify e-mail, password reset): not built yet — every realm has
  `verifyEmail: true`, so registration dead-ends without it (see `keycloak/dev-only-overrides.jq`).
- TLS in front of Keycloak; the realm redirect URIs must match the real app origins.
- Owners realm: approve staff by assigning a realm role (see CLAUDE.md "Owners-realm
  registration is open; access is not").
- Review the OTP policy (`HmacSHA256`, look-ahead 0) before the first real users enrol;
  changing it later invalidates existing authenticators.

### 2.4 Object storage (SeaweedFS) for exported PDFs

- A **public TLS hostname** for the store (e.g. `arquivos.<domain>`) with a reverse proxy that
  forwards **only** to SeaweedFS's S3 port (8333). This is what `storage-public-url` holds; the
  browser downloads PDFs from it with one-hour presigned links.
- The `anamnys-storage-data` volume on durable storage, **with backups** (2.7).
- Optional: restrict the S3 gRPC port (18333) at the network level — SeaweedFS 4.48 binds it to
  all interfaces and has no flag to move it.

### 2.4b Antivirus for external documents (ClamAV)

- Container `clamav/clamav:1.4.3` (pinned; never `latest`), AppHost resource `clamav`, data volume `anamnys-clamav-data` (`/var/lib/clamav`, signatures), clamd on TCP 3310 reachable only from the server (no public endpoint). Health check: clamd `PING` answers `PONG`. The same container is declared in publish mode.
- **Outbound access** to the ClamAV signature mirror (`database.clamav.net`, HTTPS) for the image's freshclam; the first start downloads the databases and clamd listens only afterwards, so uploads stay `pending` (the scan retries) until it is ready.
- Measured cost (`clamav/clamav:1.4.3`, local run): idle RAM 969 MiB; signatures about 113 MB (108 MiB) on the volume (`main.cvd` 89,072,577 bytes, `daily.cvd` 23,443,876 bytes, plus 0.28 MB of bytecode); image 342 MB (144 MB compressed); INSTREAM scan of a 5 MB PDF 0.47 s, of a 20 MB PDF 1.77 s; the EICAR test file is detected. The first start downloads those signatures (about 113 MB) and takes a few minutes. Reload peak not measured: expect up to about 2x the idle RAM while the daily database reloads. While clamd is unreachable (first start, reload) uploads stay `pending` and the worker keeps retrying with a backoff capped at 5 minutes; those connection failures do not count as scan attempts. A scan that times out does count as an attempt. A file whose scan keeps losing the connection after clamd accepted it is marked `failed` once that has lasted 30 minutes (the worker does the same for no other kind of unavailability).
- Migration `design/migrations/2026-10-25-external-documents.sql` (idempotent) before the new server version.

### 2.4c Document reading (PDFium, Tesseract OCR)

- Clean external documents are read in process: embedded PDF text (PdfPig), and scanned pages rendered with PDFium (PDFtoImage, bblanchon PDFium natives) and OCR'd with Tesseract 5 (Tesseract.CrossPlatform + Tesseract.Native 5.5.2.200). The native libraries come from NuGet (`runtimes/win-x64` and `runtimes/linux-x64`); nothing is installed on the host.
- **Linux loading of Tesseract.CrossPlatform is untested.** It has only been run on Windows. The Linux build may still need system libraries the package does not bundle (for example `libgomp1` for OpenMP). Before the first deploy, run a smoke test in the Linux container: upload a scanned PDF and check that its pages are read with OCR.
- Tesseract Portuguese data: `DocumentReading:TessdataUrl` (tessdata_fast `por.traineddata`, tag 4.1.0) is downloaded on first use to `DocumentReading:TessdataDirectory` (default `%LOCALAPPDATA%/anamnys/tessdata`, use a persistent volume in a container) and verified against `DocumentReading:TessdataSha256`; no hash, no download. This needs **outbound access to raw.githubusercontent.com** on the first OCR. Offline, every read that needs OCR fails its attempts and ends `failed`. These documents do not retry on their own: once the data is reachable, read them again with "Ler de novo". Alternatively, place the verified file in the directory beforehand.
- Limits: `DocumentReading:MaxPages` (20; pages beyond it are flagged), `MinTextCharacters` (20; below it a page is a scan), `RenderDpi` (300), `MaxRenderPixels` (4200; longest side of a rendered page or of an uploaded image before OCR).
- Native libraries on Linux: PDFium, SkiaSharp, Tesseract and Leptonica come from the NuGet packages (`runtimes/linux-x64/native`). Loading has not been tried on Linux; see the smoke-test note above.
- Fact extraction (F-08) uses the Qwen weights already configured under `NoteStructuring:ModelDirectory` and the shared model gate; there is no extra model download. `DocumentFacts:Extractor` is `llama` (default) or `none` (no extraction; read documents get no facts). The AppHost forwards it; the test fixture sets `none`. Extraction takes 47 to 90 s per one- or two-page document on CPU (chapter 27).
- Migration `design/migrations/2026-10-26-document-facts.sql` (idempotent) before the new server version. It adds the read columns, `ExternalDocumentPages`, the fact columns and checks, `ExcerptRefs.PageNumber`, and the audit actions `fact_confirmed`, `fact_corrected`, `fact_discarded`, `facts_extracted` and `facts_deleted`. **Re-run note:** a database that got an earlier version of this file must run it again (every step is guarded). The final version fixes `ExcerptRefs_DocumentPage_ck` (it let a null page through) and adds `facts_deleted` to `AuditEntries_subject_ck`. It also deletes once the legacy ownerless `ExtractedFacts` rows and the pageless `document` excerpt references, and sets existing clean documents to `ReadStatus='pending'` the first time the column is added, so the startup sweep reads them.

### 2.5 Database

- PostgreSQL data volume on durable storage, with backups (2.7).
- A brand-new database is bootstrapped from `anamnys-db-script.sql` at first start. An existing
  database needs every file in `design/migrations/` it has not seen, in date order, **before**
  the new server version starts (e.g. `OrphanNoteSweep` fails startup without
  `2026-10-10-live-session.sql`).
- Keep a record per environment of which migration files were applied. An existing database must
  have `2026-10-20-documents-team.sql`, `2026-10-21-theme-vocabulary.sql` and
  `2026-10-23-account-export.sql` applied (the second
  seeds the vocabulary and adds the columns the server now reads; the third adds the export status
  columns and the one-export-in-progress index). `2026-10-24-excerpt-refs.sql` gives `ExcerptRefs` its
  provider, section, quote and kind columns, links existing theme tags to a section reference and
  deletes the old unusable `ExcerptRefs` rows once.

### 2.6 Network, TLS and capacity

- TLS-terminating ingress for the server (all four SPAs are served by it, same origin).
- **`ForwardedHeaders`** must be configured before production: the confirmation endpoints'
  rate limiter keys on `RemoteIpAddress`, so behind a proxy every client would share one
  bucket.
- Memory: Whisper `medium` + Qwen2.5 7B Q4_K_M (≈ 6 GB) + live-recording buffers (≈ 600 MB per
  concurrent 90-minute recording). Size the server container accordingly or cap concurrency.
  Theme suggestion also holds its own small LLM context (≈ 0.6 GB with
  `NoteStructuring:SuggestionContextSize` 4096: KV cache plus compute buffer) for the life of the
  process (warmed in the background after start; `NoteStructuring:WarmSuggestionOnStart`, default
  `true`, set `false` to load lazily on the first suggestion; chapter 23, section 4).
- The server image needs **tzdata** (`America/Sao_Paulo` is used for PDFs and e-mails).

### 2.7 Backups and recovery (backlog F-46)

- Postgres (all clinical data, audit trail) and the SeaweedFS volume (exported PDFs).
- A restore must be rehearsed before the product promises a "cofre" or long-term custody.

### 2.8 Licences and legal (before charging anyone)

- QuestPDF Community licence is valid only while annual revenue is under US$ 1M.
- Processor agreements (LGPD art. 39) for every external service that sees personal data
  (e-mail provider, telemetry backend if hosted).
- FAQ/Terms promises still pending in code: whole-account export (F-60), PDF validity wording
  already aligned to "signed electronically in Anamnys".

---

## 3. Quick checklist

**Dev (once per machine):** Docker · Maven · theme jar · 6 user secrets · migrations applied.

**Staging and production (each, separately):**

- [ ] `ASPNETCORE_ENVIRONMENT` = Staging / Production
- [ ] 3 client secrets, Keycloak admin password, 2 storage keys (new values)
- [ ] `storage-public-url` (https) + storage host with TLS and proxy to port 8333 only
- [ ] `DataProtection:CertificateThumbprint` + certificate installed
- [ ] `KEYCLOAK_ADMIN_*` with an absolute Keycloak URL
- [ ] E-mail provider (`Email:*`), verified domain, `PatientPortal:BaseUrl`
- [ ] Keycloak SMTP relay (not built yet)
- [ ] Keycloak image without dev seed; TLS; redirect URIs
- [ ] Model directories on a persistent volume; Whisper SHA-256 verified
- [ ] Migrations applied in order (through `2026-10-26-document-facts.sql`)
- [ ] Tesseract data reachable (or pre-placed) and a Linux container OCR smoke test
- [ ] `ForwardedHeaders` configured
- [ ] Memory sized; tzdata in the image
- [ ] Backups of Postgres and SeaweedFS + a rehearsed restore
- [ ] Processor agreements; QuestPDF licence condition
