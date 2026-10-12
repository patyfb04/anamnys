# Live Session — Note Structuring

**Date:** 2026-10-12
**Status:** Approved
**Scope:** Delivery 2 of "Sessão ao Vivo". After Whisper transcribes a dictation, an in-process
LLM (Qwen2.5 7B Instruct, CPU) reorganises the transcript into the sections of the chosen note
format (DAP, SOAP or Biopsychosocial) and stores them in `NoteSections`. The note page shows the
sections, marked as an AI draft, and lets the provider regenerate them in another format while
nothing has been edited. Builds on `2026-10-10-live-session-transcription-design.md`.

Out of scope: structuring typed text (Notion flow 03), editing, signing and audit (delivery 3),
splitting transcripts longer than the model context.

---

## 1. Context

- Delivery 1 produces a `VoiceLive` note whose `RawTranscript` is filled by a single in-process
  worker (`TranscriptionWorker` → `NoteTranscription.ProcessAsync`), with progress on
  `/hubs/progress` and a 5 s polling fallback on the processing screen and the note page.
- `NoteSections` already exists: `Id`, `NoteId`, `Key`, `Content`, `Position`, `GeneratedAt`,
  `EditedAt`, unique (`NoteId`, `Key`). Nothing writes it yet.
- `Notes.Format` accepts `DAP`, `SOAP`, `Biopsychosocial`, `PtFunctional`; live session offers the
  first three.
- CFP rule (Notion, Manual Orientativo 2025 item 16): software may format and organise; it must not
  interpret, conclude or raise hypotheses. An empty section stays empty. The AI draft and the
  provider's edits must stay distinguishable.
- Hardware: CPU only (decided in delivery 1). All inference in-process (CLAUDE.md).

## 2. Decisions

| Topic | Decision |
|---|---|
| Model | Qwen2.5 7B Instruct, GGUF Q4_K_M (~4.7 GB), Apache 2.0. Configurable; downloaded on first use and verified against a pinned SHA-256, like the Whisper model. |
| Runtime | LLamaSharp with the CPU backend, one `LLamaWeights` per process. |
| What the AI may do | Put each dictated passage in the right section, remove speech fillers and repetitions, fix punctuation, use record tone. Never add facts, hypotheses, conclusions or anything not dictated. A section with nothing dictated is `""`. |
| Output | JSON constrained by a GBNF grammar generated from the format's section keys, so the output always has exactly those keys. Low temperature (0–0.2), capped output tokens. |
| Where it runs | Same queue and same single worker as transcription (Whisper and the LLM compete for the same CPU). Two job kinds: `TranscribeAndStructure` and `Structure`. |
| When the transcript shows | As soon as it exists: the note becomes `ReadyForReview` after transcription; structuring continues with `StructureStatus = pending`. |
| Structuring failure | Never fails the note. `StructureStatus = failed`; the transcript stays; the page offers "Tentar de novo". |
| Regenerate | Allowed only while the note is `ReadyForReview`, no section has `EditedAt`, the transcript is not empty and nothing is pending. Replaces all sections. |
| Context limit | `NoteStructuring:ContextSize` (default 8192 tokens, ~50 min of speech). A transcript that does not fit fails with reason `too_long`. |
| Language | Portuguese only (prompt and UI). |

## 3. Data

Migration `design/migrations/2026-10-12-note-structuring.sql` (idempotent, one transaction, same
header as earlier migrations), mirrored in `anamnys-db-script.sql`:

- `Notes."StructureStatus" text`, null for notes never structured; check
  `("StructureStatus" IS NULL) OR ("StructureStatus" = ANY (ARRAY['pending','done','failed']))`.
- `Notes."StructureFailure" text`, null unless `StructureStatus = 'failed'`; values
  `model_error`, `too_long`, `server_restart`; check that it is set exactly when failed.

EF: `Note` gains `StructureStatus`, `StructureFailure`; new `NoteSection` entity over the existing
table, `DbSet<NoteSection> NoteSections`.

## 4. Formats

`NoteFormats` holds, per format, the section keys in order:

| Format | Keys |
|---|---|
| DAP | `data`, `assessment`, `plan` |
| SOAP | `subjective`, `objective`, `assessment`, `plan` |
| Biopsychosocial | `biological`, `psychological`, `social`, `plan` |

Section titles are UI strings (`pt.json`), not stored.

## 5. Server

New folder `anamnys-aspire.Server/NoteStructuring/`.

- **`NoteStructuringOptions`** (`NoteStructuring:` section): `Model`, `ModelUrl`, `ModelSha256`,
  `ModelDirectory` (default under local application data, like Whisper), `ContextSize`,
  `Threads` (default processor count − 1), `MaxOutputTokens`.
- **`LlmModelProvider`**: download to a temp file, verify SHA-256, atomic rename, load one
  `LLamaWeights`; fail closed without a 64-character hash. The model source (single-file GGUF vs the
  official split files) is chosen in the plan, with its hash cross-checked.
- **`INoteStructurer`**: `Task<StructuredSections> StructureAsync(string transcript, string format,
  CancellationToken ct)`. `LlamaNoteStructurer` builds the Portuguese system prompt with the rules
  of §2, the GBNF grammar for the format, runs stateless inference, parses the JSON and returns the
  sections in format order. Throws `TranscriptTooLongException` when the prompt does not fit.
- **`NoteGrammar`**: builds the GBNF for a format's keys (pure function, unit-tested).
- **Queue:** `TranscriptionJob` becomes `NoteJob(NoteId, ProviderId, Kind, Language, Audio?)`;
  `Kind` is `TranscribeAndStructure` or `Structure`. Audio is null for `Structure`.
- **Worker:** `TranscribeAndStructure` runs the existing transcription; on success it sets
  `ReadyForReview` and sends `NoteReady`, then (if not `no_speech`) runs structuring. `Structure`
  runs structuring only. Progress stages: `transcribing`, `structuring`. New events:
  `NoteStructured(noteId)` and `NoteStructureFailed(noteId, reason)`.
- **`NoteStructuring.ProcessAsync`** (testable, like `NoteTranscription`): reads the transcript and
  format, calls the structurer, and in one transaction deletes the note's sections and inserts the
  new ones with `GeneratedAt = now`, sets `StructureStatus = done` — but only if no section of the
  note has `EditedAt` (re-checked in the transaction); otherwise discards the result and restores
  `done`. On failure sets `failed` with the reason. Logs carry only note id, stage and exception
  type — never prompt, transcript or sections.
- **Finalize** sets `StructureStatus = pending` on new live notes.
- **Orphan sweep** (startup) also marks `StructureStatus = pending` as `failed`
  (`server_restart`).
- **Endpoints** (`/api/phi/providers/me/notes`):
  - `GET {id}` adds `structureStatus`, `structureFailure` and `sections[]`
    (`key`, `content`, `position`, `generatedAt`, `editedAt`).
  - `POST {id}/restructure` with `{ format }`: 202 and enqueue; 400 invalid format; 404 another
    provider's note; 409 when signed, not `ReadyForReview`, empty transcript, a section edited, or
    structuring pending. Sets `Format` and `StructureStatus = pending` before enqueueing.
  - Documented in `anamnys-aspire.Server.http`.

## 6. Provider app

- **Note page:** sections in format order with translated titles; empty section shows
  "Nada ditado para esta seção"; badge "Rascunho gerado por IA"; raw transcript collapsible below
  (open when there are no sections). `pending` → "Organizando em seções…" with 5 s polling.
  `failed` → message by reason plus "Tentar de novo" (same format). When regeneration is allowed:
  format select + "Reorganizar", with a confirmation that current sections will be replaced.
- **Live session processing step:** shows "Transcrevendo…" then navigates on `NoteReady` as today
  (structuring continues on the note page).
- **Prepare step:** one short help line explaining DAP, SOAP and Biopsychosocial.
- All strings in `pt.json`, including section titles per format.

## 7. Failure handling

| Situation | Behaviour |
|---|---|
| Model download fails / hash mismatch / no hash | `failed` (`model_error`); next attempt retries. |
| Invalid JSON or wrong keys (should not happen with the grammar) | `failed` (`model_error`). |
| Output cut by the token limit | `failed` (`model_error`); no partial sections written. |
| Transcript does not fit the context | `failed` (`too_long`). |
| Section edited while structuring ran | Result discarded; edited content untouched; `done`. |
| Server restarts while pending | Startup sweep → `failed` (`server_restart`). |
| `no_speech` transcription | Note `Failed` as today; no structuring. |

## 8. Testing

- `NoteFormats` keys and order; `NoteGrammar` output per format.
- `NoteStructuring.ProcessAsync` with a fake `INoteStructurer` (real Postgres): success writes
  sections in order and `done`; failure → `failed` and transcript kept; edited section → result
  discarded; replacing previous sections on restructure.
- Worker: `TranscribeAndStructure` sends `NoteReady` before structuring; `Structure` jobs.
- Orphan sweep marks `pending` structuring as `failed`.
- Restructure endpoint: 401, 404, 409 cases, 202.
- Migration shape vs bootstrap.
- Real-model test (only with `ANAMNYS_LLM_MODEL` set): a short Portuguese sample transcript gives
  valid JSON with the format's keys, and no digits or capitalised names absent from the transcript.
- Frontend: build and lint; manual checklist.

## 9. Documentation

- Root `README.md`: a short "AI models" section — which models run, where (in-process, CPU, no
  data leaves the server), for what (Whisper.net `base`/`medium`: transcription of the provider's
  dictation; Qwen2.5 7B Instruct Q4: organising the transcript into note sections), and their
  licences.
- `CLAUDE.md` "Backend — present": LLamaSharp (CPU) for note structuring.
- Gaps file: memory sizing (Whisper + LLM ~6 GB + buffers), transcripts longer than the context,
  structuring of typed text.
