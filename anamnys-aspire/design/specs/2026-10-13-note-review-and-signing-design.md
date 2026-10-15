# Live Session — Note Review and Signing

**Date:** 2026-10-13
**Status:** Implemented
**Scope:** Delivery 3 of "Sessão ao Vivo". The provider edits the sections of a live-session note
one at a time, can write sections by hand when the AI produced none, and signs the note, which
locks it for good. Every note mutation — AI generation, edit, blank sections, signing — appends an
`AuditEntries` row in the same transaction. Builds on `2026-10-10-live-session-transcription-design.md`
and `2026-10-12-note-structuring-design.md`.

Out of scope (gaps file): showing the audit history, correcting a signed note through a new linked
version, PDF export.

---

## 1. Context

- `NoteSections` has `Content`, `Position`, `GeneratedAt`, `EditedAt`; nothing sets `EditedAt` yet.
  Structuring (`NoteStructuringJob`) discards its result if any section has `EditedAt`; the
  restructure endpoint refuses when any section has `EditedAt`.
- `AuditEntries` (`NoteId`, `DocumentId`, `ActorId`, `Action`, `FieldChanged`, `PreviousValue`,
  `NewValue`, `At`) exists with an FK to `Notes` (`ON DELETE RESTRICT`); nothing writes it.
  CLAUDE.md: every note mutation appends an audit entry — a hard requirement.
- `Notes_Signed_ck`: `Status IN ('Signed','Exported')` ⇔ `SignedAt IS NOT NULL`.
- `Providers.CrpNumber` / `CrpRegion` exist (both null or both set) and are editable on the profile
  page (`/account/profile`).
- Delivery-2 gap: the edited-section check takes no lock, so an edit and a structuring/restructure
  could interleave.
- Notion flow 01: the AI draft and the provider's edits stay distinguishable forever; a signed note
  is never edited.

## 2. Decisions

| Topic | Decision |
|---|---|
| Editing | Per section, explicit save. Each save is one audit entry with the previous and new content. |
| Concurrency | `NoteSections."Revision" integer` (starts at 0, +1 on every write). A save carries the revision it started from; a mismatch is a 409. |
| Serialisation | Every note mutation (edit, blank sections, sign, restructure request, structuring write) first locks the note row with `SELECT … FOR UPDATE` inside its transaction, then checks its conditions. This closes the delivery-2 race. |
| Sections when the AI produced none | "Escrever manualmente" creates the format's empty sections (`GeneratedAt` = now, `Revision` 0), audited. Allowed only when the note has no sections and is not pending. |
| Signing | Requires `ReadyForReview`, structuring not pending, at least one section, and a CRP on the provider's profile. Sets `Status = Signed`, `SignedAt`, `SignedByCrp` ("{number}/{region}"). After signing no route changes the note. |
| Signing without CRP | 422 with a reason; the page explains and links to the profile. |
| Audit | Written by the application in the same transaction as the change, through one helper (`NoteAudit`). Actions: `ai_generated` (per section), `section_edited`, `sections_created`, `signed`. `ActorId` = provider id; null for `ai_generated`. Values: section content (edits/AI), CRP (signing). |
| Authorship in the UI | Unedited sections are the AI draft; edited sections show "Editado por você"; the header badge is "Rascunho gerado por IA", "Em revisão" or "Assinada". |
| Logs | Never section content; audit rows are data, not logs. |

## 3. Data

Migration `design/migrations/2026-10-13-note-review.sql` (idempotent, one transaction), mirrored in
`anamnys-db-script.sql`:

- `Notes."SignedByCrp" text` — set exactly when signed: check
  `("SignedAt" IS NULL) = ("SignedByCrp" IS NULL)`. Existing signed rows (seed data) would violate
  it, so the migration backfills `'—'` for any signed note without one before adding the check.
- `NoteSections."Revision" integer DEFAULT 0 NOT NULL`.

EF: `Note.SignedByCrp`, `NoteSection.Revision`; new `AuditEntry` entity over the existing table,
`DbSet<AuditEntry> AuditEntries`.

## 4. Server

New code in `anamnys-aspire.Server/Notes/`:

- **`NoteLocks.LockAsync(db, noteId, ct)`** — `SELECT 1 FROM "Notes" WHERE "Id" = … FOR UPDATE`.
- **`NoteAudit.Add(db, noteId, actorId, action, field, previous, next, at)`** — adds the entity to
  the context (saved with the change).
- **`NoteEditing`**:
  - `EditSectionAsync(db, providerId, noteId, key, content, expectedRevision, now, ct)` →
    `Saved(revision) | NotFound | Locked | Stale`. In a transaction: lock; note must be the
    provider's (`NotFound`), `ReadyForReview`, unsigned, not pending (`Locked`); section must exist
    with `Revision == expectedRevision` (`Stale`); set `Content` (trimmed), `EditedAt`,
    `Revision + 1`; audit `section_edited` (field = key, previous, new); touch `Notes.UpdatedAt`.
    Saving unchanged content is a no-op returning `Saved` without an audit row.
  - `CreateBlankSectionsAsync(db, providerId, noteId, now, ct)` → `Created | NotFound | Locked |
    AlreadyHasSections`. Creates the note format's keys in order; audit `sections_created`
    (new value = comma-separated keys).
  - `SignAsync(db, providerId, noteId, now, ct)` → `Signed | NotFound | Locked | NoSections |
    MissingCrp`. In a transaction: lock; checks; reads the provider's CRP; sets status, `SignedAt`,
    `SignedByCrp`; audit `signed` (new value = CRP).
- **Endpoints** (`/api/phi/providers/me/notes/{noteId}`):
  - `PUT sections/{key}` `{ content, expectedRevision }` → 200 `{ revision }` | 400 (content over
    20,000 chars) | 404 | 409 `{ message, reason: "locked" | "stale" }`.
  - `POST sections/blank` → 201 | 404 | 409.
  - `POST sign` → 200 | 404 | 409 (locked, no sections) | 422 (missing CRP).
  - All documented in `anamnys-aspire.Server.http`.
- **`NoteRestructure.RequestAsync`** and **`NoteStructuringJob.ProcessAsync`** take the note lock
  before their checks/writes; the structuring write adds one `ai_generated` audit row per section.
- **`GET` note** adds `signedAt`, `signedByCrp`, and per section `revision`; also
  `providerHasCrp` (bool) so the page can explain signing up front.

## 5. Provider app (note page)

- Each section block: "Editar" → textarea with "Salvar" / "Cancelar"; saving shows a spinner; a
  `stale` 409 shows "Esta seção foi alterada em outra janela. Recarregue a página."; `locked` shows
  "A nota está sendo organizada ou já foi assinada." Edited sections show "Editado por você".
- No sections and not pending: "Escrever manualmente" in the "Evolução" card.
- Side card "Assinatura" (above "Reorganizar"):
  - unsigned, no CRP: "Cadastre seu CRP no perfil para assinar." + link to `/account/profile`,
    button disabled;
  - unsigned, CRP present: "Assinar e travar nota" with a confirmation ("Depois de assinada, a nota
    não pode ser alterada.");
  - signed: "Assinada em {date} · CRP {crp}"; the page becomes read-only (no Editar, no
    Reorganizar, no Escrever manualmente).
- Header badge: AI draft / "Em revisão" (any section edited or created by hand) / "Assinada".
- Patient record "Notas recentes" already follows the status.

## 6. Failure handling

| Situation | Behaviour |
|---|---|
| Edit while structuring pending or after signing | 409 `locked`; nothing written. |
| Two windows editing the same section | Second save 409 `stale`; nothing written. |
| Sign without CRP | 422; nothing written. |
| Sign twice | Second call 409. |
| Restructure after any edit | Already refused (delivery 2), now under the lock. |
| Audit insert fails | The whole transaction rolls back; the change is not saved. |

## 7. Testing

- Migration shape vs bootstrap; `SignedByCrp` check.
- `EditSectionAsync`: saves content/EditedAt/Revision and one audit row; unchanged content writes no
  audit; locked when pending or signed; stale revision; another provider → NotFound.
- `CreateBlankSectionsAsync`: keys in format order, audit row; refuses when sections exist or pending.
- `SignAsync`: missing CRP; no sections; pending; success sets status/SignedAt/SignedByCrp + audit;
  after signing edit/blank/restructure all refuse.
- Structuring writes `ai_generated` audit rows (one per section).
- Endpoints: 401 without session; status mapping.
- Frontend: build and lint; manual checklist (edit, stale in two tabs, sign without/with CRP,
  read-only after signing).
