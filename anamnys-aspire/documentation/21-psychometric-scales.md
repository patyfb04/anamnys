# Chapter 21: Psychometric Scales (F-01)

How the product records scores of psychometric scales, which instruments it accepts and why.
Last checked against the code on 2026-10-18 (branch `feature/psychometric-scales`). Design:
`design/specs/2026-10-18-psychometric-scales-design.md`.

This chapter is the long version of the rules. The code is short; the reasoning behind the
catalog is not, and it is what a future change has to respect.

---

## 1. What is stored, and what never is

| Stored | Never stored |
|---|---|
| Instrument, **score** (one integer), date of application, provider, patient, optional session link | Item answers (`ScaleApplications."RawAnswers"` is always `null`), severity bands, labels such as "moderate", colours, cut-offs, normative comparisons |

Why. Recording "PHQ-9 = 14 on 2026-03-12" is documentary record-keeping; Res. CFP 31/2022
art. 6 sole paragraph makes the record **mandatory**, so the product helps the psychologist
comply. Displaying items, collecting answers, scoring or interpreting is **application** of the
instrument, which brings in art. 11 (follow the approved manual), chapter V (equivalence study)
and, for remote use, Res. CFP 09/2024 art. 2 sole paragraph (the manual must foresee online
application). The product does none of that, and it does not call a number "moderate":
interpretation is clinical and belongs to the professional. Never add a severity column.

Only PHQ-9's licence would allow showing items (`MayRenderItems = true`), but the product never
renders items for any instrument, and `ScaleApplications."RawAnswers"` is never written.
`Instruments_Render_ck` refuses `MayRenderItems = true` for proprietary licences. The chart in the patient record
plots the score between the instrument's `MinScore` and `MaxScore` with no bands.

`Score` is one integer per application. Multidimensional instruments (several scores per
application, such as COPSOQ with 11 or SF-36 with 8) do not fit; that is backlog F-67 and the
model must change before one is added.

---

## 2. The catalog and the three regimes

The catalog has 15 rows in `Instruments`, seeded by `design/migrations/2026-10-18-scales.sql`
(copied from the original reference seed, with the explanation text the provider sees). The
regime is **computed**, not stored: `ScaleCatalog.RegimeFor(instrument, optedIn)`.

| Regime | Rule | Instruments | Recording |
|---|---|---|---|
| `available` | `Enabled = true` | PHQ-9, MOM-D, MOM-A | allowed |
| `needs_opt_in` / `opted_in` | not enabled, `SatepsiStatus = 'sem_classificacao'` and `LicenseMode <> 'vedado_digital'`; `opted_in` when the provider has a non-revoked opt-in | GAD-7, PCL-5 | allowed only when `opted_in` |
| `unavailable` | everything else | DASS-21, HAD, ROSENBERG, EPDS, MBI, BSQ, BDI-2, BAI, ORS, SRS | never; no confirmation unlocks it |

`ScaleCatalog.ListAsync` orders by regime (available, opted in, needs opt-in, unavailable), then
code. `unavailable` rows are kept in the database on purpose, with the reason written down, so
the app can **explain** why something is not offered. Silent absence looks like an oversight and
invites workarounds.

### 2.1 Legal grounds

The list was assembled by checking each instrument against three independent things. The earlier
list (PHQ-9, GAD-7, BDI-II) was just the standard English-language EHR trio, which is not a
criterion.

1. **SATEPSI lists (Res. CFP 31/2022).** Art. 12: using a test with an unfavourable opinion **or**
   on the list of *Testes Não Avaliados* is an **ethical offence**, and the sanction falls on the
   psychologist, not on the software. A catalog that offers such instruments induces the
   offence. Art. 4: instruments outside certification (the *Instrumentos Não Privativos* list)
   may be used only as a complementary source, never as the sole basis of a conclusion.
   "Non-private" is **not** an approval: it means the CFP decided the instrument is not a
   psychological test; psychometric quality is not assessed and checking scientific support is the
   professional's duty. Art. 5: the psychologist may choose instruments grounded in the
   literature. Art. 13: the way to settle an unclassified instrument is to ask the regional
   council (CRP) to submit it to the CCAP; the answer can be "non-private" (then it can be
   enabled) or "psychological test" (then it goes to the *Não Avaliados* list and becomes an
   ethical offence). The product must not anticipate that answer.
   Lists: `satepsi.cfp.org.br/testesNaoPrivativos.cfm`, `.../testesNaoAvaliados.cfm`.
2. **Copyright**, a separate question. Storing a score does not reproduce the work; showing the
   items does. A favourable CFP opinion does not help if the rights holder forbids the use.
3. **A Brazilian-Portuguese validation** exists.

The reason shown to the provider falls into three distinct causes that must not be conflated in
the UI because they lead to different paths:

| Cause | Meaning | Path |
|---|---|---|
| (a) Ethical offence | On the *Não Avaliados* list (art. 12): DASS-21, HAD, ROSENBERG, EPDS, MBI, BSQ | None; no registration is possible |
| (b) No classification | The CFP has not spoken: GAD-7, PCL-5 (and ORS, SRS). **Not the same as forbidden**: art. 12 types the offence by list, and there is no list entry | Outside the default catalog, but the score can be recorded after the provider's opt-in. Settling it for good takes art. 13 |
| (c) Copyright | The rights holder forbids it. BDI-2 and BAI: favourable CFP opinion, but Hogrefe holds the copyright and sells only to psychologists with a CRP number. ORS and SRS: the author's free licence covers paper and pen only and forbids digital use | Opt-in does not help, since the obstacle is not the CFP's |

ORS and SRS have both (b) and (c), so the CFP's silence is not what blocks them; the licence
does, and it would still block them if the CFP ruled. Either cause is enough by itself. `LicenseMode = 'vedado_digital'`
is what excludes them from the opt-in path.

Per-instrument notes worth keeping:

- **PHQ-9**: non-private list, analysis of 2020-05-07, public-domain footer ("No permission
  required to reproduce..."); Brazilian validation Santos IS et al., *Cad. Saúde Pública*
  2013;29(8):1533-43. A screening tool adapted to serial use.
- **MOM-D / MOM-A** (the two inventories of *A Mente Vencendo o Humor*, Greenberger & Padesky):
  on the non-private list and designed for serial application in CBT, which is the product's
  use. Guilford and the official site forbid reproduction on a site or platform and any commercial
  use, hence score only. The authors state that **no norms or cut-offs exist**; here that is an
  advantage, because the value is the patient's own curve, never a comparison with a population.
  The Brazilian translation (Artmed, 2nd ed., 2017) is editorial; no published Brazilian
  psychometric study.
- **GAD-7**: not on any list; Brazilian validation published (Moreno et al., 2016). It was once
  enabled on the presumption that the CCAP would classify it like the PHQ-9; a presumption is
  exactly what must not be made here.
- **PCL-5**: public domain (National Center for PTSD), Brazilian cross-cultural adaptation
  (Osório et al., 2017); public domain does not replace a CFP classification.

### 2.2 Evaluated and left out

Recorded so nobody repeats the research.

- **NCCN Distress Thermometer**: validated in Brazil but in an oncology population (using it
  elsewhere extrapolates the validation), and the NCCN forbids reproduction without written
  permission. Fits a psycho-oncology module, not general follow-up. A plain "distress in the last
  week, 0 to 10" field would meet the need without invoking the brand, but it would not be their
  instrument and must not be called that.
- **COPSOQ and SF-36**: not psychotherapy follow-up measures (COPSOQ is an organisational risk
  diagnosis whose unit is the sector; SF-36 is general health-related quality of life,
  insensitive to short-term change), and neither has a single score (section 1).
- **Future candidates** (each would need to clear both barriers, and none does today): WHO-5 (CC
  BY-NC-SA 3.0 IGO: the NC clause blocks a paid product), AUDIT / ASSIST / SRQ-20 (WHO: "not for
  sale or for use in conjunction with commercial purposes"), WHOQOL-BREF (WHO authorisation),
  PSS-10 (free but mandatory request through ePROVIDE/Mapi).

---

## 3. Opt-in

For `needs_opt_in` instruments the provider confirms once ("Li e quero registrar este
instrumento"). `POST providers/me/instruments/{id}/opt-in`:

- Stores a `ProviderInstrumentOptIns` row with `AcknowledgedAt` and `ExplanationShown`, the
  **text that was read**, as it was at that moment (later edits of the catalog text do not change
  what the provider acknowledged).
- Once per instrument and provider: a second call returns `204` without a new row (a unique
  violation from a concurrent call is treated the same way).
- `unavailable` instruments answer `409 opt_in_not_allowed`.
- It is **not revocable yet**: the table has `RevokedAt` and the code ignores revoked rows when
  deciding the regime, but nothing writes it, and the unique key does not consider `RevokedAt`
  (both recorded as gaps).

---

## 4. Recording, series and removal

All writes go through `Scales/ScaleRecording`, the only write path.

- `POST providers/me/patients/{id}/scales` validates in this order: required fields and
  `AppliedAt <= now + 5 minutes` (a client clock slightly ahead is tolerated; the stored date is
  `min(AppliedAt, now)`); the patient belongs to the provider (`404`); archived patient (`409
  archived`); the instrument exists; the regime is `available` or `opted_in` (`409
  instrument_unavailable`, with the explanation as the message, or `409 opt_in_required`);
  `MinScore <= Score <= MaxScore`; the optional session belongs to this patient and provider.
  Then it inserts with `RawAnswers = null`.
- `GET providers/me/patients/{id}/scales?sessionId=` returns the non-removed applications
  grouped by instrument, oldest first; with `sessionId`, only that session's entries.
- **Soft removal**: `DELETE .../scales/{applicationId}` sets `RemovedAt` and `RemovedBy`
  (`ScaleApplications_Removed_ck`: both null or both set). The row stays, leaves the list and the
  chart, and removing twice answers `404`. A wrong score is corrected by removing and recording
  again.
- Dates: the client sends "now" for today's date and noon in practice time for past days, so a
  date never lands on the wrong calendar day. A client clock a few minutes ahead of the server is
  absorbed by the 5-minute tolerance above.

**Enforcement is in the application only. There is no database trigger.** Reasons: the regime
is derived from three columns plus opt-ins and is expected to change as the CFP lists change; a
trigger would duplicate that rule in SQL where it is harder to change and test, and the same gap
already exists for signed-note immutability, so both are handled together later (gaps file, "Notas
clínicas"). The cost is that anything writing to the table outside `ScaleRecording` bypasses the
regimes; do not add another write path.

The `InstrumentSeries` SQL view still selects `RawAnswers` and ignores `RemovedAt`. Nothing reads
it; drop or fix it before anything does.

---

## 5. UI

- **Patient record, card "Escalas"**: one small line chart per instrument used (neutral colour, y
  axis from `minScore` to `maxScore`, no bands), then the list (date, score, linked appointment,
  "Remover" with confirmation), a "?" button for the help panel and "Registrar escala". Read-only
  when the patient is archived.
- **Record modal**: picker grouped "Disponíveis", "Requer sua adesão", "Indisponíveis" (dimmed;
  selecting shows the explanation and nothing else). Opt-in instruments show the explanation
  before the score field. The score field shows the range.
- **Note page, card "Escalas desta sessão"**: only when the note has a session; same modal with
  the session prefilled; recording hidden for archived patients. Scales recorded from the patient
  record are not linked to a session.

The help panel "Como funcionam as escalas" (`packages/shared/src/lib/i18n/locales/pt.json`,
`scales.helpItems`) reads, verbatim:

- Disponíveis: escalas que o CFP classificou como não privativas; você registra o escore direto.
- Requer sua adesão: o CFP ainda não se pronunciou; você registra depois de ler o aviso e confirmar uma única vez.
- Indisponíveis: proibidas pelo CFP (art. 12) ou bloqueadas pelo autor; o Anamnys não aceita registros delas.
- O Anamnys guarda só o número e a data. A interpretação é sua.
- Errou o escore? Remova e lance de novo; o registro removido fica guardado.

---

## 6. Adding or reclassifying an instrument

Always a **new migration** in `design/migrations/` (mirrored into `anamnys-db-script.sql`), never
an edit of an old one and never a manual `UPDATE`.

1. Check the instrument against the SATEPSI lists, the licence and the Brazilian validation (section
   2.1), and write down the check date (`SatepsiCheckedOn`) and the source (`SourceRef`).
2. Write the `StatusExplanation` for the professional, not for programmers: what the CFP said,
   where, and what it means for her. Say why something is available as well as why another is not.
3. Set `SatepsiStatus`, `LicenseMode`, `MayRenderItems` (keep `false` unless the licence allows
   rendering; `Instruments_Render_ck` enforces it) and `Enabled`.
4. **Never set `Enabled = true` without a SATEPSI listing** (`nao_privativo`, or another explicit
   position from the CFP). Silence from the SATEPSI does not qualify; that is the `needs_opt_in`
   path.
5. Reclassification (for example after an art. 13 answer): a migration that updates the row and
   its explanation and check date. Remember that `nao_avaliado` makes the instrument
   `unavailable`, but existing applications stay, so decide explicitly what happens to them.
6. Add or adjust the tests in `anamnys-aspire.Tests/Scales/`.
7. Multi-score instruments need a model change first (section 1).
