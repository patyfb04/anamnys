using Anamnys.Server.Dossiers;
using Anamnys.Server.Excerpts;
using Anamnys.Server.Exports;
using Anamnys.Server.Intakes;
using Anamnys.Server.Notes;
using Anamnys.Tests.Integrity;
using FluentAssertions;
using static Anamnys.Tests.Intakes.IntakeScene;

namespace Anamnys.Tests.Intakes;

// An own template's section titles (an added section has a "secao_N" key) reach every output of the signed Anamnese
// note: the dossier PDF, the account export (PDF and JSON) and excerpt passages, not only the note page.
[Collection(SharedAppHostCollection.Name)]
public class IntakeSectionTitleTests(SharedAppHostFixture fixture)
{
    private const string AddedKey = "secao_1";
    private const string AddedTitle = "Saúde física e medicação";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Outputs_NoteFromOwnTemplateWithAddedSection_ShowTheTemplateTitles()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var noteId = await SignedNoteWithAddedSectionAsync(s);
        using var tsa = new FakeTimestampAuthority();

        // Act
        DossierData dossier;
        await using (var db = s.CreateDbContext())
        {
            dossier = await DossierData.LoadAsync(
                db, tsa, new DossierRequest(Guid.NewGuid(), 1, s.Provider, s.Patient, "crp", null, null, null, Now), Ct);
        }
        var dossierModel = DossierBuilder.NoteModel(dossier, dossier.Notes.Single(n => n.Id == noteId));
        AccountData account;
        await using (var db = s.CreateDbContext())
        {
            account = await ExportData.LoadAsync(db, s.Provider, Ct);
        }
        var entry = account.Patients.Single(p => p.Patient.Id == s.Patient);
        var exportModel = AccountExportBuilder.NoteModel(account, entry.Patient, account.Notes.Single(n => n.Id == noteId));
        var exportJson = ExportViews.Patient(account, entry).Json.Notas.Single(n => n.Id == noteId);
        ExcerptPassage? passage;
        await using (var db = s.CreateDbContext())
        {
            var excerpt = await ExcerptService.AddAsync(db, s.Provider, noteId, AddedKey, "Sertralina 50 mg", null, null, Ct);
            await db.SaveChangesAsync(Ct);
            passage = await ExcerptService.GetPassageAsync(db, s.Provider, excerpt.Ref!.Id, Ct);
        }

        // Assert
        dossierModel.Sections.Select(x => x.Title).Should().Contain(AddedTitle).And.NotContain(AddedKey).And.Contain("Identificação");
        exportModel.Sections.Select(x => x.Title).Should().Contain(AddedTitle).And.NotContain(AddedKey);
        exportJson.Secoes.Single(x => x.Chave == AddedKey).Titulo.Should().Be(AddedTitle);
        exportJson.Secoes.Single(x => x.Chave == "queixa").Titulo.Should().Be("Queixa principal");
        passage!.SectionTitle.Should().Be(AddedTitle);
    }

    // An own copy of the default template whose "saude" section was replaced by an added section, made the provider's
    // default; an intake on it is answered, generated and signed.
    private static async Task<Guid> SignedNoteWithAddedSectionAsync(IntakeScene s)
    {
        Guid templateId;
        await using (var db = s.CreateDbContext())
        {
            templateId = (await IntakeTemplates.AdaptAsync(db, s.Provider, IntakeRules.SystemDefaultTemplateId, null, Now, Ct)).Value!.Id;
            (await IntakeTemplates.SetDefaultAsync(db, s.Provider, templateId, Ct)).Outcome.Should().Be(IntakeOutcome.Ok);
        }
        await s.Db.ExecuteAsync(
            """UPDATE "IntakeQuestions" SET "SectionKey" = @k, "SectionTitle" = @t WHERE "TemplateId" = @id AND "SectionKey" = 'saude'""",
            Ct, ("k", AddedKey), ("t", AddedTitle), ("id", templateId));
        var (_, noteId) = await s.SignedIntakeAsync(Ct);
        return noteId;
    }
}
