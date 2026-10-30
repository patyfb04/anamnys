# Implementation status against the Notion backlog

**Checked:** 2026-10-15 against `develop` (`fda5b7b`); updated 2026-10-18 at `develop` (`f99757c`)
after the note editor and PDF export deliveries.
**Sources:** Notion "Plano de Implementação" (backlog F-01…F-67, last updated there 2026-08-15)
and "Diagramas de Sequencia" (flows 01…23). Known defects and loose ends of the shipped work
live in `design/gaps/2026-10-03-patient-flows-gaps.md`; this file only says what exists.

Legend: **done** · **partial** (some of the scope exists) · **schema only** (the table is in
`anamnys-db-script.sql` but no server code reads or writes it) · **not started**.

## Read this first: the Notion "done" marks are stale

The Notion page marks F-01, F-02 and the whole "JÁ CONSTRUÍDO (Fase 1)" list as built. That
described the earlier `backend/` + `frontend/` pair, deleted in `aee5233`. In `anamnys-aspire`
the schema still carries every table, but only the features below have code.

## "Já construído (Fase 1)" list, re-checked

| Item in Notion | Real state | Where |
|---|---|---|
| Página pública de apresentação | done | `apps/web` (product, pricing, FAQ, about, terms, privacy…) |
| Autenticação segura | done, but Keycloak + BFF cookie, not JWT in the browser | `Auth/`, `design/specs/2026-09-05-keycloak-implementation-design.md` |
| Painel do profissional (carga do dia, notas pendentes, métricas IA) | not started; placeholder page | `apps/provider/src/routes/_app/dashboard.tsx` |
| Ditado por voz + Whisper (flow 01, audio after the session) | not started; only live recording exists | — |
| Transcrição ao vivo via SignalR | done | `LiveSession/`, spec `2026-10-10-live-session-transcription-design.md` |
| Digitação direta (flow 03) | done: typed text is structured by AI or blank sections are hand-written, optionally linked to an appointment | `Notes/NoteEditing.cs` |
| Estruturação DAP / SOAP / Biopsicossocial | done (LLamaSharp, Qwen2.5 7B) for live-session transcripts | `NoteStructuring/`, spec `2026-10-12-note-structuring-design.md` |
| Fluxo de revisão e assinatura | done: section edit, sign, lock | `Notes/`, spec `2026-10-13-note-review-and-signing-design.md` |
| Motor de cobrança multipaís | not started | — |
| Avisos de documentação incompleta | not started | — |
| Exportação em PDF + S3 + URLs assinadas | done | PDF generated once (QuestPDF) and kept in SeaweedFS (self-hosted S3) instead of a cloud S3; downloaded through 1 h presigned URLs |
| Gestão de pacientes | done: list, search, record, archive, diagnoses, medications, plan objectives | `Patients/` |
| Trilha de auditoria por documento | partial: `AuditEntries` written for note mutations; no history view | `Notes/NoteAudit.cs` |
| Pipeline Hangfire + progresso SignalR | partial: progress hub done; the queue is an in-process worker, not Hangfire | `LiveSession/TranscriptionQueue.cs`, `ProgressHub.cs` |

## Backlog items

### A · Fundações

| ID | State | Notes |
|---|---|---|
| F-01 Escalas aplicadas | done | Score-only recording with three regimes, opt-in, soft removal, series chart; `Scales/`, `documentation/21-psychometric-scales.md` |
| F-02 Vocabulário de temas | done | Closed vocabulary `mh-1` (28 terms + 12 children); local-model proposals after structuring and on demand ("Sugerir temas"; cached vocabulary prefix in a 4096-token context, ≈ 4–5 s per suggestion after the first), tags editable on drafts and signed notes with audit, search by theme (term or child, signed notes), admin page with add, rename, retire, split and merge under D-01. `Themes/`, `documentation/23-theme-vocabulary.md` |
| F-03 Referências de trecho | done | Every theme tag points at an exact passage or a section of its note; the chip opens the note, highlights it and scrolls to it, from the note or from a theme search result (`?excerpt=`). Selection in a section tags it; the model quotes the passage (proposals, background locate after accepting a suggestion, "Localizar trechos"); drafts relocate references on every save; `Excerpts.RequireExcerpt` plus an architecture test for analyses. The migration deleted the old unusable `ExcerptRefs` rows once and backfilled `section` references for the existing tags. `Excerpts/`, `Themes/`, `documentation/25-excerpt-references.md`. Verified 2026-10-25: full suite 1067 passed before the final fixes; targeted suites 355 passed after. |
| F-04 Metadados da sessão | done | `Sessions` (scheduled/actual times, modality, attendance) and appointment status/cancellation are stored; a `Sessions` row is created by a live recording or a typed note linked to an appointment. Attendance statistics per patient (no migration): the appointment is the base, sessions without appointment count only when attended, past appointments with no status are "sem registro", future ones are excluded; counts, cancellation lead (median, range), interval between attended sessions (median, range, last) and days since the last one. Card "Comparecimento" on the patient record (6 m / 1 y / all, neutral wording) and a "Consultas" lane at the top of "Linha do tempo". `Attendance/`, `components/attendance/`, `documentation/29-attendance-stats.md`. No duration line; appointment changes invalidate by prefix; not in the Análises page yet, see the gaps file. |

### B · Conformidade documental

| ID | State | Notes |
|---|---|---|
| F-05 Validador CFP 006/2019 | done | all six kinds of art. 8 (declaração, atestado, relatório, relatório multiprofissional with a typed team, laudo, parecer): form per kind, conformity check, confirmations, blocking/advisory modes, signing, correction by new version, PDF export. `Documents/`, `documentation/22-psychological-documents.md` |
| F-06 Guarda e descarte | done | One retention deadline per patient record: last record (signed note, signed document, attended session or appointment) plus guard years (default 5, Res. CFP 001/2009, configurable upward in Settings > Guarda). Page "Conformidade" (`/compliance`) with three date-ordered blocks: unsigned notes, records within 90 days of the deadline or past it, documents with compliance pending. Daily job creates one bell notification per new item (deduplicated, `Notifications:WorkerEnabled`), no e-mail. Irreversible discard of an expired record with no future appointment, confirmed by typing the patient's name: generates a disposal term (PDF), deletes the clinical content and the per-note audit rows, keeps a minimal patient row, the term and the `patient_discarded` audit row; object deletes after commit with retries; a database trigger on 18 tables refuses new rows for a discarded patient (404 `discarded`). Terms are in the F-60 export. `Retention/`, `components/compliance/`, `documentation/30-retention-panel.md`, migration `2026-10-28-retention-panel.sql`. Earlier export ZIPs and downloaded copies keep the record; the Keycloak user is untouched; see the gaps file. |

### C · Documentos externos

| ID | State | Notes |
|---|---|---|
| F-07 Upload | done | PDF/JPG/PNG up to 20 MB checked by magic bytes, with kind, date and description; stored in the private bucket (`external/{providerId}/{documentId}`); scanned by a self-hosted ClamAV container before opening (infected: object deleted, row kept as blocked; scanner unreachable: stays pending and retries); open only when clean with a one-hour inline link; soft delete; read-only for archived patients; audited. Patient record card and Documentos > Recebidos tab. `ExternalDocuments/`, `documentation/26-external-documents.md`, ClamAV cost in `documentation/20-environment-configuration.md` 2.4b. Not in the F-60 export yet. |
| F-08 OCR + fatos declarados | done | Clean documents are read in process (embedded PDF text, Tesseract OCR for scans, up to 20 pages); the local model proposes issuer, registration, issue date, ICD, medication and conclusion, each with page and a literal quote located on the page; the provider confirms, corrects (original kept) or discards; only confirmed or corrected facts count. Interactive model use goes before extraction (priority gate); deleting a document deletes its facts. Provider app: read badge, "Ver fatos" modal, "Fatos de documentos" card. `DocumentReading/`, `DocumentFacts/`, `documentation/27-document-facts.md`, Tesseract setup in `documentation/20-environment-configuration.md` 2.4c. Crash-loop guard on extraction (`ExtractAttempts`, sweep gives up after 2) and a 50 MP decode cap on images. Recall is uneven, see the gaps file. |
| F-09 Linha do tempo de medicação | done | A medication fact (F-08) opens a prefilled form (text rules: drug, dose, posology, start, end) and becomes a medication record linked to the fact: new, dose change (closes the old record, same treatment) or stop; the fact is confirmed or corrected in the same transaction and audited (`medication_from_fact`). Card "Linha do tempo" on the patient record: medication lanes with origin (typed, document, deleted document) above scale series on one date axis, 6 m / 1 y / all, neutral colours, no relation stated. `Medications/`, `components/timeline/`, `documentation/28-medication-timeline.md`, migration `2026-10-27-medication-timeline.sql`. The patient record cards from Escalas down (Escalas, Linha do tempo, Documentos, Documentos recebidos, Fatos de documentos) collapse: closed by default, state remembered per browser. Stops never show "no prontuário"; not in the Análises page yet, see the gaps file. |

### D · Camada longitudinal

| ID | State | Notes |
|---|---|---|
| F-10 Camada de agregação | done | One call returns a patient's theme history: signed notes only (date = actual session start, else appointment, else signing day), tags not removed and not untouched AI proposals, one lane per term with parents always included, retired/split terms keep their old sessions with `supersededBy` (D-01), origin (excerpt, section, quote) on every occurrence, co-occurrence counts per session, 6-signed-note minimum. `GET providers/me/patients/{id}/analysis`. `Analysis/`, `documentation/31-case-analysis.md`. No migration. |
| F-11 Linha do tempo de temas | done | "Análises" page (`/analyses`, patient id only in the URL): patient picker, period 6 m / 1 y / all, card "Temas por sessão" (sessions as columns, lanes with collapsible children, sort by frequency/recency/alphabet, "substituído por X"), cell modal with the stored quote, link to the highlighted passage and "na mesma sessão", card "Aparecem juntos com mais frequência", and the "Linha do tempo" card reused with the page period. `components/analysis/`, `documentation/31-case-analysis.md`. Build and lint only; see the gaps file. |
| F-12 Ausência de temas | done | A term recorded in at least 3 signed sessions and in none of the last N (default 4, configurable 2 to 20 per provider) is described on the "Análises" page: card "Temas sem registro recente" above the grid (dates, real session count, days; click opens the last passage) and a neutral "sem registro nas últimas N" mark on the lane. Whole signed history regardless of the period, superseded/retired terms excluded, parent flagged only when no child was recorded either, 6-session minimum, no notifications. `absences` and `absenceSessions` on the analysis endpoint; `GET/PUT providers/me/settings/analysis`; settings section "Análises". Migration `2026-10-29-theme-absence.sql` (Providers.AbsenceSessions). `Analysis/ThemeAbsence.cs`, `documentation/32-theme-absence.md`. Build and lint only for the UI; see the gaps file. |
| F-13, F-15, F-16 | not started | depend on F-01…F-03 |
| F-14 Plano terapêutico e lacunas | partial | treatment plan objectives CRUD on the record (`TreatmentPlans`, `PlanObjectives`); no comparison with registered themes, `FollowUpItems` schema only |

### E · Compartilhamento protegido

| ID | State | Notes |
|---|---|---|
| F-17, F-18 | schema only | `DisclosureAuthorizations`, `ShareLinks`, `ShareAccesses` |

### F · Convênios

| ID | State | Notes |
|---|---|---|
| F-19 … F-22 | schema only | `Insurers`, `InsurerAuthorizations`, `TissGuides`, `BillingCodes`, `Denials`, `DenialFlags` |

### G · Captura de sessão (blocked by D-03)

| ID | State | Notes |
|---|---|---|
| F-23, F-24 | schema only | `RecordingConsents`, `ConsentEvents` |
| F-25 Captura de áudio da aba | not started | the live session captures the provider's microphone, not a tab |
| F-26 Áudio transitório | partial | live-session audio is kept only in memory buffers and dropped after transcription; `AudioDestructionLogs` is not written and there is no test proving nothing survives |
| F-27 Pseudonimização | not started | |
| F-28 Transcrição como fonte | partial | the transcript is kept in `Notes.RawTranscript`; `Transcripts` table unused; no export includes the transcript (PDF built only from signed sections) |

### H · Agenda e mensageria

| ID | State | Notes |
|---|---|---|
| F-29 Agenda própria | partial | delivery 1 done (create, reschedule, statuses, day/week view, time zone column). Missing: availability rules and exceptions, recurrence (`AppointmentSeries`), month view, Google mirror (`CalendarConnections`) |
| F-30 Lembretes | partial | e-mail confirmation with one-time link, automatic cancellation, reminder 1 h before, cancellation e-mails, in-app bell. WhatsApp not built |
| F-31 Anamnese estruturada | schema only | `IntakeTemplates`, `IntakeQuestions`, `IntakeResponses` |
| F-61 Portal do paciente | partial | separate `PatientAccounts`, invitation by provider, self-registration, own appointments, "Confirmar"; separate `patients/me` endpoints. Login is Keycloak with password, not the passwordless token in the spec; no terms acceptance record; LGPD rights (unlink, delete account) missing |
| F-62 Marcação pelo paciente | not started | `BookingHolds` schema only; `BookingPolicies` is used today only for the confirmation settings |
| F-63 Perfil público | partial | provider profile edit (`providers/me/profile`) and a provider page inside the patient portal; no public card, offerings, prices, CRP gate or directory opt-in (`ProviderProfiles`, `ServiceOfferings`, `ProfileListingEvents`, `SpecialistTitles` schema only) |
| F-64 Busca no diretório | schema only | `FocusAreas`, `ProviderFocusAreas` |

### H2 · Sala própria (D-06 open)

| ID | State | Notes |
|---|---|---|
| F-38 … F-43 | not started | `RoomSessions` schema only |

### J · Assinatura e cobrança (blocks launch)

| ID | State | Notes |
|---|---|---|
| F-50 … F-59 | schema only | `Plans`, `PlanFeatures`, `Subscriptions`, `WebhookEvents`, `UsageCounters` |
| F-60 Exportação integral | done | ZIP with offline HTML, PDFs of signed notes and documents, JSON + CSV and a LEIA-ME; no transcripts, no audit values; kept 7 days; Ajustes section + bell notification. Covers active accounts only: the 60-day window after cancellation and deletion belong to F-57. `Exports/`, `documentation/24-account-export.md` |

### I · Fase 3 and items from the regulatory review

| ID | State | Notes |
|---|---|---|
| F-44, F-45 | schema only | `Dossiers`, `DocumentHashes` |
| F-46 Backup | not started | |
| F-47 Marcadores de fala | schema only | `SpeechMarkers` |
| F-48, F-49 | not started | |
| F-65, F-66, F-67 | not started | |

## Sequence diagrams (flows)

| Flow | State |
|---|---|
| 01 Ditar e assinar | partial: review and sign done; audio upload after the session not built |
| 02 Transcrever ao vivo | done; buffer survives a reconnect within its expiry (better than the diagram's "no resume") |
| 03 Digitar direto | done |
| 04 Captura com termo | not started (D-03) |
| 05, 06 | not started |
| 07 Ler documento e fatos declarados | done (F-08) |
| 08 | not started |
| 09 Linha do tempo de medicação | partial: medication and scales on one axis (F-09), themes per session and co-occurrence on the Análises page (F-10, F-11), themes without recent record (F-12); F-13 pending |
| 10, 11 | not started |
| 12 Disponibilidade e agenda | partial (appointments only) |
| 13 Google | not started |
| 14 Lembrete | partial (e-mail, not WhatsApp) |
| 15, 16 | not started |
| 17 Sala própria | not started (D-06) |
| 18 | not started |
| 19 Guarda e descarte | built (F-06) |
| 20 … 22 | not started |
| 23 Exportar conta | done for active accounts (F-60); the 60-day window after cancellation and the deletion wait for F-57 |

## Built but not in the backlog

- Admin app registration and staff approval (owners realm), break-glass grants schema.
- Account self-service: profile, e-mail change, password and 2FA through Keycloak actions.
- Keycloakify login theme.
- Dev clinical seed.
- Note list (`/note-editor`) with status filters and patient-name search (POST body, never in a URL).
- Documents list (`/documents`, sidebar "Documentos") with status filters and patient-name search
  (POST body), "Novo documento" there and on the patient page.

## Next, in priority order (agreed 2026-10-24)

Everything that does not depend on subscription and billing comes first; billing waits for D-07.

1. **Close groups A and B first (user, 2026-10-27):** ~~F-04 (attendance statistics)~~ done, ~~F-06 (retention panel)~~ done; groups A and B are closed.
   **Next = analyses:** ~~F-10 (aggregation layer)~~, ~~F-11 (theme timeline)~~ and ~~F-12 (absence
   detection)~~ done; now F-13 (scale trajectory with events), F-14 (plan adherence and follow-ups).
2. F-15 (patient quotes), F-16 (natural-language search); F-17/F-18
   (protected sharing); F-45 → F-44 (dossier), F-46 (backup); F-29 rest (availability, recurrence,
   month view, Google), F-31 (structured intake), F-61 rest, F-62, F-63, F-64, F-30 (WhatsApp).
3. Blocked by decisions, not billing: F-23…F-28 (D-03), F-38…F-43 (D-06), F-19…F-22 (D-04).
4. **Last — Assinatura e cobrança, F-50 … F-59 (rest of the launch gate)** — not started; blocked by D-07
   (Paddle answers pending, per the Notion). F-60 is done (D-08 decided 2026-10-23: export available at any
   time while the account is active and for 60 days after cancellation, then the data is deleted;
   site and Terms say exactly that). Only the active-account half exists: F-57 (cancellation) must add the
   60-day window and the deletion. These must exist before the
   first paying subscriber.

Verified 2026-10-23: full suite 884 total, 0 failed, 3 skipped (F-02 included, with the
suggestion cache and warm-up). The F-02 migration and the theme dev seed are applied to the local
dev database; any other persistent environment needs `2026-10-21-theme-vocabulary.sql`.
Verified 2026-10-24: full suite 935 total, 0 failed, 3 skipped (F-60 included; export tests 53/53
after the final fixes). `2026-10-23-account-export.sql` is applied to the dev database; any other
persistent environment needs it.
Verified 2026-10-25 (F-07): full suite 1180 passed, 0 failed, 3 skipped before the final review fixes; external-document suites 108 passed after them. `2026-10-25-external-documents.sql` must be applied to any persistent environment.
Verified 2026-10-27 (F-08): full suite 1401 total, 1 failure (an export endpoint test racing the Vite dev server start-up, fixed in the test fixture); after the final fixes 262 + 286 targeted tests passed, 0 failed. `2026-10-26-document-facts.sql` must be applied to any persistent environment, and re-run on a database that had an earlier version of it (the final-review wave added `ExternalDocuments.ExtractAttempts`).
Verified 2026-10-27 (F-09): full suite 1465 total, 2 failed, 3 skipped; both failures were the PDF text tests picking up the image-only OCR sample (`-escaneado`), now excluded from them (9/9 passed); medication suites 57/57 after fix round 1; provider build and lint clean after the final UI fixes. `2026-10-27-medication-timeline.sql` must be applied to any persistent environment.
Verified 2026-10-27 (F-04): full suite 1480 total, 0 failed, 3 skipped, after the final review fixes; provider build and lint clean. No migration.
Verified 2026-10-28 (F-06, with F-04 in the same tree): full suite 1540 total, 0 failed, 3 skipped, after the final review fixes; provider build and lint clean. `2026-10-28-retention-panel.sql` must be applied to any persistent environment (re-run it where an earlier version was applied: it added the insert trigger and `DisposalTerms.RetentionUntil` later). Merged into develop on 2026-10-28 (commit 8219ab7). Dev seeds: `dev-seed/attendance-samples.sql` (appointment history) and `dev-seed/retention-samples.sql` (expired and near-expiry records).
Verified 2026-10-29 (F-10 + F-11): full suite 1549 total, 0 failed, 3 skipped; provider build and lint clean after the final review fixes. No migration. Merged into develop on 2026-10-29 (commit ea619c3); the patient picker lists up to 100 patients across the page width. Dev seed: `dev-seed/analysis-samples.sql`.
Verified 2026-10-29 (F-12): full suite 1573 total, 0 failed, 3 skipped; provider build and lint clean after the review fixes. `2026-10-29-theme-absence.sql` must be applied to any persistent environment. The patient record header also has a "Ver análises" button that opens the Análises page with the patient selected.
