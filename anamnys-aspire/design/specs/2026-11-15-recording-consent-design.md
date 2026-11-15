# Recording Consent (G1: F-23 + F-24)

**Date:** 2026-11-15
**Status:** Approved for implementation
**Scope:** Backlog F-23 (recording consent term) and F-24 (consent state machine), the first delivery of Group G
(session capture). G2 (F-26 transient audio proof, F-25 tab capture), G3 (F-27 pseudonymisation) and G4 (F-28
transcript as source) get their own specs.

Done when (backlog): the provider issues the term, the patient signs, the signed artefact is filed in the record; the
capture control is DISABLED (not merely discouraged) in every state except SIGNED, enforced on the server.

Built "with a caveat" (user, 2026-11-03, D-03): no lawyer review before shipping, provided the term states explicitly
and with valid data the names of every party (patient, provider, guardian for minors), the date it was given and the
other items Res. CFP 13/2022 asks for; given once per patient and treatment, not per session.

## 1. Decisions (agreed with the user, 2026-11-15)

| Topic | Decision |
|---|---|
| How the patient signs | A signing link (no login) on a public page of the patient app, like the appointment confirmation. |
| Identity | The link goes by e-mail to the patient's contact e-mail. To sign, the signer types the patient's date of birth (must match the record) and their full name as signature. Recorded: date-time, term version, SHA-256 of the accepted text, "date of birth matched". No IP. One-time link, 7 days, resendable. |
| Minors (under 18 by the record's date of birth) | The guardian signs through the link (sent to the contact e-mail) with their own name and relationship. The minor's assent is recorded by the provider in session (date + optional note). Capture is allowed only with both. |
| Validity | Until revoked, while the patient is in care. Expires when the patient is archived (discharge), when a patient who signed as a minor turns 18, or when the provider asks again after a new term version. |
| Term text | Fixed, versioned Anamnys text with every mandatory clause; names, CRP and dates filled in; one optional "Observações do profissional" paragraph. |
| What is gated | Sessão ao vivo gets two modes: "Gravar a sessão" (requires a valid consent, refused by the server otherwise) and "Ditar resumo (só eu falo)" (free, after the provider confirms the patient is not being recorded; audited). |
| Revocation | The patient through a revocation link e-mailed with the signed copy (date of birth asked again), or the provider records it (optional reason). Future recordings blocked at once; signed notes untouched; bell notice. |

## 2. Data

Migration `design/migrations/2026-11-15-recording-consent.sql` (idempotent, mirrored in `anamnys-db-script.sql`,
migration-test chains after `2026-11-12-structured-intake.sql`). Existing tables (schema only today):
`RecordingConsents` (`PatientId`, `TreatmentPlanId`, `State` NOT_REQUESTED|SENT|SIGNED|REVOKED|EXPIRED,
`TemplateVersion`, `IssuedAt`, `SignedAt`, `SignatureEvidence`, `ValidUntil`, `RevokedAt`, `GuardianName`,
`AssentRecordedAt`), `ConsentEvents` (`ConsentId`, `FromState`, `ToState`, `At`, `ActorId`, `Reason`).

Changes:

- `RecordingConsents` gains: `ProviderId` uuid NOT NULL (FK), `ProviderNote` text (max 1000), `TermText` text (the
  rendered text the signer saw), `TermSha256` text, `SignerRole` `patient|guardian`, `SignerName` text (max 200),
  `GuardianRelationship` text (max 100), `AssentNote` text (max 500), `SignTokenHash` bytea, `SignTokenExpiresAt`,
  `FailedBirthDateCount` int default 0 (check 0..5), `RevokeTokenHash` bytea, `RevokedBy` `patient|provider`,
  `RevokeReason` text (max 500), `ExpiredReason` `link|discharge|adulthood|new_version`, `DocumentObjectKey` text,
  `CreatedAt`, `UpdatedAt`. `SignatureEvidence` holds a JSON summary (`{ "birthDateMatched": true, "termSha256": … }`).
  Partial unique index: at most one consent per patient in `SENT` or `SIGNED`. Token hashes unique.
  `TreatmentPlanId` stays null (consent is per patient while in care).
- `ConsentEvents`: `ActorKind` `provider|signer|system`; `Reason` holds codes only (no free text from the patient).
- Audit actions `consent_sent`, `consent_signed`, `consent_revoked`, `consent_expired`, `consent_assent_recorded`,
  `dictation_confirmed` (ids only).
- Notification kinds `consent_signed`, `consent_revoked` (bell).
- Discard (F-06) deletes consents, events and the PDF object.

## 3. The term

`ConsentTerm.Render(version, data)` builds the Portuguese text from a versioned template (`v1`) in code. Mandatory
clauses (no provider control over them):

1. Parties: patient full name and date of birth; for a minor, the guardian (name and relationship, filled at signing);
   provider name, CRP number and region.
2. Purpose: audio of the sessions is recorded only so that the provider can transcribe it and draft the clinical
   record of the session; the provider reviews and signs every note, and the recording is not used for anything else.
3. What is captured: audio only (in-person or online sessions); no video, no screen.
4. Processing: transcription runs on Anamnys's own server; no external AI service receives the audio or the text.
5. Destruction: the audio is destroyed right after transcription, including when transcription fails; it is never
   stored in files or backups.
6. What is kept: the transcript (source material, never exported or shared) and the signed note (the official record),
   kept for the record retention period (CFP: at least 5 years).
7. Access: only the provider; the patient may ask for access to their record.
8. Voluntary: refusing or revoking does not affect care; sessions continue without recording.
9. Revocation: at any time, by the link sent with the copy or by asking the provider; it stops future recordings
   immediately and does NOT delete notes already signed.
10. For minors: the guardian consents and the patient's own assent is asked for and recorded by the provider.
11. Date and place of signature (filled at signing; place = provider's practice city when set).

Plus the optional "Observações do profissional" paragraph. The rendered text is stored on the consent and hashed; the
PDF (QuestPDF) carries the text, the signer name, role, relationship, date-time and the hash.

Sending requires: provider name and CRP filled (400 `profile_incomplete`), patient contact e-mail (400
`patient_email_missing`), patient date of birth (400 `patient_birthdate_missing`), patient not archived (400
`patient_archived`), no consent already `SENT`/`SIGNED` (409 `consent_active`, except resend).

## 4. Rules

- **Capture rule.** `ConsentRules.CanRecordSession(consent, patient, now)` — true only when the consent is `SIGNED`,
  the patient is not archived, and, if the patient is a minor today, `AssentRecordedAt` is set. The live-session hub
  calls it when a session recording starts (mode `session`) and refuses with `consent_required` (and a reason code:
  `none`, `sent`, `revoked`, `expired`, `assent_missing`). Mode `dictation` needs `dictationConfirmed = true` in the
  start call and writes `dictation_confirmed`.
- **Expiry, lazily and by job.** Reading a consent applies due expiries: link past `SignTokenExpiresAt` in `SENT` →
  `EXPIRED (link)`; `SIGNED` with patient archived → `EXPIRED (discharge)`; `SIGNED` by a guardian and the patient now
  18+ → `EXPIRED (adulthood)`. The capture rule uses the same evaluation, so no stale `SIGNED` allows capture.
  Archiving a patient expires the consent in the same transaction.
- **New version.** When the current template version is newer than the consent's, the card shows "Nova versão do
  termo"; "Pedir de novo" expires the old one (`new_version`) and sends a new one. A signed consent of an older version
  keeps allowing capture until then.
- **Signing link.** 32-byte random token, only its SHA-256 stored; `GET /api/public/recording-consents/{token}` returns
  the text and whether a guardian is expected (no patient data beyond the names in the text);
  `POST …/{token}` with `{ birthDate, signerName, relationship?, accepted: true }`. Wrong date of birth increments
  `FailedBirthDateCount`; at 5 the link is dead. Every dead, unknown, used or expired link answers the same 410
  "Este link não é mais válido." Rate limited (the `public` policy). Success: `SIGNED`, the PDF is generated and
  stored, the signed-copy e-mail with the revocation link goes out, bell notice to the provider.
- **Revocation link.** `GET/POST /api/public/recording-consents/revoke/{token}` (date of birth required, same failure
  counting); provider route with optional reason. Both: `REVOKED`, revocation token cleared, bell notice when the
  patient revoked.
- **Assent.** Provider route, only for a minor patient with a `SENT` or `SIGNED` consent; date not in the future.
- **E-mails** go through the existing `Email` sender and contain no clinical content: names, provider, the link.

## 5. Endpoints

Provider (`/api/phi/providers/me`, provider-scoped, 401 first, foreign ids 404, `no-store`):

| Route | Answer |
|---|---|
| `GET patients/{patientId}/recording-consent` | current consent (after expiry evaluation) `{ id, state, version, currentVersion, sentAt, signedAt, signerRole, signerName, guardianRelationship, assentRecordedAt, revokedAt, revokedBy, expiredReason, linkExpiresAt, isMinor, canRecord, reason }` or `{ state: "none", canRecord: false, isMinor }`, plus `events` |
| `GET patients/{patientId}/recording-consent/preview` | rendered term text for the preview |
| `POST patients/{patientId}/recording-consent` | `{ providerNote? }` → 201; sends the e-mail |
| `POST recording-consents/{id}/resend` | `SENT` or link-expired: new link, new e-mail |
| `POST recording-consents/{id}/assent` | `{ date, note? }` |
| `POST recording-consents/{id}/revoke` | `{ reason? }` |
| `POST recording-consents/{id}/renew` | new version: expire + send |
| `GET recording-consents/{id}/document` | signed PDF, streamed, `no-store` |

Public (`/api/public/recording-consents`, rate limited, no auth): sign view/submit and revoke view/submit (section 4).

Live session: the hub's start call gains `mode` (`session` | `dictation`) and `dictationConfirmed`.

## 6. UI

- Provider, patient page → Avaliação tab: `DetailCard` "Consentimento para gravação" with the state badge, dates,
  signer, the actions of section 5 (preview modal with the observations field before sending), the minor's assent
  form, revoke with reason, download, and the event history. Send disabled with the reason when e-mail, date of birth
  or CRP is missing.
- Patient header: a small "Gravação autorizada" badge when `canRecord`.
- Sessão ao vivo: mode choice "Gravar a sessão" / "Ditar resumo (só eu falo)". Without a valid consent the session
  mode is disabled with the reason and a "Enviar termo" link; dictation asks for the confirmation checkbox.
- Bell: consent signed / revoked.
- Patient app (public, no login): `/consentimento/$token` (term, date of birth, full name, relationship for minors,
  "Li e concordo", sign) and `/consentimento/revogar/$token`; a single message for any dead link.
- Copy in `pt.json` under `recordingConsent.*`.

## 7. Testing

- State machine: every allowed transition and refused ones (409); lazy expiry (link, discharge, adulthood); archive
  expires; new version + renew.
- Capture rule: refused in every state except `SIGNED`; minor needs assent; archived refused; the hub refuses mode
  `session` without consent and accepts `dictation` only with confirmation (audited).
- Signing link: one-time, expired, wrong date of birth counted, blocked at 5, identical answer for every dead link,
  hash stored not the token, text hash recorded, PDF stored, e-mails sent (fake sender) with no clinical content.
- Revocation by link and by provider; notes untouched; bell notices.
- Sending preconditions (profile, e-mail, date of birth, archived, active consent).
- Discard removes consents and PDF; scoping; 401; migration and chains. Front: provider and patient build and lint.

## 8. Out of scope / gaps

Tab audio capture (F-25, G2); a heads-up before a minor turns 18; consent per treatment plan; signing without e-mail
(paper term); a lawyer's review of the text (D-03, built with the caveat).

## 9. Documentation

Chapter `documentation/44-recording-consent.md`; index; status (F-23, F-24); gaps; Confluence (F-23, F-24).
