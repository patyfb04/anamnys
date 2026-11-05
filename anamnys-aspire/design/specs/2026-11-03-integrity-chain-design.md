# Document Integrity Chain (F-45)

**Date:** 2026-11-03
**Status:** Approved for implementation
**Scope:** Backlog F-45. An append-only hash chain proving that no signed note or signed issued document was altered
after signing, anchored daily outside the database with RFC 3161 timestamps.

Done when (backlog): any modification after signing is detectable and demonstrable.

Explicitly out of scope: external documents received (F-07) and disclosure authorizations (F-17) are not sealed in
this delivery (recorded as a gap); verification by the patient; the patient dossier (F-44), which will reuse the
verification report.

## 1. Decisions (agreed with the user, 2026-11-03)

| Topic | Decision |
|---|---|
| What is sealed | Signed notes and signed issued documents, every correction version included. The seal covers the canonical content at signing time, not the PDF (a PDF can be rendered again and differ byte by byte with the same content). |
| Chain shape | One chain per provider. Each seal points to the hash of that provider's previous seal, so deleting, changing or reordering any record breaks the chain from that point on. |
| Discard | The retention discard deletes the content but keeps the seal, marked `DiscardedAt`. The seal holds only ids and hashes, no clinical text. The chain stays verifiable and proves the record existed and was discarded, not tampered with. |
| External anchor | Once a day, per provider with new seals, the chain head hash is timestamped by an RFC 3161 Time Stamping Authority. Only the hash leaves the system. A TSA outage never blocks signing; the job retries the next night. |
| Verification UI | A badge on the signed note and document pages, an "Integridade dos registros" block on the Conformidade page with "Verificar agora" and a PDF verification report, and a nightly check that raises an in-app notice. |
| Records signed before F-45 | Sealed once at deployment, in signing order, flagged retroactive, with `SealedAt` set to the deployment time (never back-dated to the signing date). The proof holds from then on. |

## 2. Data

Migration `design/migrations/2026-11-03-integrity-chain.sql`. Drops `DocumentHashes` (schema only, no code uses it;
`DiscardService` stops deleting from it) and creates:

```sql
CREATE TABLE "IntegritySeals" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "ProviderId" uuid NOT NULL REFERENCES "Providers"("Id") ON DELETE RESTRICT,
    "Sequence" bigint NOT NULL,                -- 1, 2, 3... per provider, no gaps
    "SubjectType" text NOT NULL,               -- 'note' | 'document'
    "SubjectId" uuid NOT NULL,                 -- no foreign key: the seal outlives the discarded record
    "ContentHash" text NOT NULL,               -- lowercase hex SHA-256 of the canonical content
    "PreviousHash" text,                       -- null only for Sequence 1
    "Hash" text NOT NULL,
    "SealedAt" timestamptz NOT NULL,
    "Retroactive" boolean NOT NULL DEFAULT false,
    "DiscardedAt" timestamptz,
    CONSTRAINT "IntegritySeals_Provider_Sequence_key" UNIQUE ("ProviderId", "Sequence"),
    CONSTRAINT "IntegritySeals_Subject_key" UNIQUE ("SubjectType", "SubjectId"),
    CONSTRAINT "IntegritySeals_SubjectType_ck" CHECK ("SubjectType" IN ('note', 'document')),
    CONSTRAINT "IntegritySeals_Previous_ck" CHECK (("Sequence" = 1) = ("PreviousHash" IS NULL))
);

CREATE TABLE "IntegrityAnchors" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "ProviderId" uuid NOT NULL REFERENCES "Providers"("Id") ON DELETE RESTRICT,
    "Sequence" bigint NOT NULL,                -- the seal that was the head when stamped
    "HeadHash" text NOT NULL,
    "TsaUrl" text NOT NULL,
    "Token" bytea NOT NULL,                    -- the full RFC 3161 TimeStampToken (DER)
    "AnchoredAt" timestamptz NOT NULL,         -- the genTime inside the token
    CONSTRAINT "IntegrityAnchors_Provider_Sequence_key" UNIQUE ("ProviderId", "Sequence")
);
```

Triggers make both tables append-only at the database level, so even the application cannot rewrite them:

- `IntegritySeals`: `BEFORE DELETE` raises; `BEFORE UPDATE` raises unless the only change is `DiscardedAt` going from
  null to a value.
- `IntegrityAnchors`: `BEFORE UPDATE OR DELETE` raises.

`AuditEntries_subject_ck` gains `integrity_report_downloaded` and `integrity_check_failed`; the `Notifications` kind
list gains `integrity_problem` (channel `in_app`).

Seal hash:

```
Hash = hex(SHA-256(UTF-8(
    PreviousHash ?? "" + "|" + Sequence + "|" + SubjectType + "|" + SubjectId (lowercase, hyphenated) + "|" +
    ContentHash + "|" + SealedAt (UTC, "yyyy-MM-ddTHH:mm:ss.ffffffZ")
)))
```

## 3. Canonical content (`IntegrityCanonical`)

One routine, used both to seal and to verify. Output: UTF-8 bytes of a JSON object written by hand (no library encoder),
keys in the fixed order below, no whitespace, every string normalized to Unicode NFC, timestamps in UTC as
`yyyy-MM-ddTHH:mm:ss.ffffffZ` (PostgreSQL microsecond precision), ids lowercase hyphenated. `ContentHash` is the
lowercase hex SHA-256 of those bytes. A version field `"v": 1` comes first so the format can evolve.

Escaping rule (version 1, frozen, the same for property names and string values): `"` becomes `\"`, `\` becomes `\\`,
U+0000 to U+001F become `\u00xx` (four lowercase hex digits, never the short forms `\n`, `\t`...); every other character,
including unassigned code points and characters outside the BMP, is written raw as UTF-8. The rule depends on no runtime
Unicode table, so a .NET upgrade cannot change the bytes. (Amended after the final review, I4: the first implementation
used `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, which escapes code points unassigned in the runtime's Unicode
version. Only dev seals existed; a dev database sealed with that encoder must be re-sealed.)

- **Note:** `v`, `type` ("note"), `id`, `patientId`, `format`, `sections` (ordered by `Position`, then `Key`; each
  `{ key, position, content }`), `signedByCrp`, `signedAt`. Not included: the raw transcript (not the signed record),
  status, export fields, structure state.
- **Document:** `v`, `type` ("document"), `id`, `patientId`, `noteId`, `kind`, `body`, `fields` (keys sorted
  ordinally), `confirmations` (stored order), `team` (stored order, every field of each member), `rulesetId`,
  `signedAt`, `signedByCrp`, `signedByName`, `signedCity`, `signedPatientName`. Not included: `SupersededBy` and
  `RetentionUntil` (they legitimately change after signing), PDF and export fields.

Nulls are written as JSON null, never omitted.

## 4. Sealing (`IntegritySeals.SealAsync`)

`SealAsync(db, providerId, subjectType, subjectId, contentHash, now, ct)` runs inside the caller's transaction:

1. `pg_advisory_xact_lock` on a key derived from the provider id (serializes one provider's appends; providers never
   wait on each other).
2. Read the provider's head seal (highest `Sequence`).
3. Insert the new seal with `Sequence = head + 1`, `PreviousHash = head.Hash`.

Callers:

- `NoteEditing.SignAsync`: after the note becomes `Signed` and its sections are final, in the same transaction.
- `ClinicalDocumentService.SignAsync`: after the document is signed, same transaction. A correction creates a new
  document version that is sealed when it is signed; the old version's seal is untouched.

If sealing throws, the transaction rolls back and the signing fails: a signed record without a seal never exists.

Discard: `DiscardService` replaces the `DocumentHashes` delete with
`UPDATE "IntegritySeals" SET "DiscardedAt" = now WHERE "SubjectId" IN (<the patient's notes and documents>)`.

Retroactive backfill: not a SQL script but server code (`IntegrityBackfill`), because hashing needs the canonical
routine. It runs once at startup when a provider
has signed records without seals, in `SignedAt` order (ties by id), with `Retroactive = true` and `SealedAt` = the run
time. It is idempotent (subjects already sealed are skipped) and logs counts only.

## 5. Verification (`IntegrityVerifier`)

- `VerifySubjectAsync(db, providerId, subjectType, subjectId, ct)` returns one of:
  - `intact`: the content hash recomputed from the current rows equals `ContentHash`, the seal's `Hash` recomputes,
    and `PreviousHash` equals the previous seal's `Hash`;
  - `content_changed`: the recomputed content hash differs;
  - `chain_broken`: the seal's own hash or its link to the previous seal does not recompute;
  - `unsealed`: signed but no seal (should not happen after the backfill);
  - `discarded`: the seal is marked discarded.
  Also returns `sealedAt`, `retroactive`, and `anchored` (a valid anchor exists with `Sequence` >= the seal's).
- `VerifyChainAsync(db, providerId, ct)` walks the provider's seals in `Sequence` order in batches of 500 and checks:
  sequence starts at 1 with no gaps; each `PreviousHash` equals the previous `Hash`; each `Hash` recomputes; each
  non-discarded subject's content hash recomputes. Then each anchor: the token verifies (signature, TSA certificate
  chained to the configured root, message imprint equals SHA-256 of `HeadHash`), and `HeadHash` equals the `Hash` of
  the seal at that `Sequence`. Returns: total seals, discarded count, the first problem (`sequence`, `subjectType`,
  `kind`), the last valid anchor (`sequence`, `anchoredAt`, `tsaUrl`), and `verifiedAt`.

Discarded subjects are checked through the link only (their content is gone).

## 6. Timestamping (`IntegrityAnchoring`)

- `Rfc3161TimestampRequest.CreateFromHash` (System.Security.Cryptography.Pkcs, already part of .NET) with SHA-256 of
  `HeadHash`, a random nonce, `requestSignerCertificates: true`; POST `application/timestamp-query` to the TSA;
  `ProcessResponse` validates the nonce and imprint; the token is then verified against the configured root before
  storing.
- Configuration: `Integrity:TsaUrl` (default `https://freetsa.org/tsr`) and `Integrity:TsaRootCertificate` (PEM). The
  AppHost passes both; outside Development they are required.
- Development and tests: a fake TSA (`FakeTimestampAuthority`) signs tokens with a locally generated certificate that
  is configured as the root.
- Only the hash leaves the system. Failures (network, invalid response) are logged without clinical data and retried
  the next night. Signing never depends on the TSA.

## 7. Nightly job (`IntegrityJob`)

A `BackgroundService` on the `RetentionJob` pattern, once a day:

1. For each provider whose head `Sequence` is greater than their last anchor's, timestamp the current head.
2. Verify each provider's chain. On a problem: one in-app notice per problem (`Notifications`, kind
   `integrity_problem`, `DedupeKey` = `integrity:<sequence>:<kind>`, so the same problem does not repeat every
   night), and an audit entry `integrity_check_failed` (no clinical data, the sequence and kind only).

## 8. Endpoints

All in the PHI group, scoped to the signed-in provider; unknown or foreign ids answer 404; responses are `no-store`
and carry ids, dates and results only.

| Route | Answer |
|---|---|
| `GET /api/phi/notes/{id}/integrity` | `{ status, sealedAt, retroactive, anchored }` |
| `GET /api/phi/documents/{id}/integrity` | same |
| `GET /api/phi/providers/me/integrity` | the chain summary from `VerifyChainAsync` |
| `GET /api/phi/providers/me/integrity/report.pdf` | the verification report (QuestPDF), audited as `integrity_report_downloaded` |

Report content: provider name and CRP, generation date, total seals, discarded count, head sequence and hash, every
anchor (date, TSA, head sequence and hash), the result (intact or the first problem), and a short explanation of how an
outside party can check a timestamp token with standard tools (`openssl ts -verify`). The tokens themselves are not
embedded in this delivery.

## 9. Provider UI

- Signed note page and document page: a small badge next to the signature:
  - "Íntegro desde a assinatura em DD/MM/AAAA", or, for retroactive seals, "Íntegro — selado em DD/MM/AAAA";
  - "aguardando carimbo de tempo" while not yet anchored;
  - on a problem, in red: "Conteúdo diferente do selado em DD/MM/AAAA" with a link to Conformidade.
  No badge on drafts.
- Conformidade page: new collapsible `DetailCard` "Integridade dos registros" (with a top anchor like the other
  blocks): chain state (intact or the first problem), total seals, last timestamp (date and authority), buttons
  "Verificar agora" (refetches the summary) and "Baixar comprovante (PDF)".
- Copy in `pt.json` under `integrity.*`.

## 10. Testing

- Canonical: same input gives the same hash; key and section order fixed; NFC and NFD input give the same hash; nulls
  are written.
- Sealing: signing a note and a document creates the seal in the same transaction; a correction gets its own seal;
  two concurrent signings by the same provider get consecutive sequences; a sealing failure rolls the signing back.
- Database triggers: UPDATE or DELETE of a seal fails; setting `DiscardedAt` once passes; changing it again fails;
  anchors cannot be changed.
- Verification: changing a signed section's text via SQL gives `content_changed`; changing a stored `Hash` gives
  `chain_broken`; removing a seal (trigger disabled inside the test) shows the gap; the discard keeps the chain intact;
  one provider never sees another's chain.
- Timestamping: with the fake TSA the token is stored and verifies; a tampered token or a different head hash fails;
  a TSA outage leaves signing unaffected and the job retries.
- Backfill: seals existing signed records in order, flagged retroactive; a second run adds nothing.
- Job: one notice per problem, not repeated; the report PDF renders; endpoints answer 401 and 404.
- Front: provider build and lint.

## 11. Documentation and deployment

- Chapter `documentation/38-integrity-chain.md`: model, canonical content, sealing, verification, timestamping, how to
  verify from outside.
- Migration `2026-11-03-integrity-chain.sql` applied to any persistent environment; the backfill runs at the next
  server start.
- AppHost: `Integrity:TsaUrl` and `Integrity:TsaRootCertificate`.
- Status, gaps (external documents and authorizations not sealed; tokens not embedded in the report) and Notion
  (F-45 ticked, sequence diagram).
