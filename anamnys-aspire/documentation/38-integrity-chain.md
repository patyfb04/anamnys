# Chapter 38: Document Integrity Chain (F-45)

How the system proves that no signed note and no signed issued document was altered after signing: an append-only hash
chain per provider, anchored once a day outside the database with RFC 3161 timestamps. Last checked against the code on
2026-11-03 (branch `feature/integrity-chain`). Design: `design/specs/2026-11-03-integrity-chain-design.md`.

What a future change has to respect: a signed record never exists without a seal, because sealing runs inside the
signing transaction (section 4); the canonical bytes of format `v: 1` never change, or every old hash stops
recomputing (section 3); the seal tables are append-only at the database level (section 2); a timestamp authority
outage never blocks signing (section 8); and nothing clinical leaves the system, only a hash (section 8).

---

## 1. Purpose and scope

Done when (backlog): any modification after signing is detectable and demonstrable.

Out of scope: external documents received (F-07) and disclosure authorizations (F-17) are not sealed (recorded gap);
verification by the patient; the patient dossier (F-44), which will reuse the verification report; and embedding the
timestamp tokens in the report PDF.

---

## 2. Data and migration

Migration `design/migrations/2026-11-03-integrity-chain.sql` (idempotent, mirrored in `anamnys-db-script.sql`, and
appended to the chains of the other migration tests). **It must be applied to any persistent environment before the new
server version signs anything.** Without it signing fails (the seal insert cannot run); the startup backfill
(section 6) only logs a warning and skips. It:

- drops `DocumentHashes` (schema only, nothing used it);
- creates `IntegritySeals`: `ProviderId`, `Sequence` (1, 2, 3 per provider, no gaps), `SubjectType` (`note` or
  `document`), `SubjectId` (**no foreign key**: the seal outlives a discarded record), `ContentHash`, `PreviousHash`
  (null only for sequence 1), `Hash`, `SealedAt`, `Retroactive`, `DiscardedAt`. Unique on provider plus sequence and on
  subject type plus subject id (a subject is sealed once; a correction is a new document id);
- creates `IntegrityAnchors`: `ProviderId`, `Sequence` (the head when stamped), `HeadHash`, `TsaUrl`, `Token` (the full
  RFC 3161 token, DER) and `AnchoredAt` (the `genTime` inside the token). Unique on provider plus sequence;
- makes both tables append-only with triggers, so even the application cannot rewrite them: seals reject DELETE and
  reject UPDATE unless the only change is `DiscardedAt` going from null to a value; anchors reject UPDATE and DELETE;
  both reject TRUNCATE (statement trigger `Integrity_no_truncate`);
- adds the audit actions `integrity_report_downloaded` and `integrity_check_failed` to `AuditEntries_subject_ck`.

`Notifications` has no kind constraint in the database, so the notice kind `integrity_problem` needed no SQL.

Seal hash (`IntegritySeals.SealHash`), lower-case hex SHA-256 of the UTF-8 text:

```
(PreviousHash or "") | Sequence | SubjectType | SubjectId (lower-case, hyphenated) | ContentHash | SealedAt
```

`SealedAt` is UTC, `yyyy-MM-ddTHH:mm:ss.ffffffZ`, **truncated to microseconds before hashing and inserting**, so the
stored value and the hashed value agree.

---

## 3. Canonical content (`IntegrityCanonical`)

One routine, used to seal and to verify. Output: the UTF-8 bytes of a JSON object written by hand
(`CanonicalJsonWriter`, private to `IntegrityCanonical`), fixed key order, no whitespace, every string normalized to
Unicode NFC, timestamps in UTC with microseconds, ids lower-case hyphenated, integers in invariant digits, nulls written
as `null` (never omitted). The content hash is the hex SHA-256 of those bytes. The first key is `"v": 1`, so the format
can evolve.

**Escaping rule (version 1, frozen).** Property names and string values follow the same rule, and it depends on no
runtime table:

- `"` is written `\"` and `\` is written `\\`;
- U+0000 to U+001F are written `\u00xx`, four hex digits in lower case (`\u000a`, `\u0009`, `\u001f`); the short forms
  `\n`, `\t` and so on are never used;
- every other character is written raw as UTF-8, including U+007F, U+2028/U+2029, `/ < > & '`, code points that are
  unassigned today (U+0378) and characters outside the BMP (emoji, as their 4-byte UTF-8 form);
- a lone surrogate cannot be encoded and throws (PostgreSQL `text` cannot hold one, so it never reaches sealing).

A library encoder is not used on purpose: `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` escapes code points that are
unassigned in the Unicode version compiled into that .NET release, so a runtime upgrade that assigns them would change
the bytes and raise a false `content_changed`. `IntegrityCanonicalTests` pins golden bytes for U+0378 and an emoji.

- **Note:** `v`, `type`, `id`, `patientId`, `format`, `sections` (ordered by position, then key; each `key`,
  `position`, `content`), `signedByCrp`, `signedAt`. Not included: the raw transcript, status, export and structure
  fields.
- **Document:** `v`, `type`, `id`, `patientId`, `noteId`, `kind`, `body`, `fields` (keys sorted ordinally),
  `confirmations`, `team`, `rulesetId`, `signedAt`, `signedByCrp`, `signedByName`, `signedCity`,
  `signedPatientName`. Not included: `SupersededBy` and `RetentionUntil` (they legitimately change after signing), and
  PDF and export fields.

The seal covers the content, not the PDF: a PDF can be rendered again and differ byte by byte with the same content.

---

## 4. Sealing

`IntegritySeals.SealAsync` runs **inside the caller's transaction** (it throws if there is none):

1. `LockAsync`: `pg_advisory_xact_lock` on a key derived from the provider id (the first 8 bytes XOR a constant), so one
   provider's appends are serialized and providers never wait on each other. The lock is re-entrant within a
   transaction.
2. Read the provider's head seal.
3. Insert the new seal with sequence head + 1 and `PreviousHash` = the head's `Hash`.

Callers: `NoteEditing.SignAsync` and `ClinicalDocumentService.SignAsync`. Both read the signed row back from the database
(no tracking) after `SaveChanges` and hash that, so the hash equals what verification will read. If sealing throws, the
signing transaction rolls back: a signed record without a seal never exists. A correction signs a new document version
with its own seal; the old version's seal is untouched.

---

## 5. Discard

`DiscardService` replaces the old `DocumentHashes` delete with an UPDATE that sets `DiscardedAt` on the patient's note
and document seals (provider scoped, before the rows are deleted; not counted in the disposal term). The seal holds only
ids and hashes, so it keeps proving that the record existed and was discarded, not tampered with. A discarded subject is
checked through the chain link only; its content is gone.

---

## 6. Backfill of records signed before F-45

Server code, not SQL, because hashing needs the canonical routine (`IntegrityBackfill`). `IntegrityBackfillService` is a
hosted service that runs **at every server start**, after `DatabaseInitializer`: for each provider with signed records
that have no seal, one transaction (provider lock taken first) seals them in `SignedAt` order (ties by id), with
`Retroactive = true` and `SealedAt` = the run time, **never back-dated**: the proof holds from the deployment on, not
from the signing date. It is idempotent (sealed subjects are skipped), appends after live seals, logs counts or the
exception type only, and never blocks startup on failure. Providers are isolated and processed in uuid order: when one
provider's transaction fails, it rolls back whole, the exception type is logged (no message, ids or data) and the run
goes on with the next provider; the final log line gives the seals created and the number of providers that failed
(those are retried at the next start). When the F-45 tables are absent it logs a warning and skips.

---

## 7. Verification (`IntegrityVerifier`)

`VerifySubjectAsync(db, providerId, subjectType, subjectId, tsa, ct)` returns a status:

| Status | Meaning |
|---|---|
| `intact` | content hash recomputed from the current rows equals `ContentHash`, the seal's `Hash` recomputes and `PreviousHash` equals the previous seal's `Hash` |
| `content_changed` | recomputed content hash differs (also when a non-discarded record is gone) |
| `chain_broken` | the seal's own hash or its link to the previous seal does not recompute |
| `unsealed` | signed, no seal (should not happen after the backfill) |
| `discarded` | the seal is marked discarded |

It also returns `sealedAt`, `retroactive` and `anchored`. **`anchored` is true only when an anchor at or after the
seal's sequence has a `HeadHash` equal to the `Hash` of the seal at that sequence and a token that verifies** (a forged
anchor row does not count). A later anchor covers earlier seals.

`VerifyChainAsync(db, providerId, tsa, now, ct)` walks the seals in sequence order in batches of 500 (contents of a
batch recomputed with two or three queries) and checks: sequence starts at 1 with no gaps (`sequence_gap`, reported at
the missing number), each link and hash (`chain_broken`), each non-discarded subject's content (`content_changed`), then
each anchor (`anchor_invalid`). It returns `totalSeals`, `discarded`, `firstProblem` (`sequence`, `subjectType`, `kind`),
`lastAnchor` (`sequence`, `anchoredAt`, `tsaUrl`) and `verifiedAt`. All queries are scoped to the provider.

---

## 8. Timestamping

- **Request.** `Rfc3161TimestampRequest.CreateFromHash` (package `System.Security.Cryptography.Pkcs` 10.0.11, which is
  not in the shared framework): SHA-256 of the UTF-8 hex head hash as the message imprint, 16-byte random nonce,
  certificate requested; POST `application/timestamp-query`. `ProcessResponse` checks status, nonce and imprint.
- **Cap.** The response is read up to 64 KiB (`MaxResponseBytes`) whether or not a `Content-Length` is sent; nothing
  larger is parsed.
- **Verification** (`TimestampTokens.Verify`, never throws): the token decodes, its signature verifies for the imprint,
  and the signer certificate builds a chain to the configured root (custom root trust, evaluated at `genTime`, extended
  key usage id-kp-timeStamping `1.3.6.1.5.5.7.3.8` required, no revocation check). A token is verified before it is stored and again on every chain check.
- **Configuration** (`IntegrityOptions`): `Integrity:TsaUrl` (default `https://freetsa.org/tsr`) and
  `Integrity:TsaRootCertificate` (PEM of the authority's root). Startup refuses an invalid URL, an unparsable root, and
  a missing root outside Development. In Development the AppHost passes FreeTSA and its root,
  `anamnys-aspire.AppHost/integrity/freetsa-cacert.pem`, so manual tests exercise a real authority; without a root the
  job verifies but does not stamp. Automated tests use `FakeTimestampAuthority` (generated root and TSA certificate),
  never the network.
- **Privacy and failure.** Only the head hash leaves the system. Failures (network, invalid response) are logged by
  exception type only and retried the next night. Signing never depends on the authority.
- **Anchoring** (`IntegrityAnchoring.AnchorAsync`): no database transaction is open while the authority answers; the
  insert is `ON CONFLICT ("ProviderId","Sequence") DO NOTHING`; `AnchoredAt` is the token's `genTime`.

---

## 9. The nightly job (`IntegrityJob`)

A `BackgroundService` on the `RetentionJob` pattern (registered under `Notifications:WorkerEnabled`, so tests do not run
it): at start and then daily it (1) timestamps the head of every provider whose head sequence is greater than their
last anchor's, and (2) verifies every chain. For each problem it writes, in one transaction and only when the notice is
new, an in-app notice (kind `integrity_problem`, `SubjectType` `integrity`, `DedupeKey` `integrity:<sequence>:<kind>`
against the unique `(ProviderId, DedupeKey)` index) and an audit entry `integrity_check_failed` (value
`"<sequence> <kind>"`, no clinical data, actor = the provider). The notification inbox lists these notices without a
patient join, and the bell links to the Conformidade page.

---

## 10. Endpoints

In the PHI group, cookie session; unknown, foreign or draft ids answer 404; all answers are `Cache-Control: no-store`
and carry ids, dates and results only.

| Method and path | Success | Errors |
|---|---|---|
| `GET /api/phi/notes/{noteId}/integrity` | 200 `{ status, sealedAt, retroactive, anchored }` | 401, 404 |
| `GET /api/phi/documents/{documentId}/integrity` | same | 401, 404 |
| `GET /api/phi/providers/me/integrity` | 200 chain summary (section 7) | 401, 404 (no provider row) |
| `GET /api/phi/providers/me/integrity/report.pdf` | 200 `application/pdf`, attachment `comprovante-integridade-<yyyy-MM-dd>.pdf` | 401, 404 (no provider row) |

The report is rendered with QuestPDF (`IntegrityReportPdf`) and audited as `integrity_report_downloaded` (value
`"<total> intact"` or `"<total> <sequence> <kind>"`). Content: provider name and CRP, generation date (practice time
zone), result, total seals, discarded count, head sequence and hash, every anchor (sequence, date, authority, valid or
invalid, head hash), and the explanation in section 12. No clinical text and no embedded tokens.

---

## 11. Provider UI

- **Badge** (`IntegrityBadge`) on the signed note (`SignCard`) and the signed document header; none on drafts. Texts
  (`integrity.badge.*` in `pt.json`): "Íntegro desde a assinatura em DD/MM/AAAA"; for retroactive seals "Íntegro —
  selado em DD/MM/AAAA"; "aguardando carimbo de tempo" while not anchored; in red "Conteúdo diferente do selado em
  DD/MM/AAAA" for `content_changed`, and "Sequência de selos interrompida (selado em DD/MM/AAAA)" for `chain_broken`,
  both linking to `/compliance#integridade`. `unsealed` and `discarded` show no badge.
- **Conformidade page:** the card "Integridade dos registros" (`IntegrityCard`, collapsible id `integrity`) at the top:
  chain state, total seals, last timestamp (date and authority), "Verificar agora" (refetch) and "Baixar comprovante
  (PDF)" (blob download, toast on failure). The `#integridade` hash opens and scrolls to it on mount. A problem reads
  "Problema no selo nº N (nota|documento): <kind>", without the parenthesis when `subjectType` is null
  (`sequence_gap`, `anchor_invalid`); `integrity.card.kind` has copy for all four kinds ("selo ausente na sequência",
  "sequência interrompida", "conteúdo diferente do selado", "carimbo de tempo não confere") and a generic
  "problema de integridade não identificado" for an unknown kind, so a raw code is never shown.
- Shared: `packages/shared/src/api/integrity.ts` (`integrityApi`), types in `types.ts`, and `NotificationKind`
  `integrity_problem` in the bell.

---

## 12. How an outside party verifies a token

The anchor proves that a chain head existed at a given time, without trusting Anamnys. With the stored token as
`token.tsr` (DER), the head hash from the report as `HASH` and the authority's root certificate as `raiz.pem`:

```bash
printf '%s' "$HASH" > cabeca.txt
openssl ts -verify -data cabeca.txt -in token.tsr -token_in -CAfile raiz.pem
```

`printf '%s'` writes the hex text with no newline: the message hashed by the authority is the UTF-8 hex string of the
head hash. Expected answer: `Verification: OK`. This exact command was run against a token from FreeTSA (root:
`freetsa.org/files/cacert.pem`). To see the time: `openssl ts -reply -in token.tsr -token_in -text`. The report lists
the head hash of each anchor; recomputing the chain up to that sequence then shows that nothing before it changed.

---

## 13. Tests

Under `anamnys-aspire.Tests/Integrity/` and `Data/IntegrityChainMigrationTests.cs`, on scratch databases (the dev
database may lack the migration), with the fake authority:

- `IntegrityCanonicalTests`: same input same hash, key and section order, NFC and NFD, nulls, exact UTF-8 bytes, the
  escaping rule (golden bytes for U+0378 and an emoji written raw, controls as `\u00xx`, field names escaped alike).
- `IntegritySealTests`: seal in the signing transaction, correction gets its own, concurrent signings consecutive,
  sealing failure rolls signing back, discard marks seals.
- `IntegrityChainMigrationTests`: triggers (update, delete, truncate, `DiscardedAt` once), shapes against the bootstrap.
- `IntegrityVerifierTests`: `content_changed` via SQL, `chain_broken`, gap, discard keeps the chain intact, provider
  isolation.
- `IntegrityAnchoringTests`: token stored and verifies, tampered token, other head, other root, outage then retry,
  forged anchor row is not `anchored`, 64 KiB cap.
- `IntegrityBackfillTests` (order, idempotence, one failing provider does not stop the next), `IntegrityJobTests` (one notice per problem, not repeated, audit),
  `IntegrityEndpointTests` (401 over HTTP; handlers on scratch databases for shapes, 404, `no-store`, the PDF and its
  audit).

Existing signing tests (`ClinicalDocument`, `NoteSigning`) go through the dev database and need the migration applied
there. Provider and patient builds and lint were run for the UI; no browser check.

---

## 14. Deployment

- Apply `2026-11-03-integrity-chain.sql` to every persistent environment before the new version starts; the backfill
  runs at the next server start.
- Publish mode needs the AppHost parameters `integrity-tsa-url` (env `Integrity__TsaUrl`) and
  `integrity-tsa-root-certificate` (PEM, env `Integrity__TsaRootCertificate`). The server refuses to start outside
  Development without the root. Allow outbound HTTPS from the server to the authority.
- See chapter 20, sections 2.1 and 2.2.

---

## 15. Limits and known gaps

- External documents and disclosure authorizations are not sealed.
- The tokens are not embedded in the report PDF (the report lists head hashes; the tokens are in `IntegrityAnchors`).
- A rotation of the authority's root invalidates the verification of earlier anchors until the old root is kept too
  (only one root is configured). **Must be solved before the first production deploy.**
- Only the first problem of a chain is reported and notified per run.
- No sanity check of `genTime` against the clock.
- The startup backfill runs in `StartAsync` and delays startup on a large first run. **Must move off server startup
  (a background service) before the first production deploy.**
- The report file name uses the UTC date.

**Trust limits** (what "demonstrable" rests on):

- `DiscardedAt` is not covered by any hash. Someone with database write access can mark a seal discarded and so hide a
  later change of its content: the discard is visible, but not proven. Cross-check it with the disposal terms
  (`DisposalTerms`).
- The chain after the last timestamp, and the tokens themselves, live only in the database. Truncating the unanchored
  tail, or a superuser rewriting the whole chain and deleting the anchors with the triggers disabled, cannot be detected
  from inside. The tokens do not leave the database in this delivery, so the outside proof is only as good as the
  reports the provider kept.
- An anchor proves that the head existed at `genTime`, not when each record was signed; retroactive seals prove nothing
  before the deployment.

---

## 16. Where things are

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-11-03-integrity-chain.sql` |
| Entities | `anamnys-aspire.Server/Data/Entities/IntegritySeal.cs`, `IntegrityAnchor.cs` |
| Server code | `anamnys-aspire.Server/Integrity/` |
| Signing hooks | `Notes/NoteEditing.cs`, `Documents/ClinicalDocumentService.cs`, `Retention/DiscardService.cs` |
| Development root certificate | `anamnys-aspire.AppHost/integrity/freetsa-cacert.pem` |
| Provider UI | `apps/provider/src/components/integrity/`, `components/compliance/IntegrityCard.tsx` |
| API client | `packages/shared/src/api/integrity.ts` |
| Tests | `anamnys-aspire.Tests/Integrity/`, `anamnys-aspire.Tests/Data/IntegrityChainMigrationTests.cs` |
