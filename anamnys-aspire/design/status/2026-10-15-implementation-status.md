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
| F-02 Vocabulário de temas | schema only | `ThemeVocabularies`, `ThemeTerms`, `ThemeTags`; the structuring prompt does not use them |
| F-03 Referências de trecho | schema only | `ExcerptRefs` |
| F-04 Metadados da sessão | partial | `Sessions` (scheduled/actual times, modality, attendance) and appointment status/cancellation are stored; a `Sessions` row is created by a live recording or a typed note linked to an appointment, and no attendance statistics exist |

### B · Conformidade documental

| ID | State | Notes |
|---|---|---|
| F-05 Validador CFP 006/2019 | partial | delivery 1 done: declaração, atestado and relatório (form per kind, conformity check, confirmations, blocking/advisory modes, signing, correction by new version, PDF export); all six rulesets seeded. Missing: laudo, parecer, relatório multiprofissional (delivery 2, REQUIRED). `Documents/`, `documentation/22-psychological-documents.md` |
| F-06 Guarda e descarte | schema only | `RetentionRules`, `DisposalRecords` |

### C · Documentos externos

| ID | State | Notes |
|---|---|---|
| F-07 Upload | schema only | `ExternalDocuments`; no object storage wired |
| F-08 OCR + fatos declarados | schema only | `ExtractedFacts` |
| F-09 Linha do tempo de medicação | partial | medications are entered by hand on the record (`MedicationEntries`); nothing comes from prescriptions |

### D · Camada longitudinal

| ID | State | Notes |
|---|---|---|
| F-10 … F-13, F-15, F-16 | not started | depend on F-01…F-03 |
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
| F-60 Exportação integral | schema only | `AccountExports` |

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
| 05 … 11 | not started |
| 12 Disponibilidade e agenda | partial (appointments only) |
| 13 Google | not started |
| 14 Lembrete | partial (e-mail, not WhatsApp) |
| 15, 16 | not started |
| 17 Sala própria | not started (D-06) |
| 18 … 23 | not started |

## Built but not in the backlog

- Admin app registration and staff approval (owners realm), break-glass grants schema.
- Account self-service: profile, e-mail change, password and 2FA through Keycloak actions.
- Keycloakify login theme.
- Dev clinical seed.
- Note list (`/note-editor`) with status filters and patient-name search (POST body, never in a URL).

## Next, in priority order (agreed 2026-10-18)

1. **F-05 delivery 2 — laudo, parecer and relatório multiprofissional (REQUIRED)** — F-05 is not
   done until every kind of Res. CFP 006/2019 art. 8 can be written and checked. Laudo and parecer
   are the documents of psychological assessment and require references (arts. 13 §7, 14 §6); the
   relatório multiprofissional needs a co-authorship model (art. 12). Their rulesets are already
   seeded by delivery 1.
2. **F-02 Vocabulário de temas** — blocked by decision D-01 (how series behave when a term is
   renamed, split or merged). The old seed has a reviewed mental-health vocabulary to copy.
3. **F-60 Exportação integral** — promised on the site and in the Terms; must exist before the
   first paying subscriber.

Open verification: the note search `pageSize` tests (`develop` `e1a5553`) compiled but have not
run; run `-class "*NoteListTests*"` with Aspire stopped.
