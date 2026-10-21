# Psychological Documents (F-05 delivery 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The provider writes declarações, atestados and relatórios through per-element forms, sees a live CFP 006/2019 conformity check, confirms prohibitions, signs (blocking by default, advisory optional), exports a PDF and corrects signed documents through linked versions.

**Architecture:** Rulesets for all six kinds are seeded from the deleted backend. `DocumentCatalog` (element origins, labels, prohibition texts) and `DocumentConformity` (pure check) drive `ClinicalDocumentService` (draft, save, sign, correct, list, get — locked, audited). `DocumentPdf` + export reuse the note PDF storage. The provider app gets a "Documentos" card, an editor route, a help panel, a Ajustes section and a profile field.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, PostgreSQL 18, QuestPDF, AWSSDK.S3 via `INoteFileStore`, xUnit v3 + FluentAssertions; React 19, TanStack Router/Query, Tailwind 4, i18next.

**Spec:** `design/specs/2026-10-19-psychological-documents-design.md`

## Global Constraints

- Delivery-1 kinds: `declaracao`, `atestado`, `relatorio`. Other kinds → `409 kind_not_available`. All six rulesets seeded.
- Ruleset seed verbatim from `git show 250b0442e0330d350dedcbe4844fce3dbddd346d:backend/db/02_seed_reference.sql`, lines 293–457 (the `insert into "ConformityRulesets" … on conflict ("DocumentKind","Version") do nothing;` block; do NOT copy the `delete … 'draft-1'` statement above it).
- Element origins, provider fields and prohibition handling exactly as spec §3 (tables). Confirmation texts verbatim from spec §3.
- A provider element is present when its trimmed text is non-empty. System elements: names/CRP missing when the provider has no CRP; `local_emissao` missing when `PracticeCity` is empty.
- Mode: `Providers.DocumentCheckMode` `blocking` (default) | `advisory`. Blocking refuses signing with missing elements; advisory signs with missing elements only when the request carries `acknowledgeMissing: true`. Confirmations always required. The check row stores the mode in force.
- Signed documents never change; correction = new draft copied from the signed one + original's `SupersededBy` = new id.
- Every document mutation (create, save, sign, correct, export) locks the document row and writes `AuditEntries` with `DocumentId` in the same transaction; actions `document_created`, `document_saved`, `document_signed`, `document_corrected`, `document_exported`, `document_link_issued`.
- Provider scoping on every query; no patient name in URLs, object keys or file names (`documents/{providerId}/{id}.pdf`, `documento-{CODE}.pdf`, CODE = first 8 hex of id, upper case). One-hour links.
- Errors: `409 { code, message }` codes `archived`, `locked`, `missing_elements` (+ `missing: string[]`), `confirmations_required` (+ `missing: string[]`), `kind_not_available`; `422 { code, message }` `missing_crp`, `missing_city`.
- UI strings pt-BR in `pt.json`. Tests: `dotnet run --project anamnys-aspire.Tests -- -class "*Name*"`, never `dotnet test`, Aspire stopped. Frontend: `npm run build`, `npm run lint`.
- C# style per CLAUDE.md; tests `<Method>_<Conditions>_<Outcome>` with Arrange/Act/Assert. Edit/Write tools only (no `sed -i`); preserve line endings.
- **Do not commit** (user rule).

---

### Task 1: Migration, ruleset seed and entities

**Files:** `design/migrations/2026-10-19-documents.sql` (new), `anamnys-db-script.sql`, entities `ClinicalDocument.cs`, `ConformityRuleset.cs`, `ConformityCheck.cs` (new), `Provider.cs` (+`PracticeCity`, `DocumentCheckMode`), `AnamnysDbContext.cs`; test `Data/DocumentsMigrationTests.cs`; append the migration to every older migration test chain that compares with the bootstrap shape.

**Produces:** `ClinicalDocuments` + `Fields jsonb DEFAULT '{}' NOT NULL`, `Confirmations jsonb DEFAULT '[]' NOT NULL`, `RulesetId uuid` (FK → `ConformityRulesets`, RESTRICT), `SignedByCrp text`, `PdfObjectKey text`, `PdfSha256 text`, `ExportedAt timestamptz`, `UpdatedAt timestamptz DEFAULT now() NOT NULL`; constraints `ClinicalDocuments_Signed_ck` (`SignedAt IS NULL` = `SignedByCrp IS NULL`), `ClinicalDocuments_Pdf_ck` (PDF columns all null or all set, and set only when `SignedAt` is not null). `Providers` + `PracticeCity text`, `DocumentCheckMode text DEFAULT 'blocking' NOT NULL` with `Providers_DocumentCheckMode_ck`. Entities map these (`Fields` as `string` holding JSON is acceptable if that matches how other jsonb columns are mapped; otherwise `Dictionary<string,string>` with `HasColumnType("jsonb")` — follow the context's existing jsonb convention, or Npgsql's dynamic JSON if none exists, and note the choice).

- [ ] Failing `DocumentsMigrationTests` (pattern: `ScalesMigrationTests`): shape equality old chain (through `2026-10-18-scales.sql`) + new file twice vs bootstrap + new file; six rulesets present with version `cfp-006-2019.1`; signed without `SignedByCrp` rejected by `ClinicalDocuments_Signed_ck`; bad mode rejected.
- [ ] Migration (house header, `BEGIN/COMMIT`, `IF NOT EXISTS`, `DROP/ADD CONSTRAINT`, seed verbatim) and bootstrap script (columns, constraints, seed appended at the end).
- [ ] Entities + DbSets.
- [ ] Apply to dev DB (header command; `aspire start`/`aspire stop` around it), run `-class "*MigrationTests*"` → PASS.

### Task 2: Catalog and conformity check (pure)

**Files:** `anamnys-aspire.Server/Documents/DocumentCatalog.cs`, `Documents/DocumentConformity.cs`; test `Documents/DocumentConformityTests.cs` (no fixture).

**Produces:**

```csharp
public enum ElementOrigin { System, Pdf, Provider }
public sealed record ElementInfo(string Code, ElementOrigin Origin, string Label, string? Hint);
public sealed record ProhibitionInfo(string Code, bool ByConstruction, string? ConfirmationText);
public static class DocumentCatalog
{
    public static readonly string[] AvailableKinds = ["declaracao", "atestado", "relatorio"];
    public static string KindTitle(string kind);                    // "Declaração", "Atestado psicológico", "Relatório psicológico", …
    public static ElementInfo Element(string kind, string code);    // origin/label/hint (hint names the article)
    public static ProhibitionInfo Prohibition(string code);
}
public sealed record ConformityInput(string Kind, IReadOnlyList<string> Elements, IReadOnlyList<string> Prohibitions,
    IReadOnlyDictionary<string, string> Fields, IReadOnlyCollection<string> Confirmations, bool ProviderHasCrp, bool HasPracticeCity);
public sealed record ConformityResult(IReadOnlyList<string> MissingElements, IReadOnlyList<string> MissingConfirmations)
{
    public bool ElementsComplete => MissingElements.Count == 0;
    public bool ConfirmationsComplete => MissingConfirmations.Count == 0;
}
public static class DocumentConformity { public static ConformityResult Check(ConformityInput input); }
```

Rules: provider elements missing when the trimmed field is empty or absent; `nome_psicologo`, `inscricao_crp`, `nome_autor_e_inscricao_crp` missing when `!ProviderHasCrp`; `local_emissao` missing when `!HasPracticeCity`; other system/pdf elements never missing. Missing confirmations: every prohibition with `ByConstruction == false` not in `Confirmations` (by code). Unknown element code → treated as `Provider` with its code as label (defensive; covered by a test).

- [ ] Tests per kind (declaração, atestado, relatório): complete input passes; each provider element alone missing is reported; CRP/city absence reports the right system elements; each confirmation required; atestado `quebra_de_paragrafo_sem_preenchimento` never required; labels/hints exist for every element of the three seeded rulesets (load the element lists by copying them from the seed into the test as constants).
- [ ] Implement; run `-class "*DocumentConformityTests*"` → PASS.

### Task 3: Document service

**Files:** `Documents/ClinicalDocumentService.cs` (new); tests `Documents/ClinicalDocumentServiceTests.cs`.

**Produces** (`Fields` as `IReadOnlyDictionary<string,string>`):

```csharp
public sealed record DocumentSummary(Guid Id, string Kind, string Title, string Status, DateTimeOffset CreatedAt, DateTimeOffset? SignedAt, Guid? SupersededBy);
// Status: "draft" | "signed" | "superseded"
public sealed record DocumentDetail(Guid Id, Guid PatientId, string PatientName, string Kind, string Title, string Status,
    IReadOnlyDictionary<string, string> Fields, IReadOnlyList<string> Confirmations, ConformityResult Check,
    string Mode, DateTimeOffset CreatedAt, DateTimeOffset? SignedAt, string? SignedByCrp, Guid? SupersededBy, DateTimeOffset? ExportedAt,
    bool ProviderHasCrp, bool HasPracticeCity);
public enum DocumentOutcome { Ok, NotFound, Archived, Locked, KindNotAvailable, MissingElements, ConfirmationsRequired, MissingCrp, MissingCity, Invalid }
public sealed record DocumentResult(DocumentOutcome Outcome, Guid? Id = null, IReadOnlyList<string>? Missing = null);
public static class ClinicalDocumentService
{
    Task<DocumentResult> CreateAsync(db, providerId, patientId, string kind, Guid? noteId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<DocumentSummary>?> ListAsync(db, providerId, patientId, ct);
    Task<DocumentDetail?> GetAsync(db, providerId, documentId, ct);
    Task<DocumentResult> SaveAsync(db, providerId, documentId, IReadOnlyDictionary<string,string> fields, IReadOnlyCollection<string> confirmations, DateTimeOffset now, ct);
    Task<DocumentResult> SignAsync(db, providerId, documentId, bool acknowledgeMissing, DateTimeOffset now, ct);
    Task<DocumentResult> CorrectAsync(db, providerId, documentId, DateTimeOffset now, ct);
}
```

Behaviour: create — kind in `AvailableKinds` else `KindNotAvailable`; patient of provider else `NotFound`; archived → `Archived`; `noteId` optional, must be the provider's note of the same patient else `NotFound`; `RulesetId` = latest ruleset of the kind by `EffectiveFrom`. Save — only fields whose code is a provider element of the ruleset are kept (others dropped), values trimmed, max 20 000 chars each (longer → `Invalid` with the offending codes in `Missing`; never truncate); confirmations kept only for prohibitions of the ruleset; signed → `Locked`. Sign — signed → `Locked`; no CRP → `MissingCrp`; no city → `MissingCity`; confirmations missing → `ConfirmationsRequired` (+list); elements missing and (mode blocking or !acknowledgeMissing) → `MissingElements` (+list); else assemble `Body` (titles + fields in ruleset order, prazo de validade last; atestado text joined without line breaks), set `SignedAt`, `SignedByCrp` = "{number}/{region}", insert `ConformityChecks` (mode, missing elements, `ForbiddenFound` = [], `Passed` = elements complete), audit `document_signed`. Correct — only signed and not superseded, else `Locked`; new draft copying kind, ruleset, fields, confirmations (cleared — must be confirmed again), note; set original `SupersededBy`; audit both. Every mutation in a transaction with `SELECT … FOR UPDATE` on the document row (create audits after saving the row).

- [ ] Tests (real DB, `PatientSearchSeed`, set CRP and city on the seed provider with SQL): create each available kind; laudo → KindNotAvailable; archived → Archived; other provider's patient → NotFound; save filters unknown codes and keeps provider fields; save after sign → Locked; sign blocking with missing → MissingElements with list; sign advisory with missing and `acknowledgeMissing` → Ok and check row `Passed = false`, `Mode = advisory`; confirmations missing → ConfirmationsRequired; no city → MissingCity; no CRP → MissingCrp; complete → Ok, `SignedByCrp`, `Body` order with prazo last, atestado body without line breaks; correction chain (original unchanged, `SupersededBy` set, new draft with fields, no confirmations); audit actions present; list statuses.
- [ ] Implement; run `-class "*ClinicalDocumentServiceTests*"` → PASS.

### Task 4: PDF and export

**Files:** `Documents/DocumentPdf.cs`, `Documents/DocumentExport.cs`; tests `Documents/DocumentPdfTests.cs`, `Documents/DocumentExportTests.cs`.

- `DocumentPdf.Render(DocumentPdfModel)` (QuestPDF): title (kind title, upper/emphasised); identification block (patient name; for relatório also author and CRP); body sections in ruleset order with the element labels as headings (declaração and atestado as continuous paragraphs without headings; atestado without line breaks); prazo de validade as the last paragraph; local and date line ("{PracticeCity}, {dd 'de' MMMM 'de' yyyy}", pt-BR culture); signature block ("Assinado eletronicamente no Anamnys por {name}, CRP {crp}, em {dd/MM/yyyy HH:mm}"); footer on every page: "{name} · CRP {crp}" left, "Página X de Y" right. Times in `America/Sao_Paulo`, pt-BR culture.
- `DocumentExport.ExportAsync(db, store, providerId, documentId, now, ct)` mirrors `NoteExport` (signed only else `NotSigned`; first export stores `documents/{providerId}/{id}.pdf`, SHA-256, `ExportedAt`, audit `document_exported`; later only presign + `document_link_issued`; store failure → `StorageUnavailable`, nothing written; caller-cancellation semantics as `NoteExport`).
- [ ] Tests: model/render bytes start `%PDF`; render the three kinds once each without exceptions; export first/second/unsigned/other provider/store failure with `FakeNoteFileStore`.
- [ ] Run → PASS.

### Task 5: Routes, profile and settings

**Files:** `Documents/DocumentEndpoints.cs` (new), `Program.cs`, `Profile/ProfileContracts.cs` + profile endpoints (`practiceCity`), a `document-settings` route pair (in `DocumentEndpoints`), `anamnys-aspire.Server.http`; tests `Documents/DocumentEndpointTests.cs`.

Routes (401 first): `GET providers/me/document-kinds` → `[{ kind, title, rulesetVersion, elements: [{ code, origin, label, hint }], prohibitions: [{ code, byConstruction, confirmationText }] }]` (available kinds only); `GET|POST providers/me/patients/{id}/documents` (`POST { kind, noteId? }` → 201 `{ id }`); `GET providers/me/documents/{id}` → `DocumentDetail`; `PUT providers/me/documents/{id}` `{ fields, confirmations }` → 200 `DocumentDetail`; `POST …/{id}/sign` `{ acknowledgeMissing }` → 200; `POST …/{id}/correct` → 201 `{ id }`; `POST …/{id}/export` → 200 `{ url, expiresAt }` (409 `not_signed`, 503 `storage_unavailable` as the note export); `GET|PUT providers/me/document-settings` `{ documentCheckMode }` (400 on other values). Outcome → HTTP per Global Constraints; `Invalid` → `ValidationProblem` keyed by field code. Profile: `practiceCity` (trimmed, max 120, optional) in request and response.

- [ ] Endpoint tests: 401s; logged-in `document-kinds` returns the three kinds; settings round-trip for the dev provider (restore the original value at the end).
- [ ] Run `-class "*DocumentEndpointTests*"`, `-class "*Profile*"` → PASS.

### Task 6: Provider app

**Files:** `packages/shared/src/lib/types.ts`, `packages/shared/src/api/documents.ts` (new), `pt.json`; `apps/provider/src/components/documents/{DocumentsCard,NewDocumentModal,DocumentHelpPanel,ConformityPanel}.tsx` (new); route `apps/provider/src/routes/_app/documents/$documentId.tsx` (new); `routes/_app/patients/$patientId/index.tsx` (card in the full-width area under "Escalas"); `components/settings/DocumentSettingsSection.tsx` (new) + settings page; profile page field "Cidade de atendimento".

- Card "Documentos": list (title, status badge rascunho/assinado/substituído, created/signed date), click opens the editor; "Novo documento" (hidden when archived) → modal choosing one of the three kinds → creates and navigates.
- Editor: header (title, patient, status); left column the form — system values read-only (provider name, CRP or "Cadastre seu CRP no perfil" link, patient, city or "Cadastre a cidade de atendimento" link), provider fields as textareas with label + hint, "Prazo de validade" last; "Salvar" (PUT) refreshes the check; right column `ConformityPanel`: ✓/✗ per element (all origins, with origin hint "preenchido pelo sistema" / "gerado no PDF"), confirmation checkboxes with the spec texts, "Assinar" — blocking: disabled until elements and confirmations complete; advisory: enabled once confirmations complete, and when elements are missing opens a confirm dialog ("Assinar com elementos faltando? A conferência registra que você assinou ciente.") that sends `acknowledgeMissing: true`. Signed: read-only, "Exportar PDF"/"Baixar PDF" (same behaviour and hint text as the note), "Corrigir" (creates and navigates); superseded: banner linking to the replacement. "?" opens `DocumentHelpPanel` (plain pt-BR: form by element; system-filled data; the panel; confirmations; blocking vs advisory; correction keeps the original; delivery-2 kinds "em breve").
- Ajustes: section "Conferência de documentos" with two radio options ("Bloquear a assinatura se faltar elemento obrigatório" — default; "Apenas avisar").
- Profile: optional "Cidade de atendimento".
- [ ] Build + lint pass.

### Task 7: Documentation and verification

- [ ] `documentation/22-psychological-documents.md` (English, house style): kinds, element origins table, prohibitions and confirmations, modes, correction, what the old seed left out on purpose (summarise its comments, lines 241–291), delivery 2 required and why; index (Part 9) entry.
- [ ] Gaps (`## Notas clínicas` or a new `## Documentos`): delivery 2 required; delivery protocol (art. 16, F-07); retention (F-06); `ForbiddenFound` always empty (prohibitions are confirmations, not detection); manual browser check pending.
- [ ] Status file: F-05 delivery 1 done, delivery 2 next.
- [ ] Full suite (Aspire stopped), build, lint; spec status → Implemented.
