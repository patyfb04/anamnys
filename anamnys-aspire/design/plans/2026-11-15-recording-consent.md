# Recording Consent (G1: F-23 + F-24) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A versioned recording consent term signed by the patient (or guardian) through an e-mailed link, a server-enforced state machine that allows session recording only with a valid consent, and a free, audited dictation mode.

**Architecture:** New `RecordingConsent/` server area (term rendering, rules, provider endpoints, public sign/revoke endpoints, PDF, e-mails) on the existing `RecordingConsents`/`ConsentEvents` tables; `TranscriptionHub.BeginRecording` gains a mode and calls the capture rule. Provider card on the patient page (Avaliação tab), header badge, Sessão ao vivo mode choice; two public pages in the patient app (no login), like `confirmar.$token`.

**Tech Stack:** .NET 10, EF Core + Npgsql, QuestPDF, SignalR, React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-11-15-recording-consent-design.md` (binding).

## Global Constraints

- Branch `feature/recording-consent`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests/anamnys-aspire.Tests.csproj -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time; output to a file in the scratchpad `C:/Users/patri/AppData/Local/Temp/claude/cddb4cc4d594bfe5/13806727-9a85-492d-af33-bc5a7050064b/scratchpad/` and grep it. Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB. Scratch databases for new-schema tests.
- Change files only with Edit/Write (no shell commands that write, move or delete files); CRLF for new `.cs`; keep endings.
- Provider scoping; 404 for foreign ids; 401 first; `no-store`.
- No patient names, dates of birth, tokens or term text in logs, URLs of the API (other than the opaque token path segment of the public routes) or audit values (ids and codes only). Tokens stored only as SHA-256.
- Every dead, unknown, used or expired public link answers the same 410 "Este link não é mais válido."
- E-mails carry no clinical content. Manual e-mail testing uses Resend, never Mailpit.
- The capture rule is enforced on the server; the UI only mirrors it.
- Copy in `pt.json` (`recordingConsent.*`); `DetailCard`; `npm run build` / `npm run lint` pass in the provider and patient apps.

---

### Task 1: Server — consent model, rules, endpoints, e-mails, PDF

**Files:** `design/migrations/2026-11-15-recording-consent.sql` (spec §2) + `anamnys-db-script.sql` + migration-test chains (after `2026-11-12-structured-intake.sql`); entity/mapping updates for `RecordingConsents`, `ConsentEvents`; create `anamnys-aspire.Server/RecordingConsent/ConsentTerm.cs` (template `v1`, spec §3), `ConsentRules.cs` (states, transitions, lazy expiry, `CanRecordSession`), `RecordingConsentService.cs`, `ConsentTokens.cs`, `ConsentPdf.cs`, `ConsentEmails.cs`, `RecordingConsentEndpoints.cs` (provider, spec §5), `PublicConsentEndpoints.cs` (public, `public` rate-limit policy), `RecordingConsentContracts.cs`; patient archiving expires the consent in the same transaction; `Retention/DiscardService.cs` (consents, events, PDF object); `Notifications` (kinds `consent_signed`, `consent_revoked`); audit actions; `Program.cs`; `.http`; tests `anamnys-aspire.Tests/RecordingConsent/*`, `Data/RecordingConsentMigrationTests.cs`.

**Interfaces (produced):**
- `ConsentRules.CanRecordSession(RecordingConsent? consent, Patient patient, DateTimeOffset now) : (bool Allowed, string Reason)` with reasons `none|sent|revoked|expired|assent_missing|archived` (`ok` when allowed).
- `RecordingConsentService.GetEffectiveAsync(AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct) : Task<RecordingConsent?>` — applies due expiries (persisted) and returns the current consent.
- Endpoints exactly spec §5; public routes `/api/public/recording-consents/{token}` and `/api/public/recording-consents/revoke/{token}` (GET view, POST submit).

- [ ] Spec §2–§5 and the server bullets of §7; tests first (state machine, capture rule, links, revocation, preconditions, discard, scoping, migration).

### Task 2: Server — live session modes

**Files:** `LiveSession/TranscriptionHub.cs` (`BeginRecording` gains `string mode` = `session`|`dictation` and `bool dictationConfirmed`), `LiveSession/LiveRecordings.cs` (`CheckBeginAsync` calls `RecordingConsentService.GetEffectiveAsync` + `ConsentRules.CanRecordSession` for `session`; `dictation` requires `dictationConfirmed` and writes audit `dictation_confirmed`), `LiveSessionContracts.cs` (refusal `consent_required` + reason), tests in `anamnys-aspire.Tests/LiveSession/*`; update existing live-session tests that start a recording so they set up a signed consent or use dictation.

**Interfaces:** Consumes Task 1's `ConsentRules.CanRecordSession` and `RecordingConsentService.GetEffectiveAsync`. Produces the hub signature `BeginRecording(Guid recordingId, Guid patientId, Guid? appointmentId, string format, string language, string mode, bool dictationConfirmed)` and the `BeginAck` refusal `consent_required` with `reason`.

- [ ] Spec §4 capture rule and §7 capture bullets; tests first.

### Task 3: Provider UI and patient public pages

**Files:** `packages/shared/src/api/recordingConsent.ts` (+ types), `packages/shared/src/api/publicConsent.ts`; provider: `components/recordingConsent/RecordingConsentCard.tsx` (+ preview/send modal, assent form, revoke dialog, history) on the patient page Avaliação tab, header badge "Gravação autorizada", `routes/_app/live-session.tsx` + `hooks/useLiveRecorder.ts` (mode choice, disabled session mode with reason and "Enviar termo" link to the patient page `?tab=avaliacao`, dictation confirmation), bell kinds; patient app: `routes/consentimento.$token.tsx`, `routes/consentimento.revogar.$token.tsx` (public, same pattern as `confirmar.$token.tsx`); `pt.json` (`recordingConsent.*`).

**Interfaces:** Consumes Task 1 endpoints and Task 2 hub signature.

- [ ] Spec §6. Build and lint in provider and patient apps.

### Task 4: Documentation

**Files:** `documentation/44-recording-consent.md`, `00-index.md`, `design/status/2026-10-15-implementation-status.md` (F-23, F-24 rows; verification line), `design/gaps/2026-10-03-patient-flows-gaps.md` (spec §8 gaps).

- [ ] Spec §9 (Confluence is updated by the controller).
