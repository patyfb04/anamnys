# Psychological Documents, delivery 2 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable laudo, parecer and relatório multiprofissional (Res. CFP 006/2019 arts. 12–14) in the existing document flow, closing F-05.

**Architecture:** Delivery 1 already has the catalog, pure conformity check, service, endpoints, PDF and editor; the three rulesets are seeded. This delivery maps the new elements' origins, makes confirmation texts kind-aware, adds a `Team` jsonb column for the multiprofissional co-authors, lays out numbered items in the PDF and adds a team block to the editor. No new routes.

**Tech Stack:** .NET 10 minimal API, EF Core 10 + Npgsql (jsonb via value conversion), PostgreSQL 18, QuestPDF 2026.9.1, React 19 + TanStack Query, Tailwind 4, i18next (`pt.json`).

**Spec:** `design/specs/2026-10-20-psychological-documents-delivery-2-design.md` (builds on `design/specs/2026-10-19-psychological-documents-design.md`).

## Global Constraints

- Branch `feature/psychological-documents`. **Never commit, stage, stash or switch branches** — the user reviews first.
- Tests: `dotnet run --project anamnys-aspire.Tests -- -class "*X*"` (never `dotnet test`). Classes using `SharedAppHostCollection` start their own AppHost: only run them when the controller says Aspire is stopped; otherwise compile and report "not run".
- If the user's app is running, build the server with `--artifacts-path "$TEMP/anamnys-artifacts"`.
- Edit/Write tools only (no `sed -i`, it strips CRLF); keep each file's line endings.
- Provider scoping on every query; audit every mutation through `DocumentAudit` in the same transaction; no PHI in URLs or logs.
- Team limits: at most 10 members; name ≤ 200, category ≤ 100, registry ≤ 50, analysis ≤ 20 000 (`ClinicalDocumentService.MaxFieldLength`).
- Delivery-1 confirmation texts stay byte for byte.
- C# style: file-scoped namespaces, records for requests, `CancellationToken` always passed, test names `<Method>_<Conditions>_<Outcome>` with `// Arrange // Act // Assert`.
- Frontend: 2-space indent, all copy in `packages/shared/src/lib/i18n/locales/pt.json`, `npm run build` and `npm run lint` (from `anamnys-aspire/`) pass.

---

### Task 1: Catalog origins, kind-aware confirmations and team conformity (pure)

**Files:**
- Create: `anamnys-aspire.Server/Data/Entities/TeamMember.cs`
- Modify: `anamnys-aspire.Server/Documents/DocumentCatalog.cs`, `anamnys-aspire.Server/Documents/DocumentConformity.cs`, `anamnys-aspire.Server/Documents/DocumentEndpoints.cs` (the `document-kinds` projection only)
- Test: `anamnys-aspire.Tests/Documents/DocumentConformityTests.cs`

**Interfaces:**
- Produces: `record TeamMember(string Name, string Category, string Registry, string Analysis)` in `Anamnys.Server.Data.Entities`; `DocumentCatalog.AvailableKinds` = all six kinds; `DocumentCatalog.Prohibition(string kind, string code)` (the one-argument overload is removed); `ConformityInput(..., bool HasPracticeCity, IReadOnlyList<TeamMember>? Team = null)`.

- [ ] **Step 1: Write failing tests** in `DocumentConformityTests` (pure, no AppHost). Add element lists copied verbatim from `design/migrations/2026-10-19-documents.sql`:

```csharp
private static readonly string[] LaudoElements =
[
    "secoes_em_forma_de_itens", "identificacao", "titulo_laudo_psicologico", "nome_pessoa_ou_instituicao_atendida",
    "nome_solicitante", "finalidade", "nome_autor_e_inscricao_crp", "descricao_demanda", "procedimento",
    "referencial_teorico_metodologico", "pessoas_ouvidas_numero_de_encontros_e_duracao", "analise", "conclusao",
    "local_emissao", "data_emissao", "carimbo", "nome_psicologo", "inscricao_crp", "laudas_numeradas_rubricadas",
    "assinatura", "referencias", "prazo_validade",
];

private static readonly string[] ParecerElements =
[
    "secoes_em_forma_de_itens", "identificacao", "titulo_parecer_psicologico",
    "nome_pessoa_ou_instituicao_objeto_do_questionamento", "nome_solicitante", "finalidade",
    "nome_autor_inscricao_crp_e_titulacao", "descricao_demanda", "analise", "conclusao", "local_emissao",
    "data_emissao", "carimbo", "nome_psicologo", "inscricao_crp", "laudas_numeradas_rubricadas", "assinatura",
    "referencias", "prazo_validade",
];

private static readonly string[] MultiElements =
[
    "identificacao", "titulo_relatorio_multiprofissional", "nome_pessoa_ou_instituicao_atendida", "nome_solicitante",
    "finalidade", "nomes_autores_categorias_e_registros_profissionais", "descricao_demanda", "procedimento",
    "procedimentos_privativos_da_psicologia_em_secao_separada", "analise",
    "analise_identificada_por_profissional_e_categoria", "conclusao", "local_emissao", "data_emissao", "carimbo",
    "nomes_profissionais", "inscricoes_profissionais", "laudas_numeradas_rubricadas", "assinatura_do_psicologo",
    "prazo_validade",
];
```

Add the three kinds to `ElementsByKind`, `ProhibitionsByKind` (laudo: `["descricao_literal_das_sessoes", "afirmacao_sem_fonte_ou_sustentacao"]`; parecer: `["resultado_de_avaliacao_ou_intervencao_psicologica"]`; multi: `["afirmacao_sem_fonte_ou_sustentacao", "descricao_literal_das_sessoes"]`) and `KindArticle` (`"Art. 13"`, `"Art. 14"`, `"Art. 12"`), so existing theory-style tests that iterate these dictionaries cover the new kinds. Then add:

```csharp
private static readonly TeamMember FullMember = new("Rita Lima", "Psiquiatria", "CRM 12345", "Análise da psiquiatria.");

private static ConformityResult CheckFull(string kind, IReadOnlyList<TeamMember>? team, bool crp = true, Dictionary<string, string>? fields = null)
{
    var elements = ElementsByKind[kind];
    var allFields = fields ?? elements
        .Where(code => DocumentCatalog.Element(kind, code).Origin == ElementOrigin.Provider)
        .ToDictionary(code => code, _ => "texto");
    return DocumentConformity.Check(new ConformityInput(
        kind, elements, ProhibitionsByKind[kind], allFields, ProhibitionsByKind[kind], crp, true, team));
}

[Theory]
[InlineData("laudo")]
[InlineData("parecer")]
[InlineData("relatorio_multiprofissional")]
public void Check_NewKindComplete_ReportsNothingMissing(string kind)
{
    // Arrange
    IReadOnlyList<TeamMember>? team = kind == "relatorio_multiprofissional" ? [FullMember] : null;

    // Act
    var result = CheckFull(kind, team);

    // Assert
    result.MissingElements.Should().BeEmpty();
    result.MissingConfirmations.Should().BeEmpty();
}

[Fact]
public void Check_ParecerWithoutCrp_ReportsTitulacaoMissing()
{
    // Act
    var result = CheckFull("parecer", null, crp: false);

    // Assert
    result.MissingElements.Should().Contain("nome_autor_inscricao_crp_e_titulacao");
}

[Fact]
public void Check_ParecerSubject_IsSystemAndNeverMissing()
{
    // Act
    var info = DocumentCatalog.Element("parecer", "nome_pessoa_ou_instituicao_objeto_do_questionamento");

    // Assert
    info.Origin.Should().Be(ElementOrigin.System);
}

[Fact]
public void Check_MultiEmptyTeam_ReportsAuthorsAndAnalysisMissing()
{
    // Act
    var result = CheckFull("relatorio_multiprofissional", []);

    // Assert
    result.MissingElements.Should().BeEquivalentTo([
        "nomes_autores_categorias_e_registros_profissionais", "nomes_profissionais", "inscricoes_profissionais",
        "analise_identificada_por_profissional_e_categoria",
    ]);
}

[Fact]
public void Check_MultiMemberWithoutRegistry_ReportsOnlyAuthorElements()
{
    // Act
    var result = CheckFull("relatorio_multiprofissional", [FullMember with { Registry = " " }]);

    // Assert
    result.MissingElements.Should().BeEquivalentTo([
        "nomes_autores_categorias_e_registros_profissionais", "nomes_profissionais", "inscricoes_profissionais",
    ]);
}

[Fact]
public void Check_MultiMemberWithoutAnalysis_ReportsOnlyIdentifiedAnalysis()
{
    // Act
    var result = CheckFull("relatorio_multiprofissional", [FullMember, FullMember with { Analysis = "" }]);

    // Assert
    result.MissingElements.Should().Equal("analise_identificada_por_profissional_e_categoria");
}

[Fact]
public void Check_MultiWithoutCrp_ReportsAuthorElements()
{
    // Act
    var result = CheckFull("relatorio_multiprofissional", [FullMember], crp: false);

    // Assert
    result.MissingElements.Should().Contain(["nomes_profissionais", "inscricoes_profissionais"]);
}

[Theory]
[InlineData("relatorio", "afirmacao_sem_fonte_ou_sustentacao", "Confirmo que toda afirmação do relatório tem fonte ou sustentação técnica.")]
[InlineData("relatorio", "descricao_literal_das_sessoes", "Confirmo que o relatório não transcreve literalmente as sessões.")]
[InlineData("laudo", "afirmacao_sem_fonte_ou_sustentacao", "Confirmo que toda afirmação do laudo tem fonte ou sustentação técnica.")]
[InlineData("laudo", "descricao_literal_das_sessoes", "Confirmo que o laudo não transcreve literalmente as sessões.")]
[InlineData("relatorio_multiprofissional", "afirmacao_sem_fonte_ou_sustentacao", "Confirmo que toda afirmação do relatório multiprofissional tem fonte ou sustentação técnica.")]
[InlineData("relatorio_multiprofissional", "descricao_literal_das_sessoes", "Confirmo que o relatório multiprofissional não transcreve literalmente as sessões.")]
[InlineData("parecer", "resultado_de_avaliacao_ou_intervencao_psicologica", "Confirmo que este parecer não resulta de processo de avaliação ou intervenção psicológica.")]
[InlineData("declaracao", "sintomas_situacoes_ou_estados_psicologicos", "Confirmo que esta declaração não descreve sintomas, situações ou estados psicológicos.")]
[InlineData("atestado", "informacao_alem_do_solicitado", "Confirmo que o atestado se restringe à informação solicitada.")]
public void Prohibition_KindAndCode_ReturnsKindSpecificText(string kind, string code, string expected)
{
    // Act
    var info = DocumentCatalog.Prohibition(kind, code);

    // Assert
    info.ConfirmationText.Should().Be(expected);
}
```

Replace any existing test asserting that laudo/parecer/multi are not available with `DocumentCatalog.AvailableKinds.Should().Equal("declaracao", "atestado", "relatorio", "relatorio_multiprofissional", "laudo", "parecer")`.

- [ ] **Step 2: Run, expect compile failure / FAIL.** `dotnet run --project anamnys-aspire.Tests -- -class "*DocumentConformityTests*"`

- [ ] **Step 3: Implement.**

`Data/Entities/TeamMember.cs`:

```csharp
namespace Anamnys.Server.Data.Entities;

// A co-author of a relatório multiprofissional (Res. CFP 006/2019 art. 12). Not an Anamnys user.
public sealed record TeamMember(string Name, string Category, string Registry, string Analysis);
```

`DocumentCatalog.cs`:
- `AvailableKinds = ["declaracao", "atestado", "relatorio", "relatorio_multiprofissional", "laudo", "parecer"];`
- Replace these `Entries` (labels/explanations verbatim):

```csharp
["nome_pessoa_ou_instituicao_objeto_do_questionamento"] = Sys("Pessoa objeto do questionamento", PatientNameText),
["nome_autor_inscricao_crp_e_titulacao"] = Prov("Titulação da(o) autora(or)",
    "Titulação que comprove conhecimento específico no assunto; nome e CRP vêm do perfil."),
["nomes_autores_categorias_e_registros_profissionais"] = Sys("Autoras(es), categorias e registros profissionais",
    "Você e a equipe informada no documento, com categoria e registro."),
["procedimentos_privativos_da_psicologia_em_secao_separada"] = Prov("Procedimentos privativos da Psicologia",
    "Procedimentos exclusivos da Psicologia, em seção separada."),
["analise_identificada_por_profissional_e_categoria"] = Sys("Análise por profissional e categoria",
    "Cada profissional da equipe tem sua análise, identificada por nome e categoria."),
["nomes_profissionais"] = Sys("Nomes das(os) profissionais", "Você e a equipe informada no documento."),
["inscricoes_profissionais"] = Sys("Inscrições profissionais", "Seu CRP e os registros da equipe."),
["referencias"] = Prov("Referências", "Referências obrigatórias, uma por linha."),
["referencial_teorico_metodologico"] = Prov("Referencial teórico-metodológico", "Impresso dentro do item Procedimento."),
["secoes_em_forma_de_itens"] = Pdf("Seções em forma de itens", "O PDF organiza o documento em itens numerados."),
```

- Kind-aware prohibitions: keep the `Prohibitions` dictionary for the kind-independent texts and add

```csharp
private static readonly Dictionary<string, string> ReportNouns = new()
{
    ["relatorio"] = "relatório",
    ["laudo"] = "laudo",
    ["relatorio_multiprofissional"] = "relatório multiprofissional",
};

// Unknown codes require an explicit confirmation without text.
public static ProhibitionInfo Prohibition(string kind, string code)
{
    var noun = ReportNouns.GetValueOrDefault(kind, "relatório");
    return code switch
    {
        "afirmacao_sem_fonte_ou_sustentacao" => new(code, false, $"Confirmo que toda afirmação do {noun} tem fonte ou sustentação técnica."),
        "descricao_literal_das_sessoes" => new(code, false, $"Confirmo que o {noun} não transcreve literalmente as sessões."),
        _ => Prohibitions.TryGetValue(code, out var info) ? info : new ProhibitionInfo(code, false, null),
    };
}
```

and remove those two codes from the `Prohibitions` array. Delete the one-argument `Prohibition(code)`.

`DocumentConformity.cs`:

```csharp
public sealed record ConformityInput(
    string Kind,
    IReadOnlyList<string> Elements,
    IReadOnlyList<string> Prohibitions,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyCollection<string> Confirmations,
    bool ProviderHasCrp,
    bool HasPracticeCity,
    IReadOnlyList<TeamMember>? Team = null);
```

In `Check`, use `DocumentCatalog.Prohibition(input.Kind, code)`. In `IsMissing`:

```csharp
private static readonly HashSet<string> TeamAuthorElements =
    ["nomes_autores_categorias_e_registros_profissionais", "nomes_profissionais", "inscricoes_profissionais"];

private static bool IsMissing(ConformityInput input, string code)
{
    var info = DocumentCatalog.Element(input.Kind, code);
    var team = input.Team ?? [];
    return info.Origin switch
    {
        ElementOrigin.Provider when code == "nome_autor_inscricao_crp_e_titulacao" => !input.ProviderHasCrp || Blank(input, code),
        ElementOrigin.Provider => Blank(input, code),
        ElementOrigin.System when CrpElements.Contains(code) => !input.ProviderHasCrp,
        ElementOrigin.System when code == "local_emissao" => !input.HasPracticeCity,
        ElementOrigin.System when TeamAuthorElements.Contains(code) => !input.ProviderHasCrp || !TeamIdentified(team),
        ElementOrigin.System when code == "analise_identificada_por_profissional_e_categoria" =>
            team.Count == 0 || team.Any(m => string.IsNullOrWhiteSpace(m.Analysis)),
        _ => false,
    };
}

private static bool Blank(ConformityInput input, string code) =>
    !input.Fields.TryGetValue(code, out var text) || string.IsNullOrWhiteSpace(text);

private static bool TeamIdentified(IReadOnlyList<TeamMember> team) =>
    team.Count > 0 && team.All(m =>
        !string.IsNullOrWhiteSpace(m.Name) && !string.IsNullOrWhiteSpace(m.Category) && !string.IsNullOrWhiteSpace(m.Registry));
```

`DocumentEndpoints.cs` `document-kinds`: `prohibitions = r.Prohibitions.Select(code => DocumentCatalog.Prohibition(r.DocumentKind, code))`.

- [ ] **Step 4: Run** `-class "*DocumentConformityTests*"` and `-class "*DocumentBodyTests*"` — PASS. Build the server (0 errors).

---

### Task 2: `Team` column, entity mapping, migration

**Files:**
- Create: `design/migrations/2026-10-20-documents-team.sql`, `anamnys-aspire.Tests/Data/DocumentsTeamMigrationTests.cs`
- Modify: `anamnys-db-script.sql` (`CREATE TABLE "ClinicalDocuments"` and its constraints), `anamnys-aspire.Server/Data/Entities/ClinicalDocument.cs`, `anamnys-aspire.Server/Data/AnamnysDbContext.cs`, every migration test in `anamnys-aspire.Tests/Data/` whose shape comparison runs against `anamnys-db-script.sql` (see Step 4), `anamnys-aspire.Tests/Documents/DocumentJsonbRoundTripTests.cs`

**Interfaces:**
- Consumes: `TeamMember` (Task 1).
- Produces: `ClinicalDocument.Team` (`List<TeamMember>`, default `[]`), column `"Team" jsonb DEFAULT '[]' NOT NULL`, constraint `"ClinicalDocuments_Team_array"`.

- [ ] **Step 1: Migration** `design/migrations/2026-10-20-documents-team.sql`:

```sql
-- F-05 delivery 2: co-authors of the relatório multiprofissional
-- (design/specs/2026-10-20-psychological-documents-delivery-2-design.md, section 4).
-- Idempotent; all or nothing.
BEGIN;

ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "Team" jsonb DEFAULT '[]'::jsonb NOT NULL;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ClinicalDocuments_Team_array') THEN
    ALTER TABLE "ClinicalDocuments"
      ADD CONSTRAINT "ClinicalDocuments_Team_array" CHECK (jsonb_typeof("Team") = 'array');
  END IF;
END $$;

COMMIT;
```

`anamnys-db-script.sql`: add `"Team" jsonb DEFAULT '[]'::jsonb NOT NULL,` to `ClinicalDocuments` next to `"Confirmations"`, and `CONSTRAINT "ClinicalDocuments_Team_array" CHECK (jsonb_typeof("Team") = 'array')` with that table's other constraints (match how delivery 1 added its constraints in the same file).

- [ ] **Step 2: Entity and mapping.** `ClinicalDocument`: `public List<TeamMember> Team { get; set; } = [];` after `Confirmations`. In `AnamnysDbContext` inside the `ClinicalDocument` block:

```csharp
e.Property(x => x.Team).HasColumnType("jsonb").HasConversion(
    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
    v => JsonSerializer.Deserialize<List<TeamMember>>(v, (JsonSerializerOptions?)null) ?? new(),
    new ValueComparer<List<TeamMember>>(
        (a, b) => a != null && b != null && a.SequenceEqual(b),
        v => v.Aggregate(0, (h, m) => HashCode.Combine(h, m)),
        v => v.ToList()));
```

- [ ] **Step 3: Tests.** `DocumentsTeamMigrationTests` mirrors `DocumentsMigrationTests` (same `[Collection(SharedAppHostCollection.Name)]`, `ScratchDatabase`, `RunMigrationAsync`, `ShapeAsync` helpers — copy them; previous migrations = the list in `DocumentsMigrationTests.RunPreviousMigrationsAsync` plus `2026-10-19-documents.sql`):
  - `TeamMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree` — run previous + this migration twice on one scratch DB, this migration once on the bootstrap script; shapes equal and contain `ClinicalDocuments.Team`.
  - `TeamMigration_NonArrayTeam_IsRejected` — insert a document with `"Team"` = `'{}'::jsonb` → `PostgresException` with `SqlState` `23514`.
  - In `DocumentJsonbRoundTripTests` add `Team_SavedAndReloaded_KeepsMembersInOrder` (two members, reload in a new context, `Should().Equal`).

- [ ] **Step 4: Existing migration tests.** Every test in `anamnys-aspire.Tests/Data/*MigrationTests.cs` that compares a migrated database against `anamnys-db-script.sql` must now also apply `2026-10-20-documents-team.sql` (delivery 1 did the same for `2026-10-19-documents.sql`: see `git show 98ce0e6 -- anamnys-aspire/anamnys-aspire.Tests/Data`). Add it in the same place.

- [ ] **Step 5: Run** (Aspire stopped only) `-class "*MigrationTests*"` and `-class "*DocumentJsonbRoundTripTests*"` — PASS. Otherwise build and report "not run".

---

### Task 3: Body sections and PDF layout for the new kinds (pure)

**Files:**
- Create: `anamnys-aspire.Server/Documents/DocumentSections.cs`
- Modify: `anamnys-aspire.Server/Documents/DocumentBody.cs`, `DocumentPdfModel.cs`, `DocumentPdf.cs`
- Test: `anamnys-aspire.Tests/Documents/DocumentBodyTests.cs`, `DocumentPdfTests.cs`

**Interfaces:**
- Consumes: `TeamMember`, `ClinicalDocument.Team`, `DocumentCatalog.Element`.
- Produces:
  - `DocumentSections.Build(string kind, IReadOnlyList<string> elements, IReadOnlyDictionary<string, string> fields, IReadOnlyList<TeamMember> team, string authorName) : IReadOnlyList<DocumentPdfSection>` — content sections after the identification, prazo de validade last.
  - `DocumentBody.Assemble(string kind, IReadOnlyList<string> elements, IReadOnlyDictionary<string, string> fields, IReadOnlyList<TeamMember> team, string authorName)`; the existing 3-argument `Assemble` stays and calls it with `[]` and `""`.
  - `record DocumentPdfLine(string Label, string Value)`; `DocumentPdfModel` replaces `string? Author` with `string? IdentificationHeading, IReadOnlyList<DocumentPdfLine> Identification`.

- [ ] **Step 1: Failing tests** (`DocumentBodyTests`, using the element lists from Task 1 — copy them in):

```csharp
[Fact]
public void Build_Laudo_NumbersItemsAndKeepsSubParagraphsInsideProcedimento()
{
    // Arrange
    var fields = new Dictionary<string, string>
    {
        ["nome_solicitante"] = "Juízo", ["finalidade"] = "Instruir processo", ["descricao_demanda"] = "Demanda",
        ["procedimento"] = "Entrevistas", ["referencial_teorico_metodologico"] = "TCC",
        ["pessoas_ouvidas_numero_de_encontros_e_duracao"] = "Mãe, 3 encontros", ["analise"] = "Análise",
        ["conclusao"] = "Conclusão", ["referencias"] = "Ref A\nRef B", ["prazo_validade"] = "90 dias",
    };

    // Act
    var sections = DocumentSections.Build("laudo", LaudoElements, fields, [], "Dra. Paula");

    // Assert
    sections.Select(s => s.Heading).Should().Equal(
        "2. Descrição da demanda", "3. Procedimento", "Referencial teórico-metodológico",
        "Pessoas ouvidas, número de encontros e duração", "4. Análise", "5. Conclusão", "6. Referências",
        "Prazo de validade");
    sections.Single(s => s.Heading == "6. Referências").Text.Should().Be("Ref A\nRef B");
}

[Fact]
public void Build_Parecer_NumbersFiveItemsWithoutProcedimento()
{
    // Arrange
    var fields = new Dictionary<string, string>
    {
        ["descricao_demanda"] = "D", ["analise"] = "A", ["conclusao"] = "C", ["referencias"] = "R",
        ["prazo_validade"] = "30 dias", ["nome_solicitante"] = "S", ["finalidade"] = "F",
        ["nome_autor_inscricao_crp_e_titulacao"] = "Especialista em Psicologia Jurídica",
    };

    // Act
    var sections = DocumentSections.Build("parecer", ParecerElements, fields, [], "Dra. Paula");

    // Assert
    sections.Select(s => s.Heading).Should().Equal(
        "2. Descrição da demanda", "3. Análise", "4. Conclusão", "5. Referências", "Prazo de validade");
}

[Fact]
public void Build_LaudoMissingProcedimento_NumbersPrintedItemsConsecutively()
{
    // Arrange
    var fields = new Dictionary<string, string> { ["descricao_demanda"] = "D", ["analise"] = "A", ["prazo_validade"] = "1 ano" };

    // Act
    var sections = DocumentSections.Build("laudo", LaudoElements, fields, [], "Dra. Paula");

    // Assert
    sections.Select(s => s.Heading).Should().Equal("2. Descrição da demanda", "3. Análise", "Prazo de validade");
}

[Fact]
public void Build_Multi_HeadsEachAnalysisWithProfessionalAndCategory()
{
    // Arrange
    var fields = new Dictionary<string, string>
    {
        ["descricao_demanda"] = "D", ["procedimento"] = "P",
        ["procedimentos_privativos_da_psicologia_em_secao_separada"] = "Testes", ["analise"] = "Minha análise",
        ["conclusao"] = "C", ["prazo_validade"] = "6 meses",
    };
    TeamMember[] team = [new("Rita Lima", "Psiquiatria", "CRM 1", "Análise da Rita")];

    // Act
    var sections = DocumentSections.Build("relatorio_multiprofissional", MultiElements, fields, team, "Dra. Paula");

    // Assert
    sections.Select(s => s.Heading).Should().Equal(
        "Descrição da demanda", "Procedimento", "Procedimentos privativos da Psicologia",
        "Análise — Dra. Paula (Psicologia)", "Análise — Rita Lima (Psiquiatria)", "Conclusão", "Prazo de validade");
}

[Fact]
public void Assemble_Laudo_JoinsSectionsAsHeadedBlocks()
{
    // Arrange
    var fields = new Dictionary<string, string> { ["analise"] = "A", ["prazo_validade"] = "1 ano" };

    // Act
    var body = DocumentBody.Assemble("laudo", LaudoElements, fields, [], "Dra. Paula");

    // Assert
    body.Should().Be("2. Análise\nA\n\nPrazo de validade\n1 ano");
}
```

`DocumentPdfTests`: render a laudo, a parecer and a multiprofissional model (built with `DocumentPdfModel.From` on in-memory entities, as the existing tests do) and assert the bytes start with `%PDF`; for the multiprofissional assert `model.Identification` contains a line `("Autoria", "Dra. Paula — Psicologia — CRP 06/12345\nRita Lima — Psiquiatria — CRM 1")`; for the laudo assert `model.IdentificationHeading == "1. Identificação"` and the labels `["Pessoa atendida", "Autor(a)", "CRP", "Solicitante", "Finalidade"]`; for the parecer the labels `["Pessoa objeto do questionamento", "Autor(a)", "CRP", "Titulação", "Solicitante", "Finalidade"]`; for the relatório (unchanged layout) `IdentificationHeading` is null and labels `["Paciente", "Autor(a)", "CRP"]`; for declaração/atestado labels `["Paciente"]`. Update the existing PDF tests to the new model shape.

- [ ] **Step 2: Run** `-class "*DocumentBodyTests*"`, `-class "*DocumentPdfTests*"` — FAIL.

- [ ] **Step 3: Implement** `DocumentSections.cs`:

```csharp
using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Documents;

// Content sections of the item-based kinds (Res. CFP 006/2019 arts. 12–14), shared by the signed
// Body and the PDF. Laudo and parecer number their items; item 1 is the identification block the
// PDF prints before these sections. Empty fields are skipped and numbering stays consecutive.
public static class DocumentSections
{
    public static readonly string[] ItemKinds = ["laudo", "parecer", "relatorio_multiprofissional"];

    private sealed record Item(string Code, bool Numbered, string? Heading = null);

    private static readonly Item[] Laudo =
    [
        new("descricao_demanda", true), new("procedimento", true), new("referencial_teorico_metodologico", false),
        new("pessoas_ouvidas_numero_de_encontros_e_duracao", false), new("analise", true), new("conclusao", true),
        new("referencias", true),
    ];

    private static readonly Item[] Parecer =
    [
        new("descricao_demanda", true), new("analise", true), new("conclusao", true), new("referencias", true),
    ];

    private static readonly Item[] Multi =
    [
        new("descricao_demanda", false), new("procedimento", false),
        new("procedimentos_privativos_da_psicologia_em_secao_separada", false),
    ];

    public static IReadOnlyList<DocumentPdfSection> Build(
        string kind, IReadOnlyList<string> elements, IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<TeamMember> team, string authorName)
    {
        var sections = new List<DocumentPdfSection>();
        var number = 1;
        void Add(string kind2, Item item)
        {
            var text = Value(fields, item.Code);
            if (text.Length == 0 || !elements.Contains(item.Code))
            {
                return;
            }
            var label = item.Heading ?? DocumentCatalog.Element(kind2, item.Code).Label;
            sections.Add(new DocumentPdfSection(item.Numbered ? $"{++number}. {label}" : label, text));
        }

        if (kind == "relatorio_multiprofissional")
        {
            foreach (var item in Multi) Add(kind, item);
            var own = Value(fields, "analise");
            if (own.Length > 0)
            {
                sections.Add(new DocumentPdfSection($"Análise — {authorName} (Psicologia)", own));
            }
            foreach (var member in team.Where(m => !string.IsNullOrWhiteSpace(m.Analysis)))
            {
                sections.Add(new DocumentPdfSection($"Análise — {member.Name.Trim()} ({member.Category.Trim()})", member.Analysis.Trim()));
            }
            Add(kind, new Item("conclusao", false));
        }
        else
        {
            foreach (var item in kind == "laudo" ? Laudo : Parecer) Add(kind, item);
        }

        var validity = Value(fields, "prazo_validade");
        if (validity.Length > 0)
        {
            sections.Add(new DocumentPdfSection(DocumentCatalog.Element(kind, "prazo_validade").Label, validity));
        }
        return sections;
    }

    private static string Value(IReadOnlyDictionary<string, string> fields, string code) =>
        fields.TryGetValue(code, out var value) ? value.Trim() : "";
}
```

(Rename the local `kind2` parameter to something clearer if you prefer; keep behaviour.)

`DocumentBody.Assemble` 5-argument overload: when `DocumentSections.ItemKinds.Contains(kind)` return `string.Join("\n\n", DocumentSections.Build(...).Select(s => $"{s.Heading}\n{s.Text}"))`; otherwise the existing behaviour.

`DocumentPdfModel`:

```csharp
public sealed record DocumentPdfLine(string Label, string Value);

public sealed record DocumentPdfModel(
    string Title, string ProviderName, string Crp, string PatientName,
    string? IdentificationHeading, IReadOnlyList<DocumentPdfLine> Identification,
    IReadOnlyList<DocumentPdfSection> Sections, string PracticeCity, DateTimeOffset SignedAt, string Code)
```

`From` builds, with `name = document.SignedByName ?? provider.Name`, `crp = document.SignedByCrp ?? ""`, `patient = SignedPatientName ?? full name`, `fields = document.Fields`:
- declaração / atestado: heading null; `[("Paciente", patient)]`; sections from `Body` as today.
- relatório: heading null; `[("Paciente", patient), ("Autor(a)", name), ("CRP", crp)]`; sections as today.
- laudo: `"1. Identificação"`; `[("Pessoa atendida", patient), ("Autor(a)", name), ("CRP", crp), ("Solicitante", f["nome_solicitante"]), ("Finalidade", f["finalidade"])]`; sections `DocumentSections.Build(...)` with `document.Team` and `name`.
- parecer: `"1. Identificação"`; `[("Pessoa objeto do questionamento", patient), ("Autor(a)", name), ("CRP", crp), ("Titulação", f["nome_autor_inscricao_crp_e_titulacao"]), ("Solicitante", …), ("Finalidade", …)]`.
- relatorio_multiprofissional: `"Identificação"`; `[("Pessoa atendida", patient), ("Solicitante", …), ("Finalidade", …), ("Autoria", string.Join("\n", [$"{name} — Psicologia — CRP {crp}", ..team.Select(m => $"{m.Name.Trim()} — {m.Category.Trim()} — {m.Registry.Trim()}")]))]`.
- Lines whose value is empty after trim are dropped (advisory mode may sign with blanks).

`DocumentPdf.Render`: replace the Paciente/Autor/CRP block with: if `IdentificationHeading` is not null print it like a section heading (`PaddingTop(6).Text(...).SemiBold()`); then each line as `Label: ` SemiBold + value (multi-line values keep their line breaks). The rest is unchanged.

- [ ] **Step 4: Run** `-class "*DocumentBodyTests*"`, `-class "*DocumentPdfTests*"`, `-class "*DocumentConformityTests*"` — PASS. Render one sample of each new kind to the controller's scratchpad `doc-samples/` (laudo.pdf, parecer.pdf, multiprofissional.pdf) and report the paths.

---

### Task 4: Service and endpoint: save, check, sign and correct with the team

**Files:**
- Modify: `anamnys-aspire.Server/Documents/ClinicalDocumentService.cs`, `DocumentEndpoints.cs`, `anamnys-aspire.Server/anamnys-aspire.Server.http`
- Test: `anamnys-aspire.Tests/Documents/ClinicalDocumentServiceTests.cs`, `DocumentEndpointTests.cs`, `DocumentExportTests.cs` (only if a signature changed)

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces: `SaveDocumentRequest(Dictionary<string, string>? Fields, List<string>? Confirmations, List<TeamMemberRequest>? Team)` with `Validate()`; `record TeamMemberRequest(string? Name, string? Category, string? Registry, string? Analysis)`; `ClinicalDocumentService.SaveAsync(db, providerId, documentId, fields, confirmations, IReadOnlyList<TeamMember> team, now, ct)`; `DocumentOutcome.TeamNotAllowed`; `DocumentDetail` gains a last positional member `IReadOnlyList<TeamMember> Team` (JSON `team: [{name, category, registry, analysis}]`).

- [ ] **Step 1: Failing tests** (`ClinicalDocumentServiceTests`, real DB):
  - Change `Create_LaudoKind_ReturnsKindNotAvailable` to `Create_UnknownKind_ReturnsKindNotAvailable` (kind `"carta"`), and extend the `[InlineData]` of `Create_AvailableKind_CreatesDraftWithLatestRulesetAndAudit` with the three new kinds.
  - `Save_MultiTeam_TrimsDropsEmptyMembersAndAudits` — save `[ (" Rita ", "Psiquiatria", "CRM 1", "A"), ("", " ", "", "") ]` → detail team equals `[("Rita","Psiquiatria","CRM 1","A")]`; one `AuditEntries` row with field `"team"`.
  - `Save_TeamOnLaudo_ReturnsTeamNotAllowedAndStoresNothing`.
  - `Save_SameTeamAgain_WritesNoAuditEntry`.
  - `Sign_CompleteLaudo_SignsWithNumberedBody` — body starts with `"2. Descrição da demanda\n"`.
  - `Sign_CompleteParecer_Signs`; `Sign_ParecerWithoutTitulacaoBlocking_ReturnsMissingElements` (missing contains `nome_autor_inscricao_crp_e_titulacao`).
  - `Sign_MultiWithoutTeamBlocking_ReturnsMissingElementsWithTeamCodes`; `Sign_CompleteMulti_SignsAndBodyHasMemberAnalysis`.
  - `Save_MultiAfterSign_ReturnsLocked` (team unchanged).
  - `Correct_SignedMulti_CopiesTeamToNewDraft`.
  - `DocumentEndpointTests`: `PutDocument_TeamOverTenMembers_Returns400WithTeamKey` and `PutDocument_TeamNameTooLong_Returns400WithIndexedKey` (key `team[0].name`).
  - Update every existing `SaveAsync(...)` call to pass the team (`[]`).

- [ ] **Step 2: Run** (Aspire stopped only) — FAIL. Otherwise build only.

- [ ] **Step 3: Implement.**

`DocumentEndpoints.cs`:

```csharp
public sealed record TeamMemberRequest(string? Name, string? Category, string? Registry, string? Analysis);

public sealed record SaveDocumentRequest(Dictionary<string, string>? Fields, List<string>? Confirmations, List<TeamMemberRequest>? Team)
{
    public const int MaxTeam = 10;
    public const int MaxName = 200;
    public const int MaxCategory = 100;
    public const int MaxRegistry = 50;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        var team = Team ?? [];
        if (team.Count > MaxTeam)
        {
            errors["team"] = [$"No máximo {MaxTeam} profissionais."];
            return errors;
        }
        for (var i = 0; i < team.Count; i++)
        {
            Limit(errors, $"team[{i}].name", team[i].Name, MaxName);
            Limit(errors, $"team[{i}].category", team[i].Category, MaxCategory);
            Limit(errors, $"team[{i}].registry", team[i].Registry, MaxRegistry);
            Limit(errors, $"team[{i}].analysis", team[i].Analysis, ClinicalDocumentService.MaxFieldLength);
        }
        return errors;
    }

    public IReadOnlyList<TeamMember> TeamMembers() =>
        (Team ?? []).Select(m => new TeamMember(m.Name ?? "", m.Category ?? "", m.Registry ?? "", m.Analysis ?? "")).ToList();

    private static void Limit(Dictionary<string, string[]> errors, string key, string? value, int max)
    {
        if ((value?.Trim().Length ?? 0) > max)
        {
            errors[key] = [$"Use no máximo {max} caracteres."];
        }
    }
}
```

The PUT handler: 401 first, then `request.Validate()` → `Results.ValidationProblem(errors)`, then `SaveAsync(..., request.TeamMembers(), ...)`. `ToResult` maps `DocumentOutcome.TeamNotAllowed` to `Results.ValidationProblem(new Dictionary<string, string[]> { ["team"] = ["A equipe só existe no relatório multiprofissional."] })`.

`ClinicalDocumentService.SaveAsync` gains `IReadOnlyList<TeamMember> team` after `confirmations`. After the field checks:

```csharp
var nextTeam = team
    .Select(m => new TeamMember(m.Name.Trim(), m.Category.Trim(), m.Registry.Trim(), m.Analysis.Trim()))
    .Where(m => m.Name.Length + m.Category.Length + m.Registry.Length + m.Analysis.Length > 0)
    .ToList();
if (nextTeam.Count > 0 && document.Kind != "relatorio_multiprofissional")
{
    return new DocumentResult(DocumentOutcome.TeamNotAllowed);
}
var teamChanged = !nextTeam.SequenceEqual(document.Team);
```

Include `teamChanged` in the "nothing changed" short-circuit; when changed add `DocumentAudit.Add(db, document.Id, providerId, "document_saved", "team", JsonSerializer.Serialize(document.Team), JsonSerializer.Serialize(nextTeam), now)` and set `document.Team = nextTeam`.

- `Check(...)` passes `d.Team` as the `Team` argument of `ConformityInput`.
- `SignAsync`: `document.Body = DocumentBody.Assemble(document.Kind, ruleset.Elements, document.Fields, document.Team, provider.Name);`
- `CorrectAsync`: the copy gets `Team = original.Team.ToList()`.
- `GetAsync`: pass `document.Team` as the new last argument of `DocumentDetail`.

`.http`: add a PUT example for a relatório multiprofissional draft with a `team` array (not run live).

- [ ] **Step 4: Run** (Aspire stopped only) `-class "*ClinicalDocumentServiceTests*"`, `-class "*DocumentEndpointTests*"`, `-class "*DocumentExportTests*"`, `-class "*Document*"` — PASS. Otherwise build and report which were not run.

---

### Task 5: Provider app

**Files:**
- Modify: `packages/shared/src/lib/types.ts`, `packages/shared/src/api/documents.ts`, `packages/shared/src/lib/i18n/locales/pt.json`, `apps/provider/src/routes/_app/documents/$documentId.tsx`, `apps/provider/src/components/documents/DocumentHelpPanel.tsx` (and the help content it renders)
- Create: `apps/provider/src/components/documents/TeamEditor.tsx`

**Interfaces:**
- Consumes: `DocumentDetail.team`, PUT body `{ fields, confirmations, team }`, 400 keys `team`, `team[i].name|category|registry|analysis`.
- Produces: `export interface DocumentTeamMember { name: string; category: string; registry: string; analysis: string }`; `DocumentDetail.team: DocumentTeamMember[]`; `documentsApi.save(id, fields, confirmations, team: DocumentTeamMember[])`.

- [ ] **Step 1: Types and API.** Add `DocumentTeamMember` and `team` to `DocumentDetail` in `types.ts`; `documentsApi.save` sends `{ fields, confirmations, team }`.

- [ ] **Step 2: `TeamEditor.tsx`.** Props `{ members: DocumentTeamMember[]; onChange: (m: DocumentTeamMember[]) => void; readOnly: boolean; errors?: Record<string, string> }`. One card per member (`rounded-radii-lg border border-outlineVariant p-4`) with inputs Nome, Categoria (placeholder "ex.: Psiquiatria"), Registro (placeholder "ex.: CRM 12345") and a textarea Análise; a "Remover" text button (`text-error hover:underline`) per card; "Adicionar profissional" text button (`text-primary hover:underline`) disabled at 10 members with the hint "Até 10 profissionais."; inputs `maxLength` 200/100/50; read-only when `readOnly`. Show `errors["team[i].name"]` etc. under the matching input and `errors.team` above the list.

- [ ] **Step 3: Editor.** In `$documentId.tsx`:
  - Keep a `team` state initialised from `detail.team`, include it in the dirty check (compare JSON) and the save payload, and reset it wherever `fields` is reset.
  - For `detail.kind === "relatorio_multiprofissional"` render a "Equipe" block (heading + one-line hint "Profissionais que assinam com você. Só você assina no Anamnys (Res. CFP 006/2019, art. 12).") with `<TeamEditor>` **after** the `analise` field and **before** `conclusao`, read-only when the document is not a draft or is saving.
  - For `laudo` and `parecer` show the notice "O PDF organiza este documento em itens numerados (Res. CFP 006/2019, art. 13)." / "(…, art. 14)." where the atestado hint is shown today.
  - Surface a 400 from save as field errors for the team (keys above); other errors keep the existing toast.

- [ ] **Step 4: Help.** Add plain-language entries for laudo, parecer and relatório multiprofissional to the "?" help content, in the style of the existing three (what it is for, what it must contain, the confirmations, that laudo and parecer need references, that the multiprofissional lists the team and only the psychologist signs in Anamnys).

- [ ] **Step 5: Copy.** All new strings in `pt.json` under `documents.*`; validate with `node -e "JSON.parse(require('fs').readFileSync('packages/shared/src/lib/i18n/locales/pt.json','utf8'))"`.

- [ ] **Step 6: Run** `npm run build` and `npm run lint` from `anamnys-aspire/` — both exit 0.

---

### Task 6: Documentation

**Files:**
- Modify: `documentation/22-psychological-documents.md`, `documentation/00-index.md` (only if the chapter title or scope line changes), `design/gaps/2026-10-03-patient-flows-gaps.md` (`## Documentos`), `design/status/2026-10-15-implementation-status.md`, `design/specs/2026-10-19-psychological-documents-design.md` (status line: delivery 2 implemented, pointer to the delivery-2 spec)

- [ ] **Step 1:** Chapter 22: the six kinds; origins for the new elements (spec section 2); team model and limits; numbered items; kind-specific confirmation texts table; delivery 2 done; what stays out (parecer about an institution or a non-patient; co-authors without an account or their own signature).
- [ ] **Step 2:** Gaps file: replace the "Delivery 2 is REQUIRED" entry with the two remaining gaps above.
- [ ] **Step 3:** Status file: F-05 → **done**; "Next" starts with F-02 (blocked by D-01), then F-60; keep the open-verification note for tests not yet run.
