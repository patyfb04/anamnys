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
| F-10 Camada de agregação | done | One call returns a patient's theme history: signed notes only (date = actual session start, else appointment, else signing day), tags not removed and not untouched AI proposals, one lane per term with parents always included, retired/split terms keep their old sessions with `supersededBy` (D-01), origin (excerpt, section, quote) on every occurrence, co-occurrence counts per session, 6-signed-note minimum. Since 2026-10-31 (F-03 rule applied) a tag without an ExcerptRef is not counted. `GET providers/me/patients/{id}/analysis`. `Analysis/`, `documentation/31-case-analysis.md`. No migration. |
| F-11 Linha do tempo de temas | done | "Análises" page (`/analyses`, patient id only in the URL): patient picker, period 6 m / 1 y / all, card "Temas por sessão" (sessions as columns, lanes with collapsible children, sort by frequency/recency/alphabet, "substituído por X"), cell modal with the stored quote, link to the highlighted passage and "na mesma sessão", card "Aparecem juntos com mais frequência", and the "Linha do tempo" card reused with the page period. `components/analysis/`, `documentation/31-case-analysis.md`. Build and lint only; see the gaps file. |
| F-12 Ausência de temas | done | A term recorded in at least 3 signed sessions and in none of the last N (default 4, configurable 2 to 20 per provider) is described on the "Análises" page: card "Temas sem registro recente" above the grid (dates, real session count, days; click opens the last passage) and a neutral "sem registro nas últimas N" mark on the lane. Whole signed history regardless of the period, superseded/retired terms excluded, parent flagged only when no child was recorded either, 6-session minimum, no notifications. `absences` and `absenceSessions` on the analysis endpoint; `GET/PUT providers/me/settings/analysis`; settings section "Análises". Migration `2026-10-29-theme-absence.sql` (Providers.AbsenceSessions). `Analysis/ThemeAbsence.cs`, `documentation/32-theme-absence.md`. Build and lint only for the UI; see the gaps file. |
| F-13 Trajetória de escala | done | Card "Trajetória de escala" on the "Análises" page, below "Temas sem registro recente": one instrument's scores over the page period (picker, default the one with most points) as a neutral SVG, with events on the same axis: medication start, dose change and stop (toggle on), signed notes and missed/cancelled appointments (toggles off; notes need the analysis, 6 signed sessions). No bands, value colours, trend line or relating wording. Click a point or event for details and a link (note, patient record); text list of events for accessibility. Reuses the timeline and analysis payloads, no server change, no migration. Dev seed adds monthly PHQ-9 (18 to 7) and Sertralina 50mg, 100mg, stop. `components/analysis/trajectoryModel.ts` (pure; no frontend test runner), `documentation/33-scale-trajectory.md`. Build and lint only; see the gaps file. |
| F-14 Plano terapêutico e lacunas | done | Card "Plano terapêutico" on the "Análises" page, below "Trajetória de escala": each objective of the open plan can be linked to a theme (optional "Tema ligado" select in the objective form); the card shows when it was defined, when its theme (or a child) was last recorded in a signed session, and the interval in sessions and days ("sem registro há N sessões (D dias)"); an objective never recorded counts from its definition; an objective without a theme says "sem tema ligado". Same counting rules as F-10 (signed notes, tags not removed and not untouched AI proposals). "Marcar plano como revisado" sets `TreatmentPlans.ReviewedAt` (audited) and the line "Plano revisado em …, há N dias" has no deadline or colour. "Marcar para retomar" on the note view (review or signed notes) keeps a follow-up with an optional origin passage (ExcerptRef); only the provider resolves it, from the plan card or from a later note ("retomada nesta nota", origin or earlier notes refused), and can reopen it. Observation wording only, no notifications, archived patients read-only (409). Migration `2026-10-30-plan-adherence.sql` (`FollowUpItems.ProviderId`, `ExcerptRefId`, `ResolvedById`, audit actions) must be applied to any persistent environment. `Analysis/PlanAdherence*.cs`, `components/analysis/PlanCard.tsx`, `documentation/34-plan-adherence.md`. Dev seed adds three objectives and two follow-ups. Build and lint only for the UI; see the gaps file. |
| F-15 Linha do tempo de falas do paciente | done | The provider marks a passage of a note in review or signed as a quote ("Marcar como fala do paciente" on the note view, optional active theme; the stored text is the exact passage, never typed or inferred). Card "Falas do paciente que você registrou" on the "Análises" page, below "Temas por sessão": quotes of signed notes only, in chronological order by session date (F-10 rule), theme filter (a parent includes its children), page period, 1 to 3 columns, 12 per page, fixed sentence "Estas são as falas do paciente que você registrou nas notas."; a quote whose exact text is no longer in the signed note is not listed. The grid's cell detail lists that theme's quotes in that session. The note view lists its own quotes with "Remover" (soft removal, audited `quote_recorded` / `quote_removed`). Archived patients read-only (409). Included in the account export (`falas-registradas.csv`, `paciente.json`). Migration `2026-10-30-patient-quotes.sql` (`PatientQuotes.AddedById`, `RemovedAt`, `RemovedById`, audit actions) must be applied to any persistent environment. `Analysis/PatientQuotes*.cs`, `components/analysis/QuotesCard.tsx`, `components/notes/NoteQuotes.tsx`, `documentation/35-patient-quotes.md`. Dev seed adds four quotes per seeded patient. Build and lint only for the UI; see the gaps file. |
| F-16 Busca no prontuário | done | Card "Perguntar ao prontuário" on the "Análises" page: a natural-language question about the selected patient returns passages of signed notes with session date (F-10 rule), section, snippet with the matching words highlighted and a link to the note; nothing found says so and the closest passage is never returned. Retrieval only: no summary, no interpretation, evaluative words are ignored. Lexical search in PostgreSQL (Portuguese stemming, accent-insensitive, -ão/-ões handled) plus the theme vocabulary (labels, codes, children); text requires every word, theme passages only when the terms cover the question; at most 20 passages, "primeira vez" / "última vez" set the order. `POST providers/me/patients/{id}/record-search` (question in the body, never in a URL, log or audit). Migration `2026-10-31-record-search.sql` (`unaccent`, configuration `anamnys_pt`, GIN index on `NoteSections`) must be applied to any persistent environment. `Analysis/RecordSearch*.cs`, `documentation/36-record-search.md`. Build and lint only for the UI; see the gaps file. |

### E · Compartilhamento protegido

| ID | State | Notes |
|---|---|---|
| F-17 Autorização de divulgação | done | The provider records, for a signed issued document, the recipient (name, role), purpose, authorization date, how it was obtained (verbal in session, signed term, in writing), an optional attached term (PDF/JPG/PNG, ClamAV-scanned before storing, readable again, audited) and an explicit confirmation. One authorization per document, recipient and purpose. No link without it. Migration `2026-11-01-protected-sharing.sql` must be applied to any persistent environment (re-run where an earlier version was applied: it added `disclosure_term_opened`). `Sharing/`, `documentation/37-protected-sharing.md`. Build and lint only for the UI; see the gaps file. |
| F-18 Link protegido | done | Signed issued documents only. Opaque 256-bit token (stored as SHA-256), system password shown once (Argon2id 19 MiB, t=2, p=1), expiry 7 days by default and 30 at most, optional view cap, download off by default. Public page `/patient/s/{token}`, `noindex`, no login. Unknown, expired, revoked, capped and blocked links answer the same 404; five wrong passwords block the link; rate limits per client and global, at most 4 Argon2 derivations at once (429 `busy`). Watermark with recipient name and time on every page. A corrected document keeps the shared version and gets no new link. Access log with time, outcome and user agent (no geolocation, no IP). Revocation is immediate; discard removes everything. New settings: AppHost `patient-portal-url` (required outside Development), `ForwardedHeaders:KnownProxies`; proxy logs must scrub `/s/`. Build and lint only for the UI; see the gaps file. |

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
| F-29 Agenda própria | partial | delivery 1 done (create, reschedule, statuses, day/week view, time zone column); delivery 2 of 4 done (2026-11-09): weekly availability with versions, national holidays, blocks, calendar overlay, outside-availability warning, month view (`Availability/`, `documentation/41-availability.md`). Missing: recurrence (`AppointmentSeries`), Google mirror (`CalendarConnections`) |
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
| F-44 Dossiê do paciente | done | One ZIP per request (mandatory reason, optional period): `dossie.pdf`, signed note and document PDFs, clean external documents, disclosure terms, `manifesto.json` with the SHA-256 of every file, `LEIA-ME.txt` and the patient-scoped integrity proof with the covering F-45 tokens. Built in the background, the ZIP timestamped by its own SHA-256 (token downloadable, verifiable with `openssl ts -verify -data <zip> -in <tsr> -token_in`), downloadable for 7 days, delivery receipt kept after expiry, bell notice, "Dossiês" card on the patient page. Chapter `documentation/39-patient-dossier.md`. `2026-11-04-patient-dossier.sql` must be applied to any persistent environment. |
| F-45 Cadeia de integridade | done | One append-only hash chain of seals per provider over signed notes and signed issued documents (canonical content, not the PDF), sealed inside the signing transaction; the discard marks the seal, keeps it. A daily RFC 3161 timestamp of the chain head (FreeTSA in Development), nightly verification with an in-app notice, badge on signed notes and documents, "Integridade dos registros" card with a PDF verification report on Conformidade. Records signed before F-45 are sealed retroactively at server start. `2026-11-03-integrity-chain.sql` must be applied to any persistent environment. `Integrity/`, `documentation/38-integrity-chain.md` |
| F-46 Backup | done | Encrypted continuous backup to any S3 destination off the server: WAL-G (WAL every 60 s, daily full, 30 days of point-in-time, 12 monthly fulls) and an rclone crypt copy of the object store every 10 minutes with dated versions; `backup-agent` container, `BackupMonitor` e-mail alerts, `BackupRuns`/`BackupAlerts` (`2026-11-05-backup-runs.sql` must be applied to any persistent environment), `scripts/restore.sh` (point in time or latest, on a new server), `RestoreCheck` and `scripts/restore-drill.sh`. Off unless `Parameters:backup-enabled`; publish always backs up. Drill 2026-11-07 aprovado: RPO database 0 s, objects 6m57s, RTO 7m38s (goals 1 min, 15 min, 2 h), `design/drills/2026-11-07-restore-drill-4.md`; drill 2026-11-08 aprovado with WAL replay of a post-full change and an object overlay: RPO database 0 s, objects 2m35s, RTO 5m10s, `design/drills/2026-11-08-restore-drill.md`. The word "cofre" still needs production plus a drill aprovado in the last 90 days. `documentation/40-backup-restore.md` |
| F-47 Marcadores de fala | schema only | `SpeechMarkers` |
| F-48, F-49 | dropped (user, 2026-11-05): neither own-infrastructure deployment nor EHR integrations will be built | |
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
| 09 Linha do tempo de medicação | partial: medication and scales on one axis (F-09), themes per session and co-occurrence on the Análises page (F-10, F-11), themes without recent record (F-12), scale trajectory with events (F-13); plan adherence (F-14); all analysis layers done |
| 10 Plano terapêutico e lacunas | built (F-14): objectives linked to themes, plan review and follow-ups on the Análises page and the note view |
| 11 Busca no prontuário | built (F-16): lexical search plus theme vocabulary, passages with provenance on the Análises page |
| 12 Disponibilidade e agenda | partial (appointments only) |
| 13 Google | not started |
| 14 Lembrete | partial (e-mail, not WhatsApp) |
| 15, 16 | not started |
| 17 Sala própria | not started (D-06) |
| 18 Compartilhamento protegido | built (F-17, F-18): authorization record, tokenized link with separate password, watermarked PDF, access log, revocation |
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
   **Analyses (group D) closed:** ~~F-10 (aggregation layer)~~, ~~F-11 (theme timeline)~~, ~~F-12 (absence
   detection)~~, ~~F-13 (scale trajectory with events)~~ and ~~F-14 (plan adherence and follow-ups)~~ and ~~F-15 (patient quotes)~~ done.
2. ~~F-16 (natural-language search)~~ done; ~~F-17/F-18 (protected sharing)~~ done.
   **Provider portal first, patient portal after (user, 2026-11-03):** F-45 → F-44 (dossier), F-46 (backup);
   group H: F-29 rest (availability, recurrence, month view, Google), F-31 (structured intake), F-30 (WhatsApp);
   group G: F-23…F-28; group H2: F-38…F-43; group F: F-19…F-22.
   Groups G, H2 and F are built "with a caveat" (user, 2026-11-03) instead of waiting for their decisions: for the F-23
   consent term the user decided (2026-11-03) that a lawyer review does not gate it (D-03), as long as the term
   states explicitly and with valid data the names of every party (patient, provider, guardian for minors), the date it was given and the rest
   of what CFP 13/2022 asks (purpose, what is captured, audio destroyed after transcription, what is kept, who
   accesses, retention, revocability). Consent is given once (per patient and treatment, as F-24 says), not per
   session; D-06 is decided
   (user, 2026-11-03): build the own room (group H2), still optional for the provider; the TUSS codes are checked against the ANS
   table by us and the differences listed for the user to validate (D-04).
3. Patient portal (group K in the Notion plan, user 2026-11-07: F-61 to F-64 moved out of group H), after the whole provider portal: F-61 rest, F-62, F-63, F-64.
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
Verified 2026-10-29 (F-12): full suite 1573 total, 0 failed, 3 skipped; provider build and lint clean after the review fixes. `2026-10-29-theme-absence.sql` must be applied to any persistent environment. The patient record header also has a "Ver análises" button that opens the Análises page with the patient selected. Merged into develop on 2026-10-29 (commit 82730dd).
Verified 2026-10-29 (F-13): frontend only, no server or test change since the F-12 full suite (1573 total, 0 failed); provider build and lint clean after the review fixes. No migration. Dev seed `dev-seed/analysis-samples.sql` now also adds monthly PHQ-9 and a treatment with a dose change and a stop. The event list under the chart pages 10 at a time. Merged into develop on 2026-10-29 (commit cd984eb).
Verified 2026-10-30 (F-14): full suite 1591 total, 0 failed, 3 skipped, after the review fixes; provider build and lint clean. `2026-10-30-plan-adherence.sql` must be applied to any persistent environment. Merged into develop on 2026-10-30 (commit df7a377).
Verified 2026-10-31 (F-15): full suite 1623 total, 0 failed, 3 skipped; provider build and lint clean after the review fixes. `2026-10-30-patient-quotes.sql` must be applied to any persistent environment. From this delivery the analysis queries ignore theme tags with no reference (F-03 rule), and writes that create a reference take the patient row before the note lock so a discard cannot leave one behind. Merged into develop on 2026-10-31 (commit ed0560d).
2026-10-31 (Análises page polish, frontend only): every card collapses, boxed anchors at the top open and scroll to a card, each open card ends with "Voltar ao topo", the patient name is a bold heading smaller than the page title. Provider build and lint clean; no server or test change.
Verified 2026-10-31 (F-16): full suite 1691 total, 0 failed, 3 skipped; provider build and lint clean after the final review fixes. `2026-10-31-record-search.sql` must be applied to any persistent environment (it needs the `unaccent` extension). Search results page 5 at a time on the server; the anchors on the Análises page share one row on wide screens. Suite re-run after this: 1691 total, 0 failed, 3 skipped. Merged into develop on 2026-11-01 (commit 7552df3).
Verified 2026-11-02 (F-17 + F-18): full suite 1779 total, 0 failed, 3 skipped, after the security and final review fixes; provider and patient build and lint clean. `2026-11-01-protected-sharing.sql` must be applied to any persistent environment (re-run where an earlier version was applied). Publishing now needs the AppHost parameter `patient-portal-url`; see chapter 20 for `ForwardedHeaders:KnownProxies`, proxy log scrubbing and the SPA deep-link fallback. Merged into develop on 2026-11-02 (commit 96f9bda).
2026-11-02 (sharing follow-ups): the public page draws the PDF with pdf.js (`pdfjs-dist` 6.4.299, works on Android Chrome with download off); the "Compartilhamento" menu page lists every share link with filter, paging and revoke (`GET providers/me/shares`); each block of the Conformidade page pages 5 rows at a time. Full suite 1784 total, 0 failed, 3 skipped; provider and patient build and lint clean. Merged into develop on 2026-11-02 (commit 1b4360e).
2026-11-03 (sharing patient filter): the "Compartilhamento" page has a patient search field (type-ahead, Enter or click applies, the name stays out of the URL) next to Ativos/Todos; `GET providers/me/shares` takes an optional `patientId`. Sharing tests green; provider build, type-check and lint clean.
Verified 2026-11-03 (F-45): server tests (`*Integrity*`, `*MigrationTests*`, `*Discard*`, notifications and retention) ran green; provider and patient build and lint clean. Full suite 1853 total, 0 failed, 3 skipped (2026-11-04; a first run had one flaky failure that did not repeat). `2026-11-03-integrity-chain.sql` is applied to the dev database and must be applied to any persistent environment (it drops `DocumentHashes`); the retroactive backfill runs at the next server start; publishing needs the AppHost parameters `integrity-tsa-url` and `integrity-tsa-root-certificate` (chapter 20).
Verified 2026-11-04 (F-44): `*Dossier*` plus `*MigrationTests*` 212 total, 0 failed (server round 1); after the review fixes `*Dossier*`, `*Discard*` and `*Notification*` 169 total, 5 failed only in `ProviderNotificationsTests` because the dev database lacked the migration (they should pass once `2026-11-04-patient-dossier.sql` is applied there); builder tests 8/8; provider and patient build and lint clean. Full suite 1918 total, 0 failed, 3 skipped (2026-11-05, after the final review fixes and with the migration applied to the dev database; the 5 notification failures are gone). `2026-11-04-patient-dossier.sql` is applied to the dev database and must be applied to any persistent environment.
Verified 2026-11-07 (F-46): `*Backup*`, `*RestoreCheck*` and `*MigrationTests*` ran green in their rounds; the restore drill passed (record 4, aprovado; records 1-3 are earlier failed attempts kept as history). Full suite 1971 total, 0 failed, 3 skipped (2026-11-08, after the final-review fixes). `2026-11-05-backup-runs.sql` must be applied to any persistent environment; publishing needs the `backup-*` AppHost parameters (chapter 20). Final-review fix wave 2026-11-08: restore.sh requires an explicit, empty destination bucket; RestoreCheck baseline mode fails on an unreadable source or baseline and counts a chain as pre-existing only for the same problem live; per-realm Keycloak check; test fixtures pin `backup-enabled=false`; monitor start-up grace (30 min); the drill records whether WAL replay after the full and the object overlay were exercised (drill 4 exercised neither). Drill 5 on 2026-11-08 aprovado with both exercised: a change after the full replayed, one version folder overlaid; RPO database 0 s, objects 2m35s, RTO 5m10s (`design/drills/2026-11-08-restore-drill.md`).
Verified 2026-11-09 (F-29 delivery 2): `*Availability*`, `BlocksTests` and `NationalHolidaysTests` 65/65 and all `*MigrationTests` 164/164 after the server fix round; provider and patient build and lint clean; not checked in a browser. Full suite 2036 total, 0 failed, 3 skipped (2026-11-09, after the final-review fixes; `2026-11-09-availability.sql` applied to the dev database). Also: the outside-availability notice uses the menu hover colours (secondaryContainer); the Configurações page is split into tabs Geral (account export), Agenda (hours, holidays, booking policy) and Prontuário (document check, retention, analyses), tab in the URL (`?tab=`), Geral first. `2026-11-09-availability.sql` must be applied to any persistent environment (until then appointment create and reschedule fail); the dev-database appointment and notification tests need it too.
