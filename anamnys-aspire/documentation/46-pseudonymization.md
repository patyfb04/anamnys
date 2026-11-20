# Chapter 46: Third-Party Pseudonymisation (F-27)

How the names of third parties (people, organisations, schools, addresses) in a session transcript or in the text of an AI
note are replaced by stable per-patient tokens before anything is stored or structured, how the tokens and the secrets
behind them are kept, what happens when the step fails, and how old transcripts are converted in the background. Last
checked against the code on 2026-11-19 (branch `feature/pseudonymization`, not yet merged into `develop`, nothing
committed). Design: `design/specs/2026-11-17-pseudonymization-design.md`. Depends on chapter 45 (the transcript comes
from the transient-audio pipeline) and chapter 40 (backup of the key ring).

What a future change has to respect: the product and any material say "reduz a exposição de terceiros", **never
"anonimizado"** (the data stays pseudonymised personal data, LGPD art. 13 §4); the original text lives only in memory and
is never stored, logged, audited or carried by an exception message (section 2); no column anywhere stores a surface
string or a number-to-name mapping, and the secret only allows testing a guessed name (sections 4 and 12); the secrets
are protected by the dedicated key ring in Postgres, so that ring and its certificate must be backed up (section 5); the
migration is applied before the new server starts (section 6); on a failure nothing with names is written and the held
text lives at most 10 minutes (section 7); the backfill rewrites only `RawTranscript`, never a signed section (section
8); over-suppression is preferred to a leak (section 3); audit values carry counts and codes only (section 10).

---

## 1. Purpose and scope

F-27, the third delivery of group G. Done when (backlog): the same third party mentioned in sessions 3 and 12 resolves
to the same token, and no reversible mapping exists anywhere in the system.

Decisions agreed with the user (2026-11-17):

| Topic | Decision |
|---|---|
| How far tokens go | The stored transcript (`RawTranscript`) **and** the AI draft use tokens: the draft is structured from the pseudonymised transcript. In review the provider keeps the token or types the real name (their choice; what the provider writes is not touched). |
| Detection | In-process Qwen 7B (the existing LLamaSharp model) in a step before structuring, plus a rule safety net with a list of Brazilian first names (IBGE). No new model or dependency. |
| Existing data | Applied from now on; Settings → Prontuário has "Pseudonimizar transcrições antigas" (background, one note at a time, only `RawTranscript`; signed note sections never change). |
| Inputs | The Sessão ao vivo transcript and the text of "Nota com IA" (Note Editor AI mode). Not the manual note, not anything the provider writes or edits in review. |

Out of scope (section 13): recognising that "o Zé" and "José" are one person, what the provider writes in review, names
inside uploaded external documents and their extracted facts, a provider-side legend of tokens.

---

## 2. Pipeline

```
Sessão ao vivo:  audio --Whisper--> text (audio zeroed) --PseudonymizationStep--> RawTranscript (tokens) --> structuring
Nota com IA:     typed text (only in the in-memory PseudonymizeJob) --PseudonymizationStep--> RawTranscript (tokens) --> structuring
Manual note:     text stored as typed (no step)
```

- **Live session** (`LiveSession/NoteTranscription.cs`, `TranscriptionWorker.cs`): after the transcriber returns, the
  step runs and, on `Done`, the note gets the tokenised `RawTranscript`, `TranscriptPseudonymized = true`,
  `ReadyForReview` and the audit `transcript_pseudonymized`, in one transaction. Then the audio destruction is recorded
  (`transcribed` on both `Done` and `Failed`: the audio was transcribed, chapter 45), then structuring runs on the
  tokenised transcript.
- **AI mode** (`Notes/NoteCreation.cs`): the note is created `Processing` with `RawTranscript` null and structuring
  pending, so no row ever holds the typed text. The text travels only in the in-memory `PseudonymizeJob`; the worker runs
  the step and then structuring. Manual mode is unchanged.
- While the step runs the note page shows "Protegendo nomes de terceiros…" (Text-mode note in `Processing` without a
  transcript yet).
- A restart: `LiveRecordings.FailOrphansAsync` also fails a Text note left in `Processing` with `server_restart`.
- The original text is a managed string (Whisper output, request body, job text) and cannot be zeroed; only the held copy
  (section 7) is a `char[]` that is zeroed. No log line, audit value or exception message carries text.

---

## 3. Detection

`IThirdPartyDetector.DetectAsync(text, exclusions, ct)` returns `DetectedSpan(Start, Length, Kind)`, kinds `person`,
`organization`, `school`, `address` (`EntityKinds`).

**`LlamaThirdPartyDetector`** (`Pseudonymization/`): the same model provider and `LlmGate` as structuring (no second model
instance). A fixed Portuguese system prompt and a GBNF grammar make the model return the exact surface strings and kinds
(`{"entities":[{"text":...,"kind":...}]}`); the prompt tells it to copy each mention verbatim, not to list kinship words
or pronouns, cities, professions, medicines, diagnoses, dates or numbers, and never the patient's and provider's names.
Output is parsed strictly (an unknown kind or bad JSON is a `FormatException` without the output). Each returned string
is located in the text by exact ordinal match, every occurrence, on word boundaries; a string not found verbatim is
ignored.

- **Chunks.** `Pseudonymizer` cuts the text into chunks of about 6000 characters with a **200-character overlap**
  (starting at a word start, never before the middle of the previous piece), so a name cut between two chunks is found
  whole in the overlap; found pairs and located spans are de-duplicated and `ResolveOverlaps` keeps the whole name over a
  partial one. The detector splits each chunk again into model runs of at most 1536 tokens, at sentence ends. A run whose
  output was truncated at `OutputTokens` is split in two and re-run, up to 3 levels deep.
- **Time budget.** Each model run has a budget counted **from the moment the gate is held** (waiting for the gate does
  not count): `NoteStructuring:PseudonymizationRunTimeout`, default 12 minutes (an estimate from about 2 tokens per
  second for 1024 output tokens plus prompt evaluation; not measured on the target CPU). A non-positive value falls back
  to the default. An overrun raises `TimeoutException`, which fails the step (a failure, section 7). The step itself has
  no overall timeout.
- **Priority.** Live work uses `LlmPriority.Interactive`; the backfill uses a second detector at `Background` over the
  same model provider and gate.

**Exclusions** (never replaced), built by `Pseudonymizer`: the patient's first name, last name, full name and last-name
parts longer than 2 letters; the provider's **full** profile name and its words, without titles (Dr, Dra, Sr, Sra, Seu,
Dona…). The provider's first name **alone** is not excluded, so a third party who shares it is replaced. `Patients` has no
nickname column, so none is excluded. Kinship and pronoun words ("mãe", "pai", "eu", "ele"…) are never tokens.

**Span clean-up.** Person spans lose leading kinship, title and article words ("tia Maria" becomes "tia [P1]"; articles
o, a, os, as, do, da, dos, das, no, na, pro, pra, ao… are stripped from the front, NFD input included); a span made only
of kinship, pronoun or excluded words is dropped.

**Safety net `FirstNameRule`** (`first-names.txt`, an embedded resource of about 790 entries (793 lines): lower-case, no accents, the
most frequent IBGE "Nomes no Brasil" names plus recent popular names and short forms such as Zé, Bia, Duda; everyday
words such as "irmã", "luz", "mel", "flor" are left out): a capitalised word in the list, not excluded and not already
covered, becomes a `person` span, **also at the start of a sentence** (hyphenated words by their first part). Every kept
person span then runs on through `ExtendRight`: the rest of a hyphenated word, following capitalised words, and de/da/do
+ a capitalised word ("Pedro da Costa"). `ExtendRight` stops before a pronoun or kinship word, an article or one of the
patient's own words. Overlaps are resolved longest first, then earliest.

**Over-suppression is preferred to a leak.** Ordinary words that are also first names ("Rosa", "Clara", "Vitória",
"Glória") at the start of a sentence may become person tokens; a capitalised word right after a name (an organisation
after a person) is joined into the name's token. A relative who shares the patient's surname ("Pedro Silva" for patient
"Ana Silva") becomes "[P1] Silva", because the patient's own names are never tokenised.

---

## 4. Tokens

- Persons `[P1]`, `[P2]`…; organisations `[Empresa 1]`…; schools `[Escola 1]`…; addresses always `[Endereço]` (no number,
  no row).
- **Stable per patient.** `fingerprint = HMAC-SHA256(patientSecret, kind + ":" + normalize(surface))`; `normalize` is
  trim, lower-case, accents removed, inner whitespace collapsed (`PseudonymTokens.Normalize`). The same name in sessions 3
  and 12, with other case or accents, is the same token; two patients are numbered independently; kinds are numbered
  separately. "Zé" and "José" are different strings, so different tokens (gap).
- **`PseudonymTokens`** (`PatientId`, `Kind`, `Fingerprint` bytea of 32 bytes, `Number` >= 1, `CreatedAt`; primary key
  (`PatientId`, `Kind`, `Fingerprint`), unique (`PatientId`, `Kind`, `Number`)). Numbers go in order of first appearance
  per kind, assigned in **one transaction** under `SELECT … FOR UPDATE` on the patient's secret row, with a
  unique-violation retry. The surface string is never stored.
- **`PatientPseudonymSecrets`** (`PatientId` primary key, `Protected` bytea, `CreatedAt`): 32 random bytes per patient,
  protected with ASP.NET Core Data Protection, purpose `Anamnys.Pseudonymization.v1`. The secret is created only when no
  row exists (`INSERT … ON CONFLICT DO NOTHING`, then re-selected). It is read once per `PseudonymizeAsync` and the secret
  bytes and the HMAC input are zeroed right after use (deliberately not cached).
- **No reversible mapping.** Nothing maps a number back to a name. With the secret one can only **test a guessed name**
  against a fingerprint: this is the residual risk of pseudonymisation (section 12).
- Counts in the audit value are occurrences replaced per kind (section 10).

---

## 5. Key ring and secrets

The patient secrets are protected by a **dedicated Data Protection key ring**, separate from the main (authentication)
ring, which is unchanged.

- **Why it exists.** The main ring keeps its keys in Redis without a persistent volume; secrets encrypted with it would be
  unreadable after a Redis restart, and every token would change. Ruling of the build ledger (2026-11-18): a separate ring
  persisted in Postgres.
- **Code.** `PseudonymizationKeyRing` builds a `IDataProtectionProvider` with application name
  `anamnys-pseudonymization`; `PostgresXmlRepository` is a small custom `IXmlRepository` over the table
  **`PseudonymizationKeys`** (`Id` uuid primary key, `FriendlyName` unique, `Xml` text, `CreatedAt`); no new package.
  Wired in `PseudonymizationSetup`.
- **Outside Development** the keys are encrypted at rest with the certificate named by
  `DataProtection:CertificateThumbprint` (CurrentUser/My, the same certificate as the main ring). **The app fails at
  startup if it is missing.**
- **Rotation.** New keys are protected with the current certificate. Keys protected with an earlier certificate are read
  through `DataProtection:PreviousCertificateThumbprints` (a JSON array, or one string separated by commas or semicolons
  for an environment variable). **Keep the old certificate installed**; a thumbprint that cannot be found stops startup.
  Dropping the old certificate while old keys exist makes their secrets unreadable.
- **Backup.** `PseudonymizationKeys` is in the database, so it travels with the database backup and restore (chapter 40,
  section 6). **The certificate does not travel with it** and must be restorable separately and installed before the
  server starts. Without the keys or the certificate the patient secrets cannot be read: tokens are no longer stable and
  each pseudonymisation of an existing patient fails until that patient's secret row is deleted (a new secret then
  numbers tokens from 1 again).
- **Development caveat.** In Development, with no `DataProtection:CertificateThumbprint`, Data Protection's default XML
  encryptor is active for this ring. On Windows that is DPAPI, bound to the Windows user: the keys in
  `PseudonymizationKeys` can be read only by the same user on the same machine. **A dev database copied or restored to
  another machine or user loses every patient secret.**
- **Discard** deletes the patient's secret and tokens (section 11).

---

## 6. Data and migration

Migration `design/migrations/2026-11-17-pseudonymization.sql` (idempotent, mirrored at the end of
`anamnys-db-script.sql`, appended to the migration-test chains after `2026-11-16-transient-audio.sql`; the notifications
chain skips it because that chain has no `Notes.FailureReason`). **It must be applied to every persistent environment
before the new server starts.** Without it every read or write of `Notes` through EF fails (`42703: column
"PseudonymizationFailedAt" of relation "Notes" does not exist`), and the server cannot pseudonymise
(`PseudonymizationKeys` missing). It is **not yet applied to the dev database**: until then dev-database suites such as
`NoteCreationTests`, `TranscriptionWorkerTests`, `NoteTranscriptionTests` and `NoteEndpointTests` fail; stop Aspire
before running the suite.

- `PatientPseudonymSecrets`, `PseudonymTokens` (`Kind` is `person|organization|school`, check `PseudonymTokens_Kind_ck`;
  the `Patients_not_discarded` trigger covers both), `PseudonymizationKeys`.
- `PseudonymizationBackfills` (`ProviderId` primary key, `StartedAt`, `FinishedAt`, `Total`, `Done`, `Failed` >= 0).
- `Notes.TranscriptPseudonymized` boolean NOT NULL default false (true for notes written through the step; false for
  legacy rows until the backfill converts them) and `Notes.PseudonymizationFailedAt` timestamptz; `Notes_FailureReason_ck`
  accepts `pseudonymization_failed`. (`2026-10-10-live-session.sql` redefines that check unconditionally, so the chains
  that include it re-apply the new file afterwards.)
- Audit actions added to the guarded `AuditEntries` list: `transcript_pseudonymized`, `pseudonymization_failed`,
  `pseudonymization_backfill_started`.
- The retention-panel chain counts 22 triggers instead of 20; the audio-destruction proof (chapter 45, section 5) adds
  `PatientPseudonymSecrets.Protected` and `PseudonymTokens.Fingerprint` to its `bytea` whitelist.

---

## 7. Failure and retry

Any exception from detection or tokens (including a timeout of a model run) fails the step:

- Nothing with names is written. The note becomes `Failed` with reason `pseudonymization_failed`, with
  `PseudonymizationFailedAt` set and `StructureStatus` null; audit `pseudonymization_failed` (no value). Logs say
  `Pseudonymization failed for note {NoteId}: {ExceptionType}` and nothing else.
- The original text goes to `PseudonymizationHolds`: a `char[]` zeroed on take or expiry, held **at most 10 minutes**
  (`LiveSessionRules.BufferIdleTimeout`) from the first failure; a retry keeps the first deadline. Each entry has its own
  timer (from the `TimeProvider`) that zeroes and drops exactly that entry. The text is **never persisted**.
- **Retry:** `POST /api/phi/providers/me/notes/{noteId}/pseudonymization/retry` (`PseudonymizationRetry`, `Cache-Control:
  no-store`): 401 without a session; 404 unknown or foreign note; 409 `{ "code": "not_failed" }`; 410
  `{ "code": "text_gone" }`; 202 accepted. It takes the held text, moves the note back to `Processing` (reason and
  failed-at cleared, structuring pending) and enqueues a `PseudonymizeJob` with the original deadline.
- `GET` note adds `transcriptPseudonymized`, `pseudonymizationFailedAt` and `pseudonymizationRetryable` (true only for a
  note failed with `pseudonymization_failed` whose text is still held).
- After 10 minutes, after a restart or after a discard the text is gone: the note stays failed and the provider dictates
  again or writes by hand. A **discard** forgets and zeroes the held text of every note of the patient.
- A note deleted during the step ends `Gone` (no notification, the audio destruction is still recorded without a failure).
  A failed note cannot be restructured.
- **AI-mode Processing.** A typed AI note stays `Processing` with no text until the step finishes; the UI polls every 5
  seconds. If the step fails the typed text is held for the retry (above); if the server restarts meanwhile the note is
  failed with `server_restart` and the text is gone.

---

## 8. Backfill

For transcripts written before F-27 (`TranscriptPseudonymized = false`). Code: `Pseudonymization/BackfillWorker.cs`
(the service `PseudonymizationBackfills`, `BackfillEndpoints`, the hosted `BackfillWorker`).

- `GET /api/phi/providers/me/pseudonymization/backfill` returns `{ pending, running, total, done, failed, startedAt,
  finishedAt }`; `pending` is the provider's notes with a non-empty `RawTranscript` and `TranscriptPseudonymized =
  false`. `POST` starts it: 202 with the status, or 409 `{ "code": "already_running" }`. Both answer 401 first and
  `no-store`. The start is one atomic upsert (`ON CONFLICT … WHERE FinishedAt IS NOT NULL`) so two simultaneous starts
  cannot both win; the audit `pseudonymization_backfill_started` (value `total=N`) commits with it.
- The worker is a `BackgroundService` woken by the POST. It **polls every minute** and also runs any running backfill at
  startup (resume after a restart; progress is in `PseudonymizationBackfills`). **A start or a resume resets `Failed` to 0
  and recomputes `Total` as `Done` + pending**, so failed notes are retried and not counted twice.
- One note at a time, in `Id` order with a cursor, through `Pseudonymizer.PseudonymizeAsync` with the second detector at
  `LlmPriority.Background`: it advances only between live requests. A model run over the 12-minute budget **counts as a
  failure** of that note.
- The write is one `ExecuteUpdate` of `RawTranscript` and `TranscriptPseudonymized = true` guarded by the note id, the
  provider, "still unconverted" and "`RawTranscript` unchanged", with the audit `transcript_pseudonymized` and `Done` + 1
  in the same transaction. Sections, seals, status and signed content are never touched, so the integrity chain still
  verifies. A note changed or converted concurrently is skipped and removed from `Total`.
- A note whose detection fails stays unconverted and counts in `Failed`; the log line has the note id and the exception
  type only. Shutdown leaves the row running (it resumes at the next start).
- **Single instance assumed:** with two server instances both would process the running backfill (section 13).

---

## 9. UI

Provider (`apps/provider/src`, shared types and api in `packages/shared/src`):

- `routes/_app/notes/$noteId.tsx` (the only review view; the editor's AI mode navigates there): the notice when
  `transcriptPseudonymized`; token highlighting in the transcript card and the sections (`highlightTokens` in
  `components/notes/PseudonymTokens.tsx` wraps `[P1]`, `[Empresa N]`, `[Escola N]` and `[Endereço]`; the text content is
  unchanged and only the read-only display is highlighted, never the textarea); the processing copy; the failed state
  with "Tentar de novo" only while `pseudonymizationRetryable`, otherwise the text-gone guidance.
- `components/settings/PseudonymizationBackfillSection.tsx` in Settings → Prontuário: pending count, "Iniciar" (disabled
  when nothing is pending, running or submitting), a progress bar (`done + failed` over `total`) polled every 5
  seconds while running, the failed count and the last finish time. After a finished run `pending` can still be above 0
  (failed notes), so "Iniciar" stays enabled to retry.
- `packages/shared/src/api/pseudonymization.ts`: `retry(noteId)`, `backfillStatus()`, `startBackfill()`.
  `types.ts`: `NoteFailureReason` gains `pseudonymization_failed`; `NoteDetail` the three fields; the type
  `PseudonymizationBackfillStatus`.

Copy in `packages/shared/src/lib/i18n/locales/pt.json` under `pseudonymization.*` (and `liveSession.failed.pseudonymization_failed`):

| Key | Text |
|---|---|
| `notice` | "Nomes de terceiros foram substituídos por códigos ([P1], [Empresa 1]…), o que reduz a exposição de terceiros. O mesmo código indica a mesma pessoa em todas as sessões deste paciente." |
| `processing` | "Protegendo nomes de terceiros…" |
| `failed.title` | "Não foi possível proteger os nomes de terceiros" |
| `failed.retryable` | "Nada com nomes foi guardado. Você pode tentar de novo agora." |
| `failed.gone` | "O texto original não é mais guardado, por isso não dá para tentar de novo. Dite outra vez ou escreva a nota à mão." |
| `failed.retry` | "Tentar de novo" |
| `backfill.title` | "Pseudonimizar transcrições antigas" |
| `backfill.description` | "Troca nomes de terceiros por códigos nas transcrições anteriores, o que reduz a exposição de terceiros. Só o texto-fonte é alterado; notas já assinadas não mudam. Roda em segundo plano, uma nota por vez." |
| `backfill.pending` | "{{count}} transcrição aguardando." / "{{count}} transcrições aguardando." |
| `backfill.start`, `running` | "Iniciar", "Em andamento…" |
| `backfill.progress` | "{{done}} de {{total}} transcrições processadas" |
| `backfill.failedCount` | "{{count}} transcrição não pôde ser processada e será tentada de novo na próxima execução." (plural: "…transcrições não puderam ser processadas e serão tentadas de novo na próxima execução.") |
| `backfill.finishedAt` | "Última execução concluída em {{date}} às {{time}}." |
| `backfill.alreadyRunning`, `startFailed`, `loadFailed` | "Já está em andamento.", "Não foi possível iniciar.", "Não foi possível carregar o andamento." |
| `liveSession.failed.pseudonymization_failed` | "Não foi possível proteger os nomes de terceiros. Grave novamente." |

---

## 10. Audit

| Action | Value | Written |
|---|---|---|
| `transcript_pseudonymized` | counts per kind, occurrences replaced, e.g. `person=2,organization=0,school=1,address=1` (`PseudonymizationResult.AuditValue`); no text | by the step (live and AI mode) and by the backfill, in the same transaction as the write |
| `pseudonymization_failed` | none | by the step on a failure |
| `pseudonymization_backfill_started` | `total=N`; actor is the provider | when a backfill starts |

No name, surface string, transcript or free text enters an audit value, a log line or an exception message that is logged.

---

## 11. Discard

`DiscardService.DiscardAsync` deletes the patient's `PseudonymTokens` and `PatientPseudonymSecrets` (they are not counted
in the disposal term: not clinical content) and, after the commit, forgets and zeroes the held text of every note of the
patient (`DiscardEndpoints` passes the holds). There is no general note-delete endpoint: the only `DELETE FROM "Notes"`
is the unsigned intake draft, which never goes through the step.

---

## 12. Testing and residual risks

Server (scratch databases, Aspire stopped). The detector is **a fake with fixed answers** (`FakeThirdPartyDetector`,
`HangingDetector`, `PseudonymizationKit` in `anamnys-aspire.Tests/Pseudonymization/`); **a real Qwen run on Portuguese
clinical speech is the user's manual check.**

- `PseudonymizationRulesTests` (no database: tokens, normalisation, first-name rule, segments, article stripping, options),
  `PseudonymizerTests` (same name in two sessions, accent and case variants, independent numbering per patient and per
  kind, `[Endereço]`, exclusions, safety net on a name the fake missed, surname extension, concurrency, no surface string
  stored anywhere, key ring across a restart and rotation), `PseudonymizationStepTests` (live transcript, AI mode, manual
  notes untouched, failure and timeout, retry, holds and their timers, discard), `PseudonymizationBackfillTests`
  (conversion, signed notes and the integrity chain untouched, resume, failure and retry, concurrency, scoping, priority,
  401), `Data/PseudonymizationMigrationTests.cs`, and the migration chains; `AudioDestructionTests` and
  `DiscardServiceTests` were adapted.
- Results: `-class "*Pseudonym*"` 89/89 (Aspire stopped); `*MigrationTests` 175/175, `*AudioDestruction*` 11/11 and
  `*Discard*` 21/21 earlier in the build. Dev-database note suites need the migration applied to the dev database first.
- Provider app: `npm run build` and `npm run lint` pass; the UI is not exercised in a browser (no runner, section 13).

**Manual check:** with a real model, dictate or type a text with a few names, an organisation, a school and an address; the
note page shows the tokens highlighted and the notice; the same name in a second session has the same code; the patient's
and provider's names stay; kinship words stay; stop the model or shorten `PseudonymizationRunTimeout` to see "Não foi
possível proteger os nomes de terceiros" and "Tentar de novo"; run the backfill in Settings → Prontuário.

**Residual risks.** The data stays pseudonymised personal data. (1) Anyone holding the patient's secret can test a guessed
name against the fingerprints. (2) The model can miss a name that the first-name list does not know; the safety net covers
common first names only. (3) What the provider writes in review is untouched. (4) The held text lives in memory for up to
10 minutes after a failure.

---

## 13. Where things are, and gaps

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-11-17-pseudonymization.sql` |
| Entities | `anamnys-aspire.Server/Data/Entities/` (`PseudonymToken`, `PatientPseudonymSecret`, `PseudonymizationBackfill`, `Note`) |
| Server code | `anamnys-aspire.Server/Pseudonymization/` (`IThirdPartyDetector`, `LlamaThirdPartyDetector`, `FirstNameRule` and `first-names.txt`, `PseudonymTokens`, `PatientSecrets`, key ring and `PostgresXmlRepository`, `Pseudonymizer`, `PseudonymizationHolds`, `PseudonymizationStep`, `PseudonymizationRetry`, `BackfillWorker`, `PseudonymizationSetup`) |
| Other hooks | `LiveSession/` (`NoteTranscription`, `TranscriptionWorker`, `TranscriptionQueue`, `LiveRecordings`), `Notes/` (`NoteCreation`, `NoteQueries`, `NoteEndpoints`), `Retention/` (`DiscardService`, `DiscardEndpoints`), `NoteStructuring/NoteStructuringOptions.cs`, `Data/AnamnysDbContext.cs`, `Program.cs`, `appsettings.json` |
| Provider UI | `apps/provider/src/routes/_app/notes/$noteId.tsx`, `components/notes/PseudonymTokens.tsx`, `components/notes/NoteSections.tsx`, `components/settings/PseudonymizationBackfillSection.tsx` |
| Shared | `packages/shared/src/api/pseudonymization.ts`, `lib/types.ts`, `lib/i18n/locales/pt.json` |
| Tests | `anamnys-aspire.Tests/Pseudonymization/`, `Data/PseudonymizationMigrationTests.cs` |

Deployment: apply the migration to every persistent environment before the new server starts; outside Development set
`DataProtection:CertificateThumbprint` (and `PreviousCertificateThumbprints` after a rotation); back up
`PseudonymizationKeys` and the certificate (chapter 40). The Confluence page is updated separately (spec section 10).

Limits and known gaps (full list in `design/gaps/2026-10-03-patient-flows-gaps.md`, section "Pseudonimização"):

- "Zé" and "José" get different tokens; what the provider writes in review, names inside external documents and their
  extracted facts, and a legend of tokens are out of scope.
- Over-suppression: first names that are everyday words at a sentence start, a capitalised word after a name, a relative
  sharing the patient's surname ("[P1] Silva"), a segment cut inside a name that the overlap does not recover.
- The provider's first name alone is not excluded; the patient has no nickname field.
- Until the backfill converts it, a legacy transcript is restructured and theme-suggested from its original text (names
  included); the backfill converts only the source text, never the sections written before. `POST` of a note in manual
  mode with text is refused (400): a manual note never carries a stored transcript.
- Backfill assumes one server instance; it advances only between live requests; its HTTP 202 and 409 paths are tested
  through the service, not over HTTP.
- The 12-minute run budget is an estimate; real-model quality is a manual check.
- Development key ring uses DPAPI (a copied dev database loses its secrets); a certificate rotation needs the old
  certificate installed.
- Not exercised in a browser; the migration is not yet applied to the dev database.
