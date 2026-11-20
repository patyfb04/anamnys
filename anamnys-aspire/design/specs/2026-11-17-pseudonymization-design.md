# Third-Party Pseudonymisation (G3: F-27)

**Date:** 2026-11-17
**Status:** Approved for implementation
**Scope:** Backlog F-27, the third delivery of Group G (session capture). G4 (F-28 transcript as source) gets its own
spec and depends on this one.

Done when (backlog): the same third party mentioned in sessions 3 and 12 resolves to the same token, and no reversible
mapping exists anywhere in the system.

Wording rule (backlog, LGPD art. 13 §4): the product and any material say "reduz a exposição de terceiros", never
"anonimizado" — the data stays pseudonymised personal data.

## 1. Decisions (agreed with the user, 2026-11-17)

| Topic | Decision |
|---|---|
| How far tokens go | The stored transcript AND the AI draft use tokens. The draft is structured from the pseudonymised transcript; in review the provider keeps the token or types the real name (their choice). |
| Detection | In-process Qwen 7B (the existing LLamaSharp model) in a step before structuring, plus a rule safety net: capitalised words in a list of Brazilian first names (IBGE) that are not excluded are treated as persons. No new model or dependency. |
| Existing data | Applied from now on; Settings → Prontuário gets "Pseudonimizar transcrições antigas" (background, one note at a time, only the source text `RawTranscript`; signed note sections never change). |
| Inputs | The Sessão ao vivo transcript and the text of "Nota com IA" (Note Editor AI mode). Not the manual note, not anything the provider writes or edits in review. |

## 2. Pipeline

- Live session: audio → Whisper → **pseudonymise** → write `RawTranscript` → structure. The original text lives only
  in memory and is cleared at the end of the step (same discipline as the audio: no logs, no exceptions carrying text).
- Note Editor AI mode (`Notes/NoteCreation.cs`): the typed text goes through the same step before `RawTranscript` is
  written; the structuring job starts after.
- Failure (model error, timeout, invalid output): nothing with names is written. The note becomes `Failed` with reason
  `pseudonymization_failed` and the original text is kept in memory for a bounded time (the existing in-memory
  retention of the job, at most 10 minutes) so "Tentar de novo" can retry; after that or after a restart the note stays
  failed (the provider dictates again or writes by hand). The original text is never persisted to retry later.

## 3. Detection

- `IThirdPartyDetector.DetectAsync(text, exclusions, ct) : IReadOnlyList<Span>` where `Span(int Start, int Length,
  EntityKind Kind)`, kinds `person|organization|school|address`.
- `LlamaThirdPartyDetector`: a fixed Portuguese prompt with a GBNF grammar (like `NoteGrammar.cs`) that returns the
  exact surface strings and kinds; spans are located in the text by exact match (all occurrences); strings not found
  verbatim are ignored. Shares the model and the `LlmGate` with structuring.
- Exclusions (never replaced): the patient's first name, last name(s) and a nickname field if present in the record;
  the provider's profile name; kinship and pronoun words without a proper name ("mãe", "pai", "eu", "ele").
- Safety net `FirstNameRule`: a static list of common Brazilian first names (embedded resource, built from public IBGE
  data); any capitalised token that is in the list and not excluded becomes a `person` span if not already covered
  (also at the start of a sentence: over-suppression is preferred to a leak).
- Overlaps resolved longest-first.

## 4. Tokens

- Persons `[P1]`, `[P2]`…; organizations `[Empresa 1]`…; schools `[Escola 1]`…; addresses always `[Endereço]`
  (no numbering).
- Stable per patient: `fingerprint = HMAC-SHA256(patientSecret, kind + ":" + normalize(surface))`, `normalize` =
  trim, lower-case, accents removed, inner whitespace collapsed. Table `PseudonymTokens` (`PatientId`, `Kind`,
  `Fingerprint` bytea, `Number` int, `CreatedAt`; unique (PatientId, Kind, Fingerprint) and (PatientId, Kind,
  Number)). Numbers are assigned in order of first appearance, per kind, under a row lock / unique-violation retry.
  The surface string is never stored.
- `patientSecret`: 32 random bytes per patient, stored in `PatientPseudonymSecrets` (`PatientId` PK, `Protected`
  bytea) encrypted with ASP.NET Core Data Protection (purpose `Anamnys.Pseudonymization.v1`). Discard deletes the
  secret and the tokens.
- No reversible mapping: nothing maps a number back to a name; with the secret one can only test a guessed name
  (documented as the residual risk of pseudonymisation).

## 5. Data

Migration `design/migrations/2026-11-17-pseudonymization.sql` (idempotent, mirrored in `anamnys-db-script.sql`,
migration-test chains after `2026-11-16-transient-audio.sql`): `PatientPseudonymSecrets`, `PseudonymTokens`;
`Notes` gains `TranscriptPseudonymized` boolean NOT NULL default false (true for new notes written through the step;
false for legacy rows until the backfill converts them) and `PseudonymizationFailedAt` timestamptz; failure reason
`pseudonymization_failed` accepted by the notes failure check if there is one; audit actions
`transcript_pseudonymized` (counts per kind in the value, no text), `pseudonymization_failed`,
`pseudonymization_backfill_started`. Table `PseudonymizationBackfills` (`ProviderId` PK, `StartedAt`, `FinishedAt`,
`Total`, `Done`, `Failed`).

## 6. Backfill

- `POST /api/phi/providers/me/pseudonymization/backfill` starts (409 if running); `GET` returns `{ pending, running,
  total, done, failed, startedAt, finishedAt }` (pending = the provider's notes with a non-empty `RawTranscript` and
  `TranscriptPseudonymized = false`).
- A hosted worker processes one note at a time through the same detector (respecting `LlmGate`, lowest priority after
  live work), rewrites only `RawTranscript` and sets `TranscriptPseudonymized = true`; a note whose detection fails
  stays unconverted (counted in `failed`, retried on the next start). Signed or not, note sections are never touched.
- Survives restarts: progress is in the table; on start the worker resumes a running backfill.

## 7. UI

- Note review / note page: a discreet notice when `transcriptPseudonymized` ("Nomes de terceiros foram substituídos
  por códigos ([P1], [Empresa 1]…). O mesmo código indica a mesma pessoa em todas as sessões deste paciente.") and
  tokens highlighted in the transcript and draft display.
- Failed note: "Não foi possível proteger os nomes de terceiros" with "Tentar de novo" (while the in-memory text
  exists; otherwise the existing failed-note guidance).
- Settings → Prontuário: "Pseudonimizar transcrições antigas" — count pending, "Iniciar", progress, failed count.
- Copy in `pt.json` under `pseudonymization.*`.

## 8. Testing

- Tokens: same name in sessions 3 and 12 → same `[P1]`; accent/case variants → same token; two patients → independent
  numbering; kinds numbered separately; addresses always `[Endereço]`.
- Exclusions: patient and provider names never replaced; kinship words untouched.
- Safety net: an IBGE first name missed by the (fake) detector is replaced.
- No reversible mapping: no column stores a surface string; logs, audit values and exceptions contain no name (capturing
  logger); the original text is not persisted on failure.
- Failure: detector error/timeout → note Failed with the reason, no `RawTranscript` with names; retry works while in
  memory.
- AI-mode text goes through the step; manual notes don't.
- Backfill: converts unconverted notes, leaves sections and seals untouched (integrity chain still verifies), resumes
  after restart, counts failures, scoping.
- Discard removes secrets and tokens; migration and chains. The detector is a fake with fixed answers in tests; a real
  Qwen run is the user's manual check (prompt quality on Portuguese clinical speech).
- Front: provider build and lint.

## 9. Out of scope / gaps

Recognising that "o Zé" and "José" are the same person (different tokens); pseudonymising what the provider writes in
review; third-party names inside uploaded external documents and their extracted facts; voice/audio (already
destroyed); a provider-side legend of tokens (would rebuild a reversible map).

## 10. Documentation

Chapter `documentation/46-pseudonymization.md`; index; status (F-27); gaps; Confluence (F-27).
