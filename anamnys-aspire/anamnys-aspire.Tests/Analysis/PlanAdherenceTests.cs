using Anamnys.Server.Analysis;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Analysis;

// Real database (design/specs/2026-10-30-plan-adherence-design.md §7): a scratch database from the bootstrap script,
// which carries the F-14 columns, so the tests do not depend on the dev database having had the migration applied.
// Terms live in a private inactive vocabulary (the query does not care whether a linked term is still active).
[Collection(SharedAppHostCollection.Name)]
public class PlanAdherenceTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Day0 = new(2026, 1, 5, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Scenario(RetentionScratch scratch, AnamnysDbContext db, Guid providerId, Guid patientId, Guid vocabularyId) : IAsyncDisposable
    {
        public RetentionScratch Scratch { get; } = scratch;
        public AnamnysDbContext Db { get; } = db;
        public Guid ProviderId { get; } = providerId;
        public Guid PatientId { get; } = patientId;
        public Guid VocabularyId { get; } = vocabularyId;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Scratch.DisposeAsync();
        }
    }

    private async Task<Scenario> StartAsync()
    {
        var scratch = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await scratch.AddProviderAsync(Ct, withCrp: true);
        var patient = await scratch.AddPatientAsync(provider, "Ana", Ct);
        var db = scratch.CreateDbContext();
        var vocabulary = new ThemeVocabulary { Id = Guid.NewGuid(), Version = $"test-{Guid.NewGuid():N}", Active = false };
        db.ThemeVocabularies.Add(vocabulary);
        await db.SaveChangesAsync(Ct);
        return new Scenario(scratch, db, provider, patient, vocabulary.Id);
    }

    private static async Task<ThemeTerm> TermAsync(Scenario s, string label, Guid? parent = null)
    {
        var term = new ThemeTerm
        {
            Id = Guid.NewGuid(), VocabularyId = s.VocabularyId, Code = $"t-{Guid.NewGuid():N}", Label = label,
            ParentTermId = parent, IntroducedOn = new DateOnly(2026, 1, 1),
        };
        s.Db.ThemeTerms.Add(term);
        await s.Db.SaveChangesAsync(Ct);
        return term;
    }

    // Weekly signed notes from Day0, each with an "evolucao" section.
    private static async Task<List<Guid>> SignedNotesAsync(Scenario s, int count, Guid? patientId = null)
    {
        var notes = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var at = Day0.AddDays(7 * i);
            var note = await s.Scratch.AddNoteAsync(s.ProviderId, patientId ?? s.PatientId, at, Ct, signedAt: at);
            await s.Scratch.ExecuteAsync(
                """INSERT INTO "NoteSections" ("NoteId","Key","Content") VALUES (@n, 'evolucao', 'Relata que voltou a acordar de madrugada.')""",
                Ct, ("n", note));
            notes.Add(note);
        }
        return notes;
    }

    private static async Task TagAsync(Scenario s, Guid noteId, ThemeTerm term, string createdBy = "human", bool removed = false, bool addedBy = true)
    {
        s.Db.ThemeTags.Add(new ThemeTag
        {
            Id = Guid.NewGuid(), NoteId = noteId, TermId = term.Id, CreatedBy = createdBy, CreatedAt = Day0,
            AddedById = addedBy ? s.ProviderId : null, RemovedAt = removed ? Day0 : null, RemovedById = removed ? s.ProviderId : null,
        });
        await s.Db.SaveChangesAsync(Ct);
    }

    private static async Task<Guid> PlanAsync(Scenario s, DateTimeOffset createdAt)
    {
        var plan = new TreatmentPlan { Id = Guid.NewGuid(), PatientId = s.PatientId, CreatedAt = createdAt };
        s.Db.TreatmentPlans.Add(plan);
        await s.Db.SaveChangesAsync(Ct);
        return plan.Id;
    }

    private static async Task<Guid> ObjectiveAsync(Scenario s, Guid planId, string description, DateTimeOffset createdAt, Guid? termId)
    {
        var objective = new PlanObjective { Id = Guid.NewGuid(), PlanId = planId, Description = description, CreatedAt = createdAt, TermId = termId };
        s.Db.PlanObjectives.Add(objective);
        await s.Db.SaveChangesAsync(Ct);
        return objective.Id;
    }

    private static async Task<PlanAdherence?> QueryAsync(Scenario s, Guid? providerId = null, Guid? patientId = null)
    {
        await using var db = s.Scratch.CreateDbContext();
        return await PlanAdherenceQuery.ExecuteAsync(db, providerId ?? s.ProviderId, patientId ?? s.PatientId, Ct, Today);
    }

    [Fact]
    public async Task Objectives_LinkedToAParentTheme_CountItsChildren_InSignedNotesWithCountedTagsOnly()
    {
        // Arrange: twelve weekly signed notes; Sono (or its child Insônia) recorded in notes 2, 3 and 8; a removed tag
        // in note 10, an untouched AI proposal in note 11 and a tag on an unsigned note do not count.
        await using var s = await StartAsync();
        var notes = await SignedNotesAsync(s, 12);
        var sono = await TermAsync(s, "Sono");
        var insonia = await TermAsync(s, "Insônia", sono.Id);
        var ansiedade = await TermAsync(s, "Ansiedade");
        await TagAsync(s, notes[1], insonia);
        await TagAsync(s, notes[2], sono);
        await TagAsync(s, notes[7], insonia);
        await TagAsync(s, notes[9], sono, removed: true);
        await TagAsync(s, notes[10], sono, createdBy: "ai", addedBy: false);
        var draft = await s.Scratch.AddNoteAsync(s.ProviderId, s.PatientId, Day0.AddDays(100), Ct);
        await TagAsync(s, draft, sono);
        var plan = await PlanAsync(s, Day0);
        var sleep = await ObjectiveAsync(s, plan, "Dormir melhor", Day0, sono.Id);
        var anxiety = await ObjectiveAsync(s, plan, "Reduzir ansiedade", Day0.AddDays(30), ansiedade.Id);
        var free = await ObjectiveAsync(s, plan, "Retomar exercícios", Day0.AddDays(31), null);

        // Act
        var result = await QueryAsync(s);

        // Assert
        result!.Plan.PlanId.Should().Be(plan);
        result.Plan.ReviewedAt.Should().BeNull();
        result.Objectives.Select(o => o.ObjectiveId).Should().Equal(sleep, anxiety, free);

        var first = result.Objectives[0];
        first.Description.Should().Be("Dormir melhor");
        first.CreatedAt.Should().Be(Day0);
        first.Term.Should().Be(new PlanAdherenceTerm(sono.Id, "Sono"));
        first.FirstOn.Should().Be(DateOnly.FromDateTime(Day0.AddDays(7).UtcDateTime));
        first.LastOn.Should().Be(DateOnly.FromDateTime(Day0.AddDays(49).UtcDateTime));
        first.LastNoteId.Should().Be(notes[7]);
        first.SessionsSince.Should().Be(4);
        first.DaysSince.Should().Be(Today.DayNumber - first.LastOn!.Value.DayNumber);

        // Never recorded: the interval runs from the definition (signed sessions after it, days since its day).
        var second = result.Objectives[1];
        second.Term.Should().Be(new PlanAdherenceTerm(ansiedade.Id, "Ansiedade"));
        (second.FirstOn, second.LastOn, second.LastNoteId).Should().Be((null, null, null));
        second.SessionsSince.Should().Be(7);
        second.DaysSince.Should().Be(Today.DayNumber - DateOnly.FromDateTime(Day0.AddDays(30).UtcDateTime).DayNumber);

        var third = result.Objectives[2];
        third.Term.Should().BeNull();
        (third.FirstOn, third.LastOn, third.LastNoteId, third.SessionsSince, third.DaysSince).Should().Be((null, null, null, null, null));
    }

    [Fact]
    public async Task Query_WithoutAPlan_ListsNothing_AndIsScopedToTheProvider_AndBlockedForADiscardedRecord()
    {
        // Arrange
        await using var s = await StartAsync();
        var other = await s.Scratch.AddProviderAsync(Ct);
        var discarded = await s.Scratch.AddPatientAsync(s.ProviderId, "Bia", Ct, discardedAt: Now);

        // Act
        var result = await QueryAsync(s);

        // Assert
        result!.Plan.Should().Be(new PlanAdherencePlan(null, null));
        result.Objectives.Should().BeEmpty();
        result.FollowUps.Should().BeEmpty();
        (await QueryAsync(s, providerId: other)).Should().BeNull();
        (await QueryAsync(s, patientId: discarded)).Should().BeNull();
        (await QueryAsync(s, patientId: Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Review_CreatesThePlanWhenNone_ThenUpdatesIt_AndAuditsWithIdsOnly()
    {
        // Arrange
        await using var s = await StartAsync();

        // Act
        var first = await PlanAdherenceService.ReviewAsync(s.Scratch.CreateDbContext(), s.ProviderId, s.PatientId, Now, Ct);
        var second = await PlanAdherenceService.ReviewAsync(s.Scratch.CreateDbContext(), s.ProviderId, s.PatientId, Now.AddDays(3), Ct);

        // Assert
        first.Outcome.Should().Be(PlanOutcome.Ok);
        second.Outcome.Should().Be(PlanOutcome.Ok);
        second.PlanId.Should().Be(first.PlanId);
        second.ReviewedAt.Should().Be(Now.AddDays(3));
        (await s.Db.TreatmentPlans.CountAsync(t => t.PatientId == s.PatientId, Ct)).Should().Be(1);
        (await QueryAsync(s))!.Plan.Should().Be(new PlanAdherencePlan(first.PlanId, Now.AddDays(3)));
        var audits = await s.Db.AuditEntries.AsNoTracking().Where(a => a.Action == "plan_reviewed" && a.ActorId == s.ProviderId).ToListAsync(Ct);
        audits.Should().HaveCount(2);
        audits.Should().AllSatisfy(a =>
        {
            a.NoteId.Should().BeNull();
            a.NewValue.Should().Be($"{s.PatientId} {first.PlanId}");
        });
    }

    [Fact]
    public async Task Review_ArchivedIsRefused_ForeignAndDiscardedAreNotFound()
    {
        // Arrange
        await using var s = await StartAsync();
        var other = await s.Scratch.AddProviderAsync(Ct);
        var discarded = await s.Scratch.AddPatientAsync(s.ProviderId, "Bia", Ct, discardedAt: Now);
        await s.Scratch.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @pa""", Ct, ("pa", s.PatientId));

        // Act
        var archived = await PlanAdherenceService.ReviewAsync(s.Scratch.CreateDbContext(), s.ProviderId, s.PatientId, Now, Ct);
        var foreign = await PlanAdherenceService.ReviewAsync(s.Scratch.CreateDbContext(), other, s.PatientId, Now, Ct);
        var gone = await PlanAdherenceService.ReviewAsync(s.Scratch.CreateDbContext(), s.ProviderId, discarded, Now, Ct);

        // Assert
        archived.Outcome.Should().Be(PlanOutcome.Archived);
        foreign.Outcome.Should().Be(PlanOutcome.NotFound);
        gone.Outcome.Should().Be(PlanOutcome.NotFound);
        (await s.Db.TreatmentPlans.CountAsync(t => t.PatientId == s.PatientId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task FollowUp_CreateWithAndWithoutPassage_ResolveFromALaterNote_Reopen()
    {
        // Arrange
        await using var s = await StartAsync();
        var notes = await SignedNotesAsync(s, 6);

        // Act
        var plain = await PlanAdherenceService.CreateFollowUpAsync(
            s.Scratch.CreateDbContext(), s.ProviderId, notes[1], new FollowUpRequest(" Retomar a relação com o pai "), Now, Ct);
        var quoted = await PlanAdherenceService.CreateFollowUpAsync(
            s.Scratch.CreateDbContext(), s.ProviderId, notes[2], new FollowUpRequest("Sono", "evolucao", 20, 40), Now.AddMinutes(1), Ct);
        var resolved = await PlanAdherenceService.ResolveFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, plain.Id!.Value, notes[4], Now.AddDays(1), Ct);
        var again = await PlanAdherenceService.ResolveFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, plain.Id.Value, null, Now.AddDays(2), Ct);
        var listed = await QueryAsync(s);

        // Assert
        plain.Outcome.Should().Be(FollowUpOutcome.Ok);
        quoted.Outcome.Should().Be(FollowUpOutcome.Ok);
        resolved.Should().Be(FollowUpOutcome.Ok);
        again.Should().Be(FollowUpOutcome.Ok);
        listed!.FollowUps.Select(f => f.Id).Should().Equal(quoted.Id!.Value, plain.Id.Value);

        var open = listed.FollowUps[0];
        open.Description.Should().Be("Sono");
        open.NoteId.Should().Be(notes[2]);
        open.OriginOn.Should().Be(DateOnly.FromDateTime(Day0.AddDays(14).UtcDateTime));
        open.ResolvedAt.Should().BeNull();
        open.Excerpt!.Section.Should().Be("evolucao");
        open.Excerpt.Quote.Should().Be("Relata que voltou a acordar de madrugada."[20..40]);
        open.SessionsSince.Should().Be(3);

        var done = listed.FollowUps[1];
        done.Description.Should().Be("Retomar a relação com o pai");
        done.Excerpt.Should().BeNull();
        done.ResolvedAt.Should().Be(Now.AddDays(1));
        done.ResolvedNoteId.Should().Be(notes[4]);
        done.SessionsSince.Should().Be(3);
        (await s.Db.FollowUpItems.AsNoTracking().SingleAsync(f => f.Id == plain.Id, Ct)).ResolvedById.Should().Be(s.ProviderId);

        // Reopen clears the resolution; reopening an open one changes nothing.
        (await PlanAdherenceService.ReopenFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, plain.Id.Value, Now.AddDays(3), Ct)).Should().Be(FollowUpOutcome.Ok);
        (await PlanAdherenceService.ReopenFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, plain.Id.Value, Now.AddDays(4), Ct)).Should().Be(FollowUpOutcome.Ok);
        var reopened = (await QueryAsync(s))!.FollowUps;
        reopened.Select(f => f.Id).Should().Equal(plain.Id.Value, quoted.Id.Value);
        (reopened[0].ResolvedAt, reopened[0].ResolvedNoteId, reopened[0].SessionsSince).Should().Be((null, null, 4));
        (await s.Db.FollowUpItems.AsNoTracking().SingleAsync(f => f.Id == plain.Id, Ct)).ResolvedById.Should().BeNull();

        // Audit: ids only, one entry per change (the repeated resolve and reopen were no-ops).
        var audits = await s.Db.AuditEntries.AsNoTracking()
            .Where(a => a.ActorId == s.ProviderId && a.Action.StartsWith("follow_up_")).OrderBy(a => a.At).ToListAsync(Ct);
        audits.Select(a => (a.Action, a.NoteId, a.NewValue)).Should().Equal(
            ("follow_up_created", notes[1], plain.Id.ToString()),
            ("follow_up_created", notes[2], quoted.Id.ToString()),
            ("follow_up_resolved", notes[4], plain.Id.ToString()),
            ("follow_up_reopened", notes[1], plain.Id.ToString()));
    }

    [Fact]
    public async Task FollowUp_RefusesAnInvalidPassage_AResolvingNoteOfAnotherPatient_ArchivedAndForeign()
    {
        // Arrange
        await using var s = await StartAsync();
        var notes = await SignedNotesAsync(s, 2);
        var otherPatient = await s.Scratch.AddPatientAsync(s.ProviderId, "Bia", Ct);
        var otherNotes = await SignedNotesAsync(s, 1, otherPatient);
        var otherProvider = await s.Scratch.AddProviderAsync(Ct);
        var item = await PlanAdherenceService.CreateFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, notes[0], new FollowUpRequest("x"), Now, Ct);

        // Act
        var badOffsets = await PlanAdherenceService.CreateFollowUpAsync(
            s.Scratch.CreateDbContext(), s.ProviderId, notes[0], new FollowUpRequest("x", "evolucao", 10, 999), Now, Ct);
        var badSection = await PlanAdherenceService.CreateFollowUpAsync(
            s.Scratch.CreateDbContext(), s.ProviderId, notes[0], new FollowUpRequest("x", "inexistente"), Now, Ct);
        var foreignNote = await PlanAdherenceService.CreateFollowUpAsync(s.Scratch.CreateDbContext(), otherProvider, notes[0], new FollowUpRequest("x"), Now, Ct);
        var wrongPatient = await PlanAdherenceService.ResolveFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, item.Id!.Value, otherNotes[0], Now, Ct);
        var foreignResolve = await PlanAdherenceService.ResolveFollowUpAsync(s.Scratch.CreateDbContext(), otherProvider, item.Id.Value, null, Now, Ct);
        var foreignReopen = await PlanAdherenceService.ReopenFollowUpAsync(s.Scratch.CreateDbContext(), otherProvider, item.Id.Value, Now, Ct);
        await s.Scratch.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @pa""", Ct, ("pa", s.PatientId));
        var archivedCreate = await PlanAdherenceService.CreateFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, notes[1], new FollowUpRequest("x"), Now, Ct);
        var archivedResolve = await PlanAdherenceService.ResolveFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, item.Id.Value, null, Now, Ct);
        var archivedReopen = await PlanAdherenceService.ReopenFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, item.Id.Value, Now, Ct);

        // Assert
        badOffsets.Outcome.Should().Be(FollowUpOutcome.InvalidSelection);
        badSection.Outcome.Should().Be(FollowUpOutcome.InvalidSelection);
        foreignNote.Outcome.Should().Be(FollowUpOutcome.NotFound);
        wrongPatient.Should().Be(FollowUpOutcome.InvalidNote);
        foreignResolve.Should().Be(FollowUpOutcome.NotFound);
        foreignReopen.Should().Be(FollowUpOutcome.NotFound);
        archivedCreate.Outcome.Should().Be(FollowUpOutcome.Archived);
        archivedResolve.Should().Be(FollowUpOutcome.Archived);
        archivedReopen.Should().Be(FollowUpOutcome.Archived);
        var rows = await s.Db.FollowUpItems.AsNoTracking().Where(f => f.PatientId == s.PatientId).ToListAsync(Ct);
        rows.Should().ContainSingle().Which.ResolvedAt.Should().BeNull();
        (await s.Db.ExcerptRefs.CountAsync(r => r.ProviderId == s.ProviderId, Ct)).Should().Be(0);
        (await s.Db.AuditEntries.CountAsync(a => a.ActorId == s.ProviderId && a.Action.StartsWith("follow_up_"), Ct)).Should().Be(1);
    }

    // Fix round 1: the resolving note must be a later note (not the origin, not dated before it).
    [Fact]
    public async Task FollowUp_Resolve_RefusesTheOriginNote_AndAnEarlierNote_AcceptsALaterOne()
    {
        // Arrange
        await using var s = await StartAsync();
        var notes = await SignedNotesAsync(s, 4);
        var review = await s.Scratch.AddNoteAsync(s.ProviderId, s.PatientId, Day0.AddDays(100), Ct);
        var item = await PlanAdherenceService.CreateFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, notes[2], new FollowUpRequest("x"), Now, Ct);

        // Act
        var origin = await PlanAdherenceService.ResolveFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, item.Id!.Value, notes[2], Now, Ct);
        var earlier = await PlanAdherenceService.ResolveFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, item.Id.Value, notes[0], Now, Ct);
        var later = await PlanAdherenceService.ResolveFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, item.Id.Value, review, Now, Ct);

        // Assert
        origin.Should().Be(FollowUpOutcome.InvalidNote);
        earlier.Should().Be(FollowUpOutcome.InvalidNote);
        later.Should().Be(FollowUpOutcome.Ok);
        (await s.Db.FollowUpItems.AsNoTracking().SingleAsync(f => f.Id == item.Id, Ct)).ResolvedNoteId.Should().Be(review);
    }

    // Fix round 1: a quote is either found (exact passage) or refused; no silent whole-section fallback.
    [Fact]
    public async Task FollowUp_QuotePassage_IsExactWhenFound_AndRefusedWhenNot()
    {
        // Arrange
        await using var s = await StartAsync();
        var notes = await SignedNotesAsync(s, 1);

        // Act
        var found = await PlanAdherenceService.CreateFollowUpAsync(
            s.Scratch.CreateDbContext(), s.ProviderId, notes[0], new FollowUpRequest("x", Quote: "acordar de madrugada"), Now, Ct);
        var missing = await PlanAdherenceService.CreateFollowUpAsync(
            s.Scratch.CreateDbContext(), s.ProviderId, notes[0], new FollowUpRequest("x", Quote: "frase que não existe"), Now, Ct);
        var missingInSection = await PlanAdherenceService.CreateFollowUpAsync(
            s.Scratch.CreateDbContext(), s.ProviderId, notes[0], new FollowUpRequest("x", "evolucao", Quote: "frase que não existe"), Now, Ct);

        // Assert
        found.Outcome.Should().Be(FollowUpOutcome.Ok);
        missing.Outcome.Should().Be(FollowUpOutcome.InvalidSelection);
        missingInSection.Outcome.Should().Be(FollowUpOutcome.InvalidSelection);
        var excerpt = (await QueryAsync(s))!.FollowUps.Should().ContainSingle().Subject.Excerpt!;
        excerpt.Quote.Should().Be("acordar de madrugada");
        (await s.Db.ExcerptRefs.AsNoTracking().SingleAsync(r => r.ProviderId == s.ProviderId, Ct)).Kind.Should().Be("exact");
    }

    // Fix round 1: only notes in review or signed take a follow-up.
    [Fact]
    public async Task FollowUp_Create_OnlyOnNotesInReviewOrSigned()
    {
        // Arrange
        await using var s = await StartAsync();
        var draft = await s.Scratch.AddNoteAsync(s.ProviderId, s.PatientId, Day0, Ct, status: "Draft");
        var processing = await s.Scratch.AddNoteAsync(s.ProviderId, s.PatientId, Day0, Ct, status: "Processing");
        var review = await s.Scratch.AddNoteAsync(s.ProviderId, s.PatientId, Day0, Ct);
        var signed = (await SignedNotesAsync(s, 1))[0];
        var exported = await s.Scratch.AddNoteAsync(s.ProviderId, s.PatientId, Day0, Ct, signedAt: Day0);
        await s.Scratch.ExecuteAsync(
            """UPDATE "Notes" SET "Status" = 'Exported', "PdfObjectKey" = 'k', "PdfSha256" = repeat('a', 64), "ExportedAt" = "SignedAt" WHERE "Id" = @n""",
            Ct, ("n", exported));

        // Act
        async Task<FollowUpOutcome> Mark(Guid note) =>
            (await PlanAdherenceService.CreateFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, note, new FollowUpRequest("x"), Now, Ct)).Outcome;

        // Assert
        (await Mark(draft)).Should().Be(FollowUpOutcome.NoteNotReady);
        (await Mark(processing)).Should().Be(FollowUpOutcome.NoteNotReady);
        (await Mark(review)).Should().Be(FollowUpOutcome.Ok);
        (await Mark(signed)).Should().Be(FollowUpOutcome.Ok);
        (await Mark(exported)).Should().Be(FollowUpOutcome.Ok);
        (await s.Db.FollowUpItems.CountAsync(f => f.PatientId == s.PatientId, Ct)).Should().Be(3);
    }

    [Fact]
    public async Task FollowUp_OnADiscardedRecord_IsNotFound()
    {
        // Arrange: the discard deletes the notes and the follow-ups, so nothing of the record can be reached.
        await using var s = await StartAsync();
        var notes = await SignedNotesAsync(s, 1);
        var item = await PlanAdherenceService.CreateFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, notes[0], new FollowUpRequest("x"), Now, Ct);
        await s.Scratch.MarkDiscardedAsync(s.PatientId, Now, Ct);

        // Act
        var create = await PlanAdherenceService.CreateFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, notes[0], new FollowUpRequest("x"), Now, Ct);
        var resolve = await PlanAdherenceService.ResolveFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, item.Id!.Value, null, Now, Ct);
        var reopen = await PlanAdherenceService.ReopenFollowUpAsync(s.Scratch.CreateDbContext(), s.ProviderId, item.Id.Value, Now, Ct);

        // Assert
        create.Outcome.Should().Be(FollowUpOutcome.NotFound);
        resolve.Should().Be(FollowUpOutcome.NotFound);
        reopen.Should().Be(FollowUpOutcome.NotFound);
    }

    [Fact]
    public void FollowUpRequest_Validate_RequiresADescription_AndAWholeSelection()
    {
        // Arrange / Act
        var empty = new FollowUpRequest("  ").Validate();
        var tooLong = new FollowUpRequest(new string('a', 501)).Validate();
        var halfSelection = new FollowUpRequest("x", "evolucao", 3).Validate();
        var selectionWithoutSection = new FollowUpRequest("x", null, 3, 5).Validate();
        var ok = new FollowUpRequest("x", "evolucao", 3, 5).Validate();

        // Assert
        empty.Should().ContainKey("description");
        tooLong.Should().ContainKey("description");
        halfSelection.Should().ContainKey("offsetStart");
        selectionWithoutSection.Should().ContainKey("offsetStart");
        ok.Should().BeEmpty();
    }
}
