# Chapter 44: Recording Consent (F-23, F-24)

How the provider asks a patient (or the guardian of a minor) for consent to record sessions, how the patient signs it
through a public link, how the consent moves through its states, and how the server refuses to record a session unless
the consent is valid. Last checked against the code on 2026-11-15 (branch `feature/recording-consent`, not yet merged
into `develop`; sections 4 to 9 were compared with the C# in `RecordingConsent/` and `LiveSession/` after the review
rounds). Design: `design/specs/2026-11-15-recording-consent-design.md`.

**Built with a caveat (D-03):** the term text was written without a lawyer's review, as agreed with the user on
2026-11-03 (it carries the names of every party, the date, and the other items Res. CFP 13/2022 asks for; given once
per patient and treatment, not per session). The wording must be reviewed before it is relied on in production.

What a future change has to respect: the capture control is refused **by the server** in every state except `SIGNED`
(section 5), and the same evaluation (including due expiries) is used by the hub, the card and the badge; the signing
and revocation links are one-time, store only the SHA-256 of the token and answer every dead link identically (section
7); the signer signs the **stored term text** and its hash is recorded (section 4); e-mails never carry clinical content
or the date of birth (section 8); a revoked consent never touches notes already signed (section 4); audit values carry
ids and codes only (section 13); and the migration is applied before the server that reads it (section 3).

---

## 1. Purpose and scope

F-23 (recording consent term) and F-24 (consent state machine), the first delivery of group G (session capture). Done
when (backlog): the provider issues the term, the patient signs, the signed artefact is filed in the record, and the
capture control is DISABLED (not merely discouraged) in every state except `SIGNED`, enforced on the server.

Out of scope (see section 15): tab audio capture (F-25, G2), transient-audio proof (F-26), pseudonymisation (F-27),
transcript as source (F-28).

---

## 2. Decisions

| Topic | Decision |
|---|---|
| How the patient signs | A signing link (no login) on a public page of the patient app, like the appointment confirmation. |
| Identity | The link goes to the patient's contact e-mail. The signer types the patient's date of birth (must match the record) and their full name as signature. Recorded: date-time, term version, SHA-256 of the signed text, "date of birth matched". No IP. One-time link, 7 days, resendable. |
| Minors (under 18 today) | The guardian signs through the link with name and relationship. The minor's assent is recorded by the provider (date + optional note). Capture needs both. |
| Validity | Until revoked, while the patient is in care. Expires on archiving (discharge), when a patient who signed as a minor turns 18, or when the provider asks again after a new term version. |
| Term text | Fixed, versioned Anamnys text (`v1`) with every mandatory clause; names, CRP and dates filled in; one optional "Observações do profissional" paragraph. |
| What is gated | Sessão ao vivo has two modes: "Gravar a sessão" (valid consent, refused by the server otherwise) and "Ditar resumo (só eu falo)" (free, after the provider confirms the patient is not being recorded; audited). |
| Revocation | By the patient through the revocation link (date of birth asked again), or recorded by the provider (optional reason). Future recordings blocked at once; signed notes untouched; bell notice when the patient revoked. |
| Signed copy (differs from the spec) | `EmailMessage` has no attachments and the PDF holds the date of birth, so the signed-copy e-mail **links to the revocation page**, which shows the signed term (date of birth masked) and its hash, and takes the revocation. The PDF stays in the private bucket (provider download only). |
| Namespace | `Anamnys.Server.RecordingConsents` (folder `RecordingConsent/`): the plural avoids the clash with the `RecordingConsent` entity. |

---

## 3. Data and migration

Migration `design/migrations/2026-11-15-recording-consent.sql` (idempotent, one transaction, mirrored in
`anamnys-db-script.sql` and appended to the chains of the other migration tests after `2026-11-12-structured-intake.sql`).
**It must be applied to any persistent environment before the new server version starts.** Archiving a patient and the
bell list read `RecordingConsents.ProviderId`, and the live-session begin check reads the table, so on an unmigrated
database those existing endpoints fail (`42703: column ... does not exist`) and the dictation audit row is refused by
`AuditEntries_subject_ck` (`23514`). The dev-database suites that touch them fail for the same reason until it is
applied. The migration:

- `RecordingConsents` (schema existed): adds `ProviderId` (backfilled from the patient, NOT NULL, FK `Providers` plus
  composite FK (`PatientId`, `ProviderId`) to `Patients`, CASCADE), `ProviderNote` (max 1000), `TermText`, `TermSha256`,
  `SignerRole` (`patient|guardian`), `SignerName` (200), `GuardianRelationship` (100), `AssentNote` (500),
  `SignTokenHash`, `SignTokenExpiresAt`, `FailedBirthDateCount` (check 0..5), `RevokeTokenHash`, `RevokedBy`
  (`patient|provider`), `RevokeReason` (500), `ExpiredReason` (`link|discharge|adulthood|new_version`),
  `DocumentObjectKey`, `CreatedAt`, `UpdatedAt`. `SignatureEvidence` holds `{ birthDateMatched, termSha256,
  termVersion, signerRole }`. Partial unique index `RecordingConsents_Patient_active_key`: at most one `SENT` or
  `SIGNED` consent per patient. Token hashes unique. `TreatmentPlanId` stays null (consent is per patient in care).
- `ConsentEvents`: `ActorKind` (`provider|signer|system`, default `system`); `Reason` holds codes only
  (`^[a-z_]{1,40}$`), never free text from the patient.
- Audit actions added to `AuditEntries_subject_ck`: `consent_sent`, `consent_signed`, `consent_revoked`,
  `consent_expired`, `consent_assent_recorded`, `dictation_confirmed`.
- Notification kinds `consent_signed` and `consent_revoked` (bell items of subject `recording_consent`).

No trigger is added.

---

## 4. The term

`ConsentTerm.Render(version, data)` builds the Portuguese text from the versioned template `v1` in code (`RenderV1`
prints the literal `Versão do termo: v1`, not the current version). Mandatory clauses, with no provider control over
them: parties (patient name and date of birth; guardian name and relationship for a minor, filled at signing; provider
name, CRP number and region), purpose (audio recorded only so the provider can transcribe it and draft the clinical
record; the provider reviews and signs every note), what is captured (audio only), processing (transcription on
Anamnys's own server, no external AI service), destruction (audio destroyed right after transcription, also on
failure, never in files or backups), what is kept (transcript as source material, never exported or shared, and the
signed note, for the retention period, CFP at least 5 years), access (only the provider; the patient may ask for access
to the record), voluntary (refusing or revoking does not affect care), revocation (at any time, stops future recordings
at once, does not delete signed notes), minors (guardian consents, the minor's assent is recorded by the provider; at
18 a new consent is asked), and date and place of signature (place = the provider's `PracticeCity` when set). Plus the
optional "Observações do profissional" paragraph.

How the signed text is built:

- The text is rendered at sending and stored (`TermText`) with its SHA-256 (`TermSha256`). The preview route returns
  the text **without** the observations (free text stays out of a GET); the UI appends the paragraph.
- The public signing page shows that stored text with the date of birth masked ("data de nascimento confirmada por quem
  assina"); the date of birth is never shown on a public page. The stored/PDF text has it.
- At signing, `ConsentTerm.Complete` fills only the open lines (guardian line, date and place, signature) of the **stored**
  text, so the signer signs what was shown. Exception: if the patient's name or date of birth changed since sending
  (the `Paciente:` line no longer matches, `ConsentTerm.NamesPatient`), the term is rendered again from the record, so
  the signed text states the current data (no 410; the signer types the current date of birth). The hash of the text
  actually signed is recorded.
- The PDF (QuestPDF, `ConsentPdf`) carries the text, signer name, role, relationship, date-time, version and hash.

Sending requires (400 codes): `profile_incomplete` (provider name, CRP number **and region**), `patient_email_missing`,
`patient_birthdate_missing`, `patient_archived`; and no consent already `SENT`/`SIGNED` (409 `consent_active`, except
resend and renew).

---

## 5. States and rules

States (stored upper case, returned lower case): `NOT_REQUESTED` (shown as `none`), `SENT`, `SIGNED`, `REVOKED`,
`EXPIRED`. Every transition writes a `ConsentEvents` row (from, to, time, actor kind, reason code).

| Transition | Who | Notes |
|---|---|---|
| (new row) none to SENT | provider | send, also after REVOKED/EXPIRED: inserts a new consent (event `NOT_REQUESTED` to `SENT`); the REVOKED/EXPIRED row stays as history; new link, e-mail |
| SENT or EXPIRED (link) to SENT | provider | resend, on the same row; the previous link dies; refused (409 `outdated_version`) when the consent is of an older term version (send a new term, or renew) |
| SENT to SIGNED | signer | public link; PDF stored; signed-copy e-mail; bell |
| SIGNED to REVOKED | patient (link) or provider | patient revocation only from SIGNED; provider also from SENT (cancels the link) |
| SENT/SIGNED to EXPIRED | system or provider | `link` (past `SignTokenExpiresAt`), `discharge` (archived), `adulthood` (signed by a guardian, patient now 18+), `new_version` (renew) |

**Capture rule.** `ConsentRules.CanRecordSession(consent, patient, now)` returns `(Allowed, Reason)` with reasons
`ok|none|sent|revoked|expired|assent_missing|archived`. Allowed only when the consent is `SIGNED`, the patient is not
archived and, if the patient is a minor today, `AssentRecordedAt` is set. It applies the due-expiry evaluation itself,
so a stale `SIGNED` never allows capture.

**Expiry is lazy** (there is no background job): reading a consent applies due expiries.
`RecordingConsentService.GetEffectiveAsync` returns the active consent else the latest, persists a due expiry with one
SQL statement (state, event and audit; guarded by the state and by the `UpdatedAt` that was read, so a resend or
signature committed meanwhile wins) and does not touch the caller's change tracker (safe from the hub). The event is
dated when it really happened (link deadline, `ArchivedAt`, midnight of the 18th birthday), never after now.

**Archiving** (`PatientRecords.SetArchivedAsync`) runs in one transaction: `ExpireOnArchiveAsync` loads the `SENT` and
`SIGNED` rows `FOR UPDATE`, so a signature in flight finishes first and is then expired with its real from-state (or
the signature waits and finds `EXPIRED`, answering 410). Reason `discharge`, actor provider.

**New version.** When the current template version is newer than the consent's, the card shows "Nova versão do termo";
"Pedir de novo" (renew) expires the old consent (`new_version`) and sends a new one. A signed consent of an older
version keeps allowing capture until then.

**Assent.** Provider route, only for a minor with a `SENT` or `SIGNED` consent; the date must not be in the future nor
before the practice day the consent was first requested (400 `invalid_date`); 400 `not_minor` otherwise.

**Send, resend and renew are all-or-nothing with the e-mail:** the write and the e-mail run in one transaction; a failed
send rolls back (no `SENT` consent without its e-mail, no audit). The token is made before the execution strategy so a
retry sends the same link.

---

## 6. Capture rule in the live session

The hub's `BeginRecording(recordingId, patientId, appointmentId, format, language, mode, dictationConfirmed)` takes the
mode `session` or `dictation` (exact, case-sensitive; anything else is `invalid`).
`LiveRecordings.CheckBeginAsync` checks in this order: format and mode, patient (`not_found`), archived, appointment,
gate.

- `session`: `GetEffectiveAsync` plus `CanRecordSession`; refusal `consent_required` with the reason.
- `dictation`: needs `dictationConfirmed = true`, else `dictation_not_confirmed`; then it writes the audit row
  `dictation_confirmed` (actor the provider, value = the patient id only) and saves.

The ack is `{ status, reason }` with statuses `ok`, `invalid`, `not_found`, `archived`, `forbidden`, `consent_required`
(reason `none|sent|revoked|expired|assent_missing|archived`) and `dictation_not_confirmed` (reason null).

Consequences of the contract: the gate is checked **when a recording begins**. A consent revoked in the middle of a
recording does not stop the running recording, but a reconnect (the client calls `begin` again) is refused with
`consent_required` and the held audio is dropped. A dictation recording writes one `dictation_confirmed` row per begin,
so per reconnect too (both listed in section 15).

---

## 7. Public links

Routes under `/api/public/recording-consents`, rate limited with the `public` policy, no authorization.

- **Token:** 32 random bytes, only the SHA-256 stored (unique). The signing link lives 7 days. Every unknown, used,
  expired, revoked or exhausted link answers the same **410** "Este link não é mais válido." (malformed tokens too).
- **Sign:** `GET {token}` returns the term text (date of birth masked) and whether a guardian is expected;
  `POST {token}` takes `{ birthDate, signerName, relationship?, accepted: true }`. Success: 204, state `SIGNED`, PDF
  stored, signed-copy e-mail, bell notice. Wrong date of birth: 400 `birthdate_mismatch` and `FailedBirthDateCount` + 1;
  the 5th wrong answer returns 410 itself and the link is dead. 400 `invalid` with field codes when the body is wrong;
  signer name and relationship must be one line: control, format and line/paragraph separator characters are refused
  (`signer_name_single_line`, `relationship_single_line`). 503 `unavailable` when storage fails.
- **Revoke:** `GET/POST revoke/{token}`: the page shows the signed term (date of birth masked) and its hash; the POST
  asks for the date of birth again. The counter is shared with signing but reset to 0 at signing, so the revocation link
  has its own 5 attempts. Success: `REVOKED` (by patient), revocation token cleared, bell notice.
- **Two signatures at once:** one gets 204, the other 410 (row lock); one event, one bell notice.
- **Link base:** `PatientPortal:BaseUrl` (`PublicConsentEndpoints.PortalBaseUrl`); the request host is used only in
  Development. The server refuses to start outside Development without an absolute https `PatientPortal:BaseUrl`
  (`ShareNetwork.RequirePortalUrl`), so the "no link" branch is unreachable in a started server.
- **PDF at signing:** stored before the save; if the save fails, the object is deleted (logged on failure) and the error
  rethrown. A failed commit after a successful save can still leave an orphan.

---

## 8. E-mails

Through the existing `Email` sender (`ConsentEmails`), no clinical content: names, provider, the link. The date of birth
and the observations never appear. Wording differs for an adult and for a minor's guardian.

- **Request:** the signing link (`/patient/consentimento/{token}`).
- **Signed copy:** a link to the revocation page (`/patient/consentimento/revogar/{token}`), not an attachment (see
  section 2). If this e-mail fails (or there is no sender) it is logged with the consent id and the exception type and not
  retried; the consent stays `SIGNED`. The revocation link then exists nowhere else (gap, section 15).

---

## 9. PDF

`ConsentPdf` (QuestPDF) builds the signed term: the signed text, signer name, role, relationship, date-time, version and
the SHA-256. Stored in the private bucket (`DocumentObjectKey`); read only by the provider through
`GET recording-consents/{id}/document` (streamed, `no-store`). Storage failures at signing and at download are logged
(consent id and exception type) and never include the content. The PDF is deleted by discard (section 12).

---

## 10. Endpoints and codes

Provider (`/api/phi/providers/me`, provider-scoped, 401 first, foreign ids 404, `no-store`):

| Route | Answer |
|---|---|
| `GET patients/{patientId}/recording-consent` | current consent after expiry evaluation, or `{ state: "none", canRecord: false, isMinor }`; plus `events` |
| `GET patients/{patientId}/recording-consent/preview` | rendered term text (without observations) and `sendBlockedBy` |
| `POST patients/{patientId}/recording-consent` | `{ providerNote? }` (max 1000) -> 201; sends the e-mail |
| `POST recording-consents/{id}/resend` | `SENT` or link-expired: new link, new e-mail |
| `POST recording-consents/{id}/assent` | `{ date, note? }` |
| `POST recording-consents/{id}/revoke` | `{ reason? }` |
| `POST recording-consents/{id}/renew` | new version: expire and send |
| `GET recording-consents/{id}/document` | signed PDF |

The view carries `id, state, version, currentVersion, sentAt, signedAt, signerRole, signerName, guardianRelationship,
assentRecordedAt (yyyy-MM-dd), assentNote, revokedAt, revokedBy, revokeReason, expiredReason, linkExpiresAt, isMinor,
canRecord, reason, providerNote, hasDocument, sendBlockedBy` and events `{ fromState, toState, at, actorKind, reason }`.

Codes: 400 `profile_incomplete`, `patient_email_missing`, `patient_birthdate_missing`, `patient_archived`,
`invalid_note`, `invalid_date`, `not_minor`; 409 `consent_active`, `invalid_state` (assent, revoke or resend on a
`REVOKED`/`EXPIRED` consent), `not_outdated` (renew without a newer version), `outdated_version` (resend of an older
term version); 502 `send_failed`; 503
`email_not_configured`, `storage_unavailable`. Public routes: section 7. The routes are documented in
`anamnys-aspire.Server.http`.

---

## 11. UI

Provider (`apps/provider/src/components/recordingConsent/`):

- Patient page, tab Avaliação: card "Consentimento para gravação" (`RecordingConsentCard`) with the state, dates, signer,
  capture reason, the "Nova versão" notice, the actions, the minor's assent form (date up to today, optional note),
  revoke with reason, PDF download and the event history. Actions per state: none/revoked/expired (not link, or link of an older
  version) = Enviar termo; sent/expired (link) of the current version = Reenviar link; sent or signed with an older
  version = Pedir de novo; revoke from sent or signed.
  Send, resend and renew are disabled with the reason (`sendBlockedBy`). An archived patient is read-only except revoke
  and download.
- `SendConsentModal`: term preview, observations field appended as the last paragraph, send.
- Header badge "Gravação autorizada" when `canRecord`.
- Sessão ao vivo (`SessionModeChoice`): "Gravar a sessão" is enabled only when `canRecord`; otherwise it is disabled
  with the reason and an "Enviar termo" link to the patient's Avaliação tab (also while the consent loads or fails to
  load). "Ditar resumo" shows a confirmation checkbox, cleared each time a recording starts and on patient change;
  the start button needs it. `consent_required` and `dictation_not_confirmed` are fatal errors with their own message;
  `consent_required` refetches the consent.
- Bell: "consent_signed" and "consent_revoked" notices open the patient's Avaliação tab (plain text without a patient id).

Patient app (public, no login, `apps/patient/src`): `routes/consentimento.$token.tsx` (term, date of birth, full name,
relationship for minors, "Li e concordo", sign) and `routes/consentimento.revogar.$token.tsx`; `components/consent/`.
Plain state and direct calls (no react-query), so the token and the date of birth never enter a cache; the date of birth
is cleared after submit and never rendered or logged. A 410 or 404 shows the single message "Este link não é mais
válido.". Copy in `pt.json` under `recordingConsent.*`.

---

## 12. Discard and archive

Discard (F-06, `DiscardService`) deletes the patient's consents (events cascade), the signed PDF object, and the bell
notices of subject `recording_consent`. Archiving expires the consent (section 5). Revoking never deletes notes.

---

## 13. Audit and privacy

| Action | Value (ids and codes only) |
|---|---|
| `consent_sent` | `{consent} sent`, `resend`, or `renew {oldId}` |
| `consent_signed` | `{consent} patient` or `guardian` |
| `consent_revoked` | `{consent} patient` or `provider` |
| `consent_expired` | `{consent} link`, `discharge`, `adulthood` or `new_version` |
| `consent_assent_recorded` | `{consent}` |
| `consent_document_downloaded` | `{consent}` (the signed PDF was returned to the provider) |
| `dictation_confirmed` | the patient id, one row per begin |

`FieldChanged` is `recording_consent`; the actor is the provider for provider actions and null for signer and system
actions. `ProviderNote` is part of the term text, so it appears on both public pages (sign and revoke) and in the PDF (the send
modal warns the provider: no clinical information). `RevokeReason` and `AssentNote` never leave the provider UI. None of
the three enters audit values, event reasons or e-mails.

---

## 14. Testing

New tests in `anamnys-aspire.Tests/RecordingConsent/` (`ConsentScene` plus `ConsentRulesTests`,
`RecordingConsentServiceTests`, `RecordingConsentEndpointTests`, `RecordingConsentEdgeTests`; 60 tests, scratch
databases), `LiveSession/LiveRecordingModesTests.cs` and `Data/RecordingConsentMigrationTests.cs`; `LiveRecordingsTests`
and `TranscriptionHubTests` use the new signatures; 20 migration-test chains gained the new file. Covered: every state of
the capture rule, lazy expiry (link, discharge, adulthood, persisted), archive expiry and the archive-versus-signature
ordering, renew and resend, one-time link, wrong date of birth counted and dead at 5, the same 410 for every dead link,
only the token hash stored, signed text hash, signed text after a rename or birth date correction, single-line name
guard, PDF content, e-mails without date of birth or notes, a failed e-mail leaving nothing, revocation by link and by
provider with the signed note untouched, bell notices, sending preconditions, scoping, 401, `no-store`, rate-limit
metadata, discard, and the hub/live-session modes (refusals by reason, dictation confirmation audited).

Tests that read the **dev database** (some `PatientRecordsTests`, `PatientInvitationsTests`, `ProviderNotificationsTests`,
`LiveRecordingsTests.CheckBegin_OwnPatient_IsOk`, two `TranscriptionHubTests`) fail until the migration is applied there;
stop Aspire before running them. Provider and patient `npm run build` and `npm run lint` pass. Not exercised in a browser
or against a live server.

---

## 15. Deployment, where things are, and gaps

Deployment: apply `design/migrations/2026-11-15-recording-consent.sql` to every persistent environment before the new
server starts; outside Development `PatientPortal:BaseUrl` must be an absolute https URL; the e-mail sender must be
configured for sending (503 `email_not_configured` otherwise).

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-11-15-recording-consent.sql` |
| Entities | `anamnys-aspire.Server/Data/Entities/RecordingConsent.cs` (`RecordingConsent`, `ConsentEvent`) |
| Server code | `anamnys-aspire.Server/RecordingConsent/` (`ConsentTerm`, `ConsentRules`, `ConsentTokens`, `ConsentPdf`, `ConsentEmails`, `RecordingConsentService`, `RecordingConsentEndpoints`, `PublicConsentEndpoints`) |
| Hooks | `LiveSession/LiveRecordings.cs`, `LiveSession/TranscriptionHub.cs`, `Patients/PatientRecords.cs`, `Retention/DiscardService.cs`, `Notifications/ProviderNotifications.cs` |
| Provider UI | `apps/provider/src/components/recordingConsent/`, `routes/_app/live-session.tsx`, `lib/signalr.ts` |
| Patient UI | `apps/patient/src/routes/consentimento.$token.tsx`, `consentimento.revogar.$token.tsx`, `components/consent/` |
| API clients | `packages/shared/src/api/recordingConsent.ts`, `publicConsent.ts` |
| Tests | `anamnys-aspire.Tests/RecordingConsent/`, `LiveSession/LiveRecordingModesTests.cs`, `Data/RecordingConsentMigrationTests.cs` |

Limits and known gaps (full list in `design/gaps/2026-10-03-patient-flows-gaps.md`, section "Consentimento para
gravação"):

- Lawyer review of the term text (D-03): built with the caveat, unreviewed.
- The signed copy cannot be sent again (no "reenviar cópia"); the patient can still revoke through the provider.
- No heads-up before a minor turns 18 (the consent just expires, lazily).
- Consent is per patient, not per treatment plan (`TreatmentPlanId` stays null).
- No signing without e-mail (paper term).
- A consent revoked mid-recording does not stop the running recording; only a reconnect is refused.
- The dictation audit row is written per begin, so per reconnect.
- Tab audio capture (F-25) belongs to G2.
- The Confluence documentation is updated separately.
