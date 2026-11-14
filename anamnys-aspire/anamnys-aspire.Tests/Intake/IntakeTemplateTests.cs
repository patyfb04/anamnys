using Anamnys.Server.Intakes;
using FluentAssertions;
using static Anamnys.Tests.Intakes.IntakeScene;

namespace Anamnys.Tests.Intakes;

// design/specs/2026-11-12-structured-intake-design.md §2 Seed, §3 Templates, §6 "Seed", "Adapt and versioning".
[Collection(SharedAppHostCollection.Name)]
public class IntakeTemplateTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_SeededDefault_HasSixSectionsQuestionsAndDestinations()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var list = await IntakeTemplates.ListAsync(db, s.Provider, Ct);
        var template = await IntakeTemplates.GetAsync(db, s.Provider, IntakeRules.SystemDefaultTemplateId, Ct);

        // Assert
        list.DefaultTemplateId.Should().Be(IntakeRules.SystemDefaultTemplateId);
        list.Templates.Should().ContainSingle().Which.Should().Be(new IntakeTemplateSummary(
            IntakeRules.SystemDefaultTemplateId, "Anamnese padrão", "1", true, null, false, false));
        template!.Sections.Select(x => x.Key).Should().Equal("identificacao", "queixa", "historia", "saude", "contexto", "objetivos");
        var questions = template.Sections.SelectMany(x => x.Questions).ToList();
        questions.Should().HaveCountGreaterThanOrEqualTo(30);
        questions.Select(q => q.Position).Should().BeInAscendingOrder();
        questions.Where(q => q.TargetField != null).ToDictionary(q => q.Key, q => q.TargetField).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["data_nascimento"] = "patient.dateOfBirth", ["telefone"] = "patient.phone", ["email"] = "patient.contactEmail",
            ["medicacoes"] = "medication", ["diagnosticos_previos"] = "diagnosis", ["objetivos_terapia"] = "plan.objective",
        });
        questions.Single(q => q.Key == "data_nascimento").AnswerType.Should().Be("date");
        questions.Where(q => q.AnswerType == "choice").Should().OnlyContain(q => q.Choices != null && q.Choices.Count > 0);
        questions.Where(q => q.Required).Select(q => q.Key).Should().BeEquivalentTo("queixa_principal", "risco");
    }

    [Fact]
    public async Task Update_DefaultTemplate_IsForbidden_AndForeignTemplatesAreNotFound()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var request = await RequestFromAsync(s, IntakeRules.SystemDefaultTemplateId);
        var foreign = (await IntakeTemplates.AdaptAsync(db, s.OtherProvider, IntakeRules.SystemDefaultTemplateId, null, Now, Ct)).Value!;

        // Act
        var onDefault = await IntakeTemplates.UpdateAsync(db, s.Provider, IntakeRules.SystemDefaultTemplateId, request, Now, Ct);
        var onForeign = await IntakeTemplates.UpdateAsync(db, s.Provider, foreign.Id, request, Now, Ct);
        var getForeign = await IntakeTemplates.GetAsync(db, s.Provider, foreign.Id, Ct);
        var adaptForeign = await IntakeTemplates.AdaptAsync(db, s.Provider, foreign.Id, null, Now, Ct);
        var archiveDefault = await IntakeTemplates.ArchiveAsync(db, s.Provider, IntakeRules.SystemDefaultTemplateId, Now, Ct);
        var settingForeign = await IntakeTemplates.SetDefaultAsync(db, s.Provider, foreign.Id, Ct);

        // Assert
        onDefault.Outcome.Should().Be(IntakeOutcome.Forbidden);
        onForeign.Outcome.Should().Be(IntakeOutcome.NotFound);
        getForeign.Should().BeNull();
        adaptForeign.Outcome.Should().Be(IntakeOutcome.NotFound);
        archiveDefault.Outcome.Should().Be(IntakeOutcome.Forbidden);
        settingForeign.Outcome.Should().Be(IntakeOutcome.NotFound);
        (await IntakeTemplates.ListAsync(db, s.Provider, Ct)).Templates.Should().ContainSingle();
    }

    [Fact]
    public async Task AdaptThenUpdate_UnusedTemplate_IsEditedInPlace()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var adapted = (await IntakeTemplates.AdaptAsync(db, s.Provider, IntakeRules.SystemDefaultTemplateId, null, Now, Ct)).Value!;
        var second = (await IntakeTemplates.AdaptAsync(db, s.Provider, IntakeRules.SystemDefaultTemplateId, null, Now, Ct)).Value!;
        var request = await RequestFromAsync(s, adapted.Id);
        var questions = request.Questions!.Select(q => q!).ToList();
        questions.RemoveAll(q => q.Key == "lazer");
        questions[questions.FindIndex(q => q.Key == "rotina")] = questions.Single(q => q.Key == "rotina") with { Disabled = true };
        questions[questions.FindIndex(q => q.Key == "ocupacao")] = questions.Single(q => q.Key == "ocupacao") with { Label = "Profissão" };
        questions.Add(new IntakeTemplateQuestionInput("hobby", "extra", "Extra", "Algum hobby?", null, "choice", ["Sim", "Não"], false, null, false));
        var updated = await IntakeTemplates.UpdateAsync(db, s.Provider, adapted.Id, request with { Name = "Anamnese adulto", Questions = questions }, Now, Ct);

        // Assert
        adapted.Name.Should().Be("Minha anamnese");
        second.Name.Should().Be("Minha anamnese 2");
        adapted.BasedOnTemplateId.Should().Be(IntakeRules.SystemDefaultTemplateId);
        adapted.IsDefault.Should().BeFalse();
        updated.Outcome.Should().Be(IntakeOutcome.Ok);
        var detail = updated.Value!;
        detail.Id.Should().Be(adapted.Id);
        (detail.Name, detail.Version, detail.Archived).Should().Be(("Anamnese adulto", "1", false));
        var all = detail.Sections.SelectMany(x => x.Questions).ToList();
        all.Should().NotContain(q => q.Key == "lazer");
        all.Single(q => q.Key == "rotina").Disabled.Should().BeTrue();
        all.Single(q => q.Key == "ocupacao").Label.Should().Be("Profissão");
        detail.Sections.Last().Key.Should().Be("extra");
        detail.Sections.Last().Questions.Single().Choices.Should().Equal("Sim", "Não");
        var originalIds = adapted.Sections.SelectMany(x => x.Questions).ToDictionary(q => q.Key, q => q.Id);
        all.Single(q => q.Key == "ocupacao").Id.Should().Be(originalIds["ocupacao"]);
        (await s.AuditAsync(Ct)).Should().Contain(a => a.StartsWith($"intake_template_adapted {adapted.Id} edited"));
    }

    [Fact]
    public async Task Update_UsedTemplate_CreatesNewVersion_AndOldIntakesKeepTheirQuestions()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        Guid adaptedId;
        await using (var db = s.CreateDbContext())
        {
            adaptedId = (await IntakeTemplates.AdaptAsync(db, s.Provider, IntakeRules.SystemDefaultTemplateId, new("Adulto"), Now, Ct)).Value!.Id;
            (await IntakeTemplates.SetDefaultAsync(db, s.Provider, adaptedId, Ct)).Outcome.Should().Be(IntakeOutcome.Ok);
        }
        var intake = await s.StartAsync(Ct);
        await s.AnswerAsync(intake, Ct, ("lazer", "Corrida"));
        var request = await RequestFromAsync(s, adaptedId);
        var questions = request.Questions!.Where(q => q!.Key != "lazer").ToList();

        // Act
        IntakeResult<IntakeTemplateDetail> updated;
        await using (var db = s.CreateDbContext())
        {
            updated = await IntakeTemplates.UpdateAsync(db, s.Provider, adaptedId, request with { Questions = questions }, Now, Ct);
        }

        // Assert
        updated.Outcome.Should().Be(IntakeOutcome.Ok);
        updated.Value!.Id.Should().NotBe(adaptedId);
        (updated.Value.Name, updated.Value.Version, updated.Value.BasedOnTemplateId).Should().Be(("Adulto", "2", adaptedId));
        await using var check = s.CreateDbContext();
        var list = await IntakeTemplates.ListAsync(check, s.Provider, Ct);
        list.DefaultTemplateId.Should().Be(updated.Value.Id);
        list.Templates.Single(t => t.Id == adaptedId).Should().Match<IntakeTemplateSummary>(t => t.Archived && t.InUse);
        (await s.IntakeAsync(intake, Ct)).TemplateId.Should().Be(adaptedId);
        var detail = await IntakeService.GetAsync(check, s.Provider, intake, Ct);
        detail!.Sections.SelectMany(x => x.Questions).Should().Contain(q => q.Key == "lazer");
        detail.Answers.Should().ContainSingle(a => a.Answer == "Corrida");
        var again = await IntakeTemplates.UpdateAsync(check, s.Provider, adaptedId, request, Now, Ct);
        again.Outcome.Should().Be(IntakeOutcome.TemplateArchived);
        (await s.AuditAsync(Ct)).Should().Contain($"intake_template_adapted {updated.Value.Id} version 2 of {adaptedId}");
    }

    [Fact]
    public async Task Validate_RejectsBadQuestionLists()
    {
        // Arrange
        var good = new IntakeTemplateQuestionInput("a", "s", "Seção", "Pergunta", null, "text", null, false, null, false);

        // Act
        var empty = new UpdateIntakeTemplateRequest(null, []).Validate();
        var duplicate = new UpdateIntakeTemplateRequest(null, [good, good]).Validate();
        var badKey = new UpdateIntakeTemplateRequest(null, [good with { Key = "Com Espaço" }]).Validate();
        var choiceWithout = new UpdateIntakeTemplateRequest(null, [good with { AnswerType = "choice" }]).Validate();
        var badTarget = new UpdateIntakeTemplateRequest(null, [good with { TargetField = "patient.name" }]).Validate();
        var wrongType = new UpdateIntakeTemplateRequest(null, [good with { TargetField = "patient.dateOfBirth" }]).Validate();
        var twice = new UpdateIntakeTemplateRequest(null, [good with { TargetField = "patient.phone" }, good with { Key = "b", TargetField = "patient.phone" }]).Validate();
        var allOff = new UpdateIntakeTemplateRequest(null, [good with { Disabled = true }]).Validate();
        var ok = new UpdateIntakeTemplateRequest("Nome", [good, good with { Key = "b", TargetField = "medication" }]).Validate();
        var mixedTitles = new UpdateIntakeTemplateRequest(null, [good, good with { Key = "b", SectionTitle = "Outro título" }]).Validate();

        // Assert
        empty.Should().ContainKey("questions");
        duplicate.Should().ContainKey("questions[1].key");
        badKey.Should().ContainKey("questions[0].key");
        choiceWithout.Should().ContainKey("questions[0].choices");
        badTarget.Should().ContainKey("questions[0].targetField");
        wrongType.Should().ContainKey("questions[0].targetField");
        twice.Should().ContainKey("questions[1].targetField");
        allOff.Should().ContainKey("questions");
        ok.Should().BeEmpty();
        mixedTitles.Should().ContainKey("questions[1].sectionTitle");
    }

    [Fact]
    public async Task Update_OntoAnotherTemplatesName_IsNameTaken()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var first = (await IntakeTemplates.AdaptAsync(db, s.Provider, IntakeRules.SystemDefaultTemplateId, new("Adulto"), Now, Ct)).Value!;
        var second = (await IntakeTemplates.AdaptAsync(db, s.Provider, IntakeRules.SystemDefaultTemplateId, new("Infantil"), Now, Ct)).Value!;
        (await IntakeTemplates.SetDefaultAsync(db, s.Provider, second.Id, Ct)).Outcome.Should().Be(IntakeOutcome.Ok);
        await s.StartAsync(Ct);
        var request = await RequestFromAsync(s, first.Id);

        // Act
        var inPlace = await IntakeTemplates.UpdateAsync(db, s.Provider, first.Id, request with { Name = "Infantil" }, Now, Ct);
        var versioned = await IntakeTemplates.UpdateAsync(db, s.Provider, second.Id, request with { Name = "Adulto" }, Now, Ct);
        var sameName = await IntakeTemplates.UpdateAsync(db, s.Provider, second.Id, request, Now, Ct);

        // Assert
        inPlace.Outcome.Should().Be(IntakeOutcome.NameTaken);
        versioned.Outcome.Should().Be(IntakeOutcome.NameTaken);
        sameName.Outcome.Should().Be(IntakeOutcome.Ok);
        (sameName.Value!.Name, sameName.Value.Version).Should().Be(("Infantil", "2"));
    }

    internal static async Task<UpdateIntakeTemplateRequest> RequestFromAsync(IntakeScene s, Guid templateId)
    {
        await using var db = s.CreateDbContext();
        var template = await IntakeTemplates.GetAsync(db, s.Provider, templateId, Ct)
            ?? throw new InvalidOperationException("template not visible");
        return new UpdateIntakeTemplateRequest(null, [.. template.Sections.SelectMany(section => section.Questions.Select(q =>
            (IntakeTemplateQuestionInput?)new IntakeTemplateQuestionInput(
                q.Key, section.Key, section.Title, q.Label, q.HelpText, q.AnswerType, q.Choices?.ToList(), q.Required, q.TargetField, q.Disabled)))]);
    }
}
