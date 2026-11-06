# Patient Dossier (F-44)

**Date:** 2026-11-04
**Status:** Approved for implementation
**Scope:** Backlog F-44. In one action, build a complete, chronological and auditable dossier of one patient: every
signed note, every signed issued document, every external document received, every disclosure (recipient, date,
purpose, authorization, access log), compliance status per document, retention status and the cryptographic integrity
proof (F-45).

Done when (backlog): a CRP request or a court order can be answered with a single export.

Explicitly out of scope: structured clinical data as CSV (scales, diagnoses, medication, objectives); sending the
dossier by e-mail; a request made by the patient from the patient portal.

Naming: the product calls it "Dossiê do paciente". The word "cofre" is not used anywhere (F-46 forbids it until backup
and restore exist and a restore has been rehearsed).

## 1. Decisions (agreed with the user, 2026-11-04)

| Topic | Decision |
|---|---|
| Format | A ZIP with `dossie.pdf` in front (the document to file), the original files (signed note and document PDFs, external documents exactly as received), a `manifesto.json` with the SHA-256 of every file, and the integrity proof. |
| Content | The official record only: signed notes, signed issued documents (superseded versions included and marked), external documents, disclosures with authorizations and accesses, compliance status, retention, integrity. No drafts, no raw transcripts, no audit values, nothing about other patients. |
| Period | Optional `from`/`to`; the default is the whole history. The cover states the period requested. |
| Request | Button "Gerar dossiê" on the patient page; a mandatory reason (CRP request, court order, the patient's own request, other with a short text). Generated in the background, bell notice when ready. |
| Lifetime | The ZIP is downloadable for 7 days, then deleted. The `Dossiers` row (date, period, reason, ZIP hash, manifest, timestamp token) is kept, so what was delivered can be proven later. The request and every download are audited. |
| Timestamp | The ZIP's SHA-256 is timestamped (RFC 3161, the F-45 authority) at generation. A TSA outage does not fail the dossier; it goes out without a timestamp and the cover says so. |
| Integrity problems | The dossier is generated anyway; the problem is shown on the cover and in the integrity section. |
| Archived patients | A dossier can be generated (requests arrive after care has ended). Discard keeps deleting `Dossiers` (unchanged). |

## 2. Data

Migration `design/migrations/2026-11-04-patient-dossier.sql` (idempotent, mirrored in `anamnys-db-script.sql`, added
to the migration-test chains). The existing `Dossiers` table (`Id`, `PatientId`, `GeneratedAt`, `ObjectKey`,
`Manifest`, `IntegrityVerified`, schema only, no code) becomes:

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | existing |
| `ProviderId` | uuid NOT NULL, FK `Providers` | new |
| `PatientId` | uuid NOT NULL, FK `Patients` | existing; add the FK if missing |
| `Number` | integer NOT NULL | per provider, 1, 2, 3... (used in the file name); unique (`ProviderId`, `Number`) |
| `Status` | text NOT NULL default `queued` | `queued`, `running`, `done`, `failed`, `expired` |
| `Reason` | text NOT NULL | `crp`, `court`, `patient_request`, `other` |
| `ReasonNote` | text | 1..200 chars, required if and only if `Reason = 'other'` |
| `PeriodFrom`, `PeriodTo` | date | optional; `PeriodFrom <= PeriodTo` when both are set |
| `RequestedAt` | timestamptz NOT NULL | new |
| `GeneratedAt` | timestamptz | existing; set when `done` (renamed meaning: completion time) |
| `ExpiresAt` | timestamptz | `GeneratedAt + 7 days` |
| `ExpiredAt` | timestamptz | set by the sweep |
| `ObjectKey` | text | existing; `dossiers/{providerId}/{dossierId}.zip`, set only when `done` |
| `SizeBytes` | bigint | |
| `ZipSha256` | text | lowercase hex |
| `Manifest` | jsonb | existing; the content of `manifesto.json` |
| `AnchorToken` | bytea | the RFC 3161 token for `ZipSha256`, null when the TSA failed |
| `AnchoredAt` | timestamptz | the token's genTime |
| `IntegrityVerified` | boolean | existing; true when every sealed item verified `intact` at generation |
| `FailureReason` | text | `dossier_error` or `interrupted` |

Constraints: the status values; `GeneratedAt` set if and only if the status is `done` or `expired`; `ObjectKey` only
when `done`; a partial unique index allowing one `queued|running` dossier per patient. Audit actions
`dossier_requested`, `dossier_downloaded`, `dossier_receipt_downloaded` are added to `AuditEntries_subject_ck`; the
notification kind `dossier_ready` (channel `in_app`, subject the dossier id) is added.

## 3. Flow (on the account export pattern, `Exports/`)

1. `POST /api/phi/patients/{patientId}/dossiers` with `{ reason, reasonNote?, periodFrom?, periodTo? }` validates
   (400 `invalid_reason`, `reason_note_required`, `invalid_period`), creates the row (`queued`, next `Number`), writes
   `dossier_requested` (values: reason and period only, never the note text) and enqueues. 202 `{ id, status }`.
   409 `dossier_in_progress` when the patient has one `queued|running` (the unique index makes it race-safe).
2. `DossierWorker` (hosted service, in-memory queue like `ExportQueue`, one job at a time): row to `running`;
   `DossierBuilder` writes the ZIP to a temporary file (deleted on close); SHA-256 of the file; `ITimestampAuthority`
   stamps that hash (failure: no token, logged without data); upload to the private bucket; one transaction sets
   `done`, `GeneratedAt`, `ExpiresAt`, `ObjectKey`, `SizeBytes`, `ZipSha256`, `Manifest`, `AnchorToken`,
   `AnchoredAt`, `IntegrityVerified` and inserts the bell notice.
3. Failure: `failed` with `FailureReason`, the uploaded object deleted when it exists. At startup, rows left `running`
   become `failed` / `interrupted` and `queued` rows are re-enqueued (same as the account export).
4. `GET /api/phi/patients/{patientId}/dossiers?page=n` lists the patient's dossiers newest first, 5 per page:
   `{ items: [{ id, number, status, reason, periodFrom, periodTo, requestedAt, generatedAt, expiresAt, sizeBytes,
   anchored, integrityVerified }], total, page, pageSize }`. `reasonNote` is not listed.
5. `POST /api/phi/dossiers/{id}/download` returns `{ url, expiresAt }` (presigned, one hour, new on each click) and
   writes `dossier_downloaded`. 409 `not_ready` or `expired`.
6. `GET /api/phi/dossiers/{id}/receipt.pdf` returns the delivery receipt (section 5) for `done` and `expired`
   dossiers, writes `dossier_receipt_downloaded`. 409 `not_ready` otherwise.
7. Sweep (daily, in the same worker or the export sweep pattern): `done` rows past `ExpiresAt` get the object deleted,
   `ObjectKey` null, status `expired`, `ExpiredAt` set.

All routes are in the PHI group, provider-scoped, 401 first, 404 for another provider's patient or dossier, `no-store`.
Download names carry no patient name: `anamnys-dossie-AAAA-MM-DD-NNN.zip`, receipt
`anamnys-dossie-NNN-comprovante.pdf` (NNN = `Number`, 3 digits).

## 4. ZIP layout

```
dossie.pdf
manifesto.json
LEIA-ME.txt
notas/AAAA-MM-DD-nota[-N].pdf
documentos/AAAA-MM-DD-<tipo>[-N][-substituido].pdf
externos/AAAA-MM-DD-<nome-original-saneado>[-N].<ext>
divulgacoes/AAAA-MM-DD-termo[-N].<ext>     authorization terms attached in F-17, byte for byte
integridade/comprovante.pdf
integridade/carimbos/<sequencia>.tsr
```

- Notes: signed notes whose `SignedAt` (in the provider's time zone, America/Sao_Paulo) falls in the period; a stored
  PDF is copied, a missing one rendered with `NotePdf` and not stored (same rule as the account export).
- Documents: signed issued documents in the period, every version; superseded ones get `-substituido`.
- External documents: received in the period, scan `clean`, copied byte for byte from storage; the stored name is
  sanitized (ASCII letters, digits, `-` and `_`, max 60 chars, extension kept). Blocked or not yet scanned files are not
  included; they appear in the summary as "arquivo não incluído: bloqueado" / "pendente de verificação".
- Integrity: `comprovante.pdf` is the F-45 report restricted to this patient's seals (each seal: subject, sequence,
  status, anchored); the `.tsr` files are the tokens of the anchors that cover those seals (raw DER).
- `manifesto.json`: `{ "v": 1, "dossierId", "number", "provider": { name, crp }, "patientId", "generatedAt",
  "period": { from, to }, "reason", "files": [ { "path", "sha256", "bytes" } ] }` — every file except
  `manifesto.json` itself. No patient name in the manifest (the name is in `dossie.pdf`).
- `LEIA-ME.txt`: what each file is; how to check files (`sha256sum -c` style instructions) and tokens
  (`openssl ts -verify -digest <sha256> -in <file>.tsr -CAfile <root>`); what is not included and why.

## 5. `dossie.pdf` and the delivery receipt (QuestPDF)

`dossie.pdf`:

1. Cover: provider name and CRP; patient name and date of birth; generation date; period requested ("histórico
   completo" when none); reason (and its text for `other`); integrity summary (all intact, or the problems);
   the statement "Dossiê gerado pelo Anamnys a partir do prontuário assinado. Os arquivos anexos estão listados com
   seus hashes SHA-256 em manifesto.json." The ZIP hash and its timestamp cannot be inside the ZIP; the cover says
   they are in the delivery receipt.
2. Chronological summary: date, kind (nota, documento, documento externo, divulgação), title, file in the ZIP (or the
   reason it is not included).
3. Disclosures: per authorization — recipient, role, purpose, authorization date, how obtained, the attached term
   file in `divulgacoes/` when there is one; per link — created, expires, revoked, views and every access (date, outcome, user agent).
4. Compliance: per issued document, the F-05 validator status and the pending items.
5. Retention: the record's retention date (F-06), active or archived, whether a disposal term exists.
6. Integrity: per note and document, the verification result at generation, seal sequence, sealed date, retroactive,
   anchored; the anchors used (date, authority); how to verify.

Delivery receipt (`receipt.pdf`, available while the row exists): dossier number, provider, patient name, requested
and generated dates, reason and period, `ZipSha256`, timestamp (date and authority, or "sem carimbo"), the manifest's
file list with hashes, current status (`done` until DD/MM or `expired` on DD/MM).

## 6. Provider UI

- Patient page: new collapsible `DetailCard` "Dossiês" (`collapsibleId`, anchor like the other cards):
  - "Gerar dossiê" opens a dialog: period from/to (optional, default whole history), reason (radio: "Pedido do CRP",
    "Intimação ou ordem judicial", "Solicitação do próprio paciente", "Outro" with a text field, max 200), the warning
    "O arquivo contém dados de saúde do paciente. Entregue apenas a quem tem direito.", button "Gerar".
  - The list (5 per page, newest first): date, period, reason, status ("Na fila", "Gerando", "Pronto até DD/MM",
    "Falhou", "Expirado"), "Baixar" while ready, "Comprovante de entrega" for ready and expired, "Tentar de novo" on
    failed (a new request with the same reason and period). Polls while queued or running.
  - 409 `dossier_in_progress` shows "Já existe um dossiê sendo gerado para este paciente."
- Bell: `dossier_ready` links to the patient page, card "Dossiês".
- Copy in `pt.json` under `dossier.*`.

## 7. Testing

- Content: only signed notes and documents in the period (superseded marked), clean external documents only,
  disclosures with accesses and term files, compliance, retention, integrity; drafts, raw transcript and another
  patient's data absent (a marker string in each, every non-PDF byte of the ZIP scanned; for PDFs the model input is
  checked, like the account export test).
- Period: items outside the period left out; no period means everything.
- Manifest: every listed SHA-256 matches the file; every file except the manifest is listed; `ZipSha256` matches the
  stored object.
- Timestamp: with the fake TSA the token is stored and verifies against `ZipSha256`; with the TSA down the dossier is
  `done` without a token and the cover says so.
- Integrity: a tampered note gives `IntegrityVerified = false` and the problem on the cover.
- Flow: 409 while in progress; the sweep deletes the object and marks `expired`, the receipt still works; downloads
  and receipts audited; 404 for another provider's patient and dossier; 401; failure marks `failed` and deletes the
  object; restart handling.
- Validation: reason values, note required only for `other`, period order.
- Front: provider build and lint.

## 8. Documentation and deployment

- Chapter `documentation/39-patient-dossier.md` and the index.
- Migration `2026-11-04-patient-dossier.sql` applied to any persistent environment.
- Status, gaps (structured CSV, e-mail, patient-portal request), Notion (F-44).
