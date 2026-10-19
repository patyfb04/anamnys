# Psychometric Scales (F-01)

**Date:** 2026-10-18
**Status:** Implemented
**Scope:** Backlog F-01. The provider records the score of a psychometric instrument for a
patient — from the patient record, or from a note so it is linked to that note's session — and
sees each instrument's dated series. The catalog follows the CFP rules (Res. CFP 31/2022) and
the instruments' licences: some instruments are available, some need the provider's one-time
opt-in, some are unavailable with an explanation. Only the number and date are stored; there is
no severity band, label or value colour.

Out of scope: administering instruments in the app (showing items, collecting answers),
multidimensional instruments (F-67), revoking an opt-in, a database trigger for the regimes,
the trajectory with overlaid events (F-13).

---

## 1. Context

- Tables exist with no code: `Instruments`, `ScaleApplications`, `ProviderInstrumentOptIns`.
  `Instruments` constraints already encode part of the rules: `Instruments_Enabled_ck` (only
  `nao_privativo`/`favoravel` may be enabled), `Instruments_Render_ck` (items only for
  `livre`/`dominio_publico`), `Instruments_Range_ck`, `Instruments_Explanation_ck` (explanation
  over 20 characters). `ScaleApplications` has `PatientId`, `SessionId`, `InstrumentId`,
  `Score`, `AppliedAt`, `AppliedBy`, `RawAnswers`, `CreatedAt`.
- No instrument is seeded. The deleted backend's `backend/db/02_seed_reference.sql` (commit
  before `aee5233`) holds a catalog of 15 instruments checked on 2026-08-23 against the SATEPSI
  lists, copyright and Brazilian validation, each with a provider-facing explanation. It also had
  a plpgsql trigger enforcing the regimes; the current schema has no trigger.
- CLAUDE.md lists stored procedures among patterns not used; cross-table rules live in the
  application (e.g. signed-note immutability, recorded as a gap).
- Notion "Plano de Implementação" (corrections section): catalog PHQ-9, MOM-D, MOM-A; three
  regimes; refusals always carry the explanation.

## 2. Decisions

| Topic | Decision |
|---|---|
| Where to record | Patient record (card "Escalas", with the series) and the note page (card "Escalas desta sessão", session prefilled). Same form component. |
| Enforcement | Application only, through one service that is the single write path. The missing trigger is recorded as a gap, alongside signed-note immutability. |
| Corrections | Soft removal: `RemovedAt`, `RemovedBy`. Removed entries leave the list and the series; the row stays. |
| Catalog | The 15 instruments of the old seed, verbatim (names, ranges, statuses, check date, licence, explanation, source). |
| What is stored | Score and date only. `RawAnswers` is never written (the product never renders items for any instrument, although PHQ-9's licence alone would allow `MayRenderItems = true`). No severity band, label or colour. |
| Help | An in-app "Como funcionam as escalas" panel in plain language, plus `documentation/21-psychometric-scales.md` with the full rules. |

## 3. Regimes

Computed from existing columns and the provider's opt-ins:

| Regime | Rule | Instruments |
|---|---|---|
| `available` | `Enabled = true` | PHQ-9, MOM-D, MOM-A |
| `needs_opt_in` / `opted_in` | `SatepsiStatus = 'sem_classificacao'` and `LicenseMode <> 'vedado_digital'`; `opted_in` when the provider has a non-revoked opt-in | GAD-7, PCL-5 |
| `unavailable` | everything else: art. 12 (`nao_avaliado`, `desfavoravel`), `vedado_digital`, or a disabled instrument blocked by copyright (`favoravel` + `proprietario`) | DASS-21, HAD, ROSENBERG, EPDS, MBI, BSQ, ORS, SRS, BDI-2, BAI |

`unavailable` instruments are listed with their `StatusExplanation`; no confirmation unlocks
them. Recording is allowed for `available` and `opted_in` only.

## 4. Data

Migration `design/migrations/2026-10-18-scales.sql` (idempotent, all-or-nothing):

- `ScaleApplications` gains `"RemovedAt" timestamptz`, `"RemovedBy" uuid`;
  `ScaleApplications_Removed_ck`: both null or both set.
- Seed the 15 instruments from the old `02_seed_reference.sql` (`ON CONFLICT ("Code") DO
  NOTHING`).
- `anamnys-db-script.sql` gets the columns, the constraint and the seed.

Entities `Instrument`, `ScaleApplication`, `ProviderInstrumentOptIn` and their `DbSet`s.

## 5. Server

### 5.1 `ScaleCatalog`

- `RegimeFor(Instrument, bool optedIn) : string` — the rule of §3.
- `ListAsync(db, providerId, ct)` → instruments ordered by regime (available, opted in, needs
  opt-in, unavailable) then code: `id, code, name, minScore, maxScore, regime, explanation`.
- `OptInAsync(db, providerId, instrumentId, now, ct)` → `OptedIn | AlreadyOptedIn | NotFound |
  NotAllowed`; stores `ExplanationShown` = the instrument's current `StatusExplanation`.

### 5.2 `ScaleRecording`

- `RecordAsync(db, providerId, patientId, request, now, ct)` with
  `RecordScaleRequest(Guid? InstrumentId, int? Score, DateTimeOffset? AppliedAt, Guid? SessionId)`:
  1. Validate: instrument id, score and date present; `AppliedAt <= now`.
  2. Patient of the provider (else NotFound); archived → `Archived`.
  3. Instrument exists (else NotFound); regime must be `available` or `opted_in`, else
     `Unavailable` (with explanation) or `OptInRequired`.
  4. `MinScore <= Score <= MaxScore`, else a field error.
  5. Session, when given, belongs to this patient and provider (else NotFound).
  6. Insert with `AppliedBy = providerId`, `RawAnswers = null`.
- `RemoveAsync(db, providerId, patientId, applicationId, now, ct)` → `Removed | NotFound`
  (already removed → NotFound).
- `SeriesAsync(db, providerId, patientId, sessionId?, ct)` → non-removed applications grouped by
  instrument (instrument code, name, min, max; entries `id, score, appliedAt, sessionId,
  appointmentStartsAt`), entries oldest first; with `sessionId`, only that session's entries.

### 5.3 Routes (`/api/phi`)

| Route | Result |
|---|---|
| `GET providers/me/instruments` | `200 InstrumentItem[]` |
| `POST providers/me/instruments/{id}/opt-in` | `204`; `404`; `409 { code: "opt_in_not_allowed", message }` |
| `GET providers/me/patients/{id}/scales?sessionId=` | `200 ScaleSeries[]`; `404` |
| `POST providers/me/patients/{id}/scales` | `201 { id }`; `400` field errors; `404`; `409 { code: "instrument_unavailable" \| "opt_in_required" \| "archived", message }` (`instrument_unavailable` carries the instrument's explanation as `message`) |
| `DELETE providers/me/patients/{id}/scales/{applicationId}` | `204`; `404` |

`NoteDetail` gains `sessionId`. `anamnys-aspire.Server.http` gains the requests.

## 6. Provider app

- **Patient record — card "Escalas":** per instrument used, a small line chart (score × date,
  neutral colour, y axis from `minScore` to `maxScore`, no bands or cut-offs) and the list of
  entries (date, score, linked appointment date, "Remover" with confirmation). Empty state with
  "Registrar escala". A "?" button opens the help panel.
- **Record modal (shared):** instrument picker grouped "Disponíveis", "Requer sua adesão",
  "Indisponíveis" (dimmed; selecting one shows its explanation and nothing else). Opt-in
  instruments show the explanation and "Li e quero registrar este instrumento" before the score
  field. Score field shows the range ("0–27"); date defaults to today. Server 409/400 messages
  are shown.
- **Note page — card "Escalas desta sessão":** only when the note has a session; lists that
  session's entries and opens the modal with the session prefilled.
- **Help panel "Como funcionam as escalas"** (plain pt-BR):
  - Disponíveis: escalas que o CFP classificou como não privativas; você registra o escore direto.
  - Requer sua adesão: o CFP ainda não se pronunciou; você registra depois de ler o aviso e
    confirmar uma única vez.
  - Indisponíveis: proibidas pelo CFP (art. 12) ou bloqueadas pelo autor; o Anamnys não aceita
    registros delas.
  - O Anamnys guarda só o número e a data. A interpretação é sua.
  - Errou o escore? Remova e lance de novo; o registro removido fica guardado.

## 7. Documentation

- `documentation/21-psychometric-scales.md`: regimes and their grounds (Res. CFP 31/2022 art. 4,
  6, 11, 12, 13; Res. CFP 09/2024; copyright), opt-in, what is stored and what never is, soft
  removal, how to add or reclassify an instrument (migration with a new explanation and check
  date).
- Gaps file: no database trigger for the regimes; opt-in cannot be revoked; multidimensional
  instruments (F-67); scales recorded from the record are not linked to a session.
- Status file: F-01 done.

## 8. Testing

- Service (real database): each regime — available records; needs opt-in refuses until opt-in;
  unavailable refuses even with an opt-in row inserted directly; opt-in twice is idempotent; opt-in
  on an unavailable instrument → NotAllowed; score at `MinScore` and `MaxScore` accepted, outside
  refused; future date refused; another patient's session → NotFound; archived patient; another
  provider's patient → NotFound; removal hides the entry from the series; series filtered by
  session; regime ordering in the catalog list.
- Migration: catalog seeded with 15 instruments; running twice keeps 15; removal constraint.
- Endpoint: 401 without session.
- Manual: record from the record and from a note, opt-in flow, unavailable explanation, help
  panel, removal, chart at 390 px.
