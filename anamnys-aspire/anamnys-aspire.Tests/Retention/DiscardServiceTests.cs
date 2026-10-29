using Anamnys.Server.Data.Entities;
using Anamnys.Server.Retention;
using Anamnys.Tests.Notes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;

namespace Anamnys.Tests.Retention;

// The irreversible discard and its disposal term (design/specs/2026-10-28-retention-panel-design.md §3, §6 "Discard").
[Collection(SharedAppHostCollection.Name)]
public class DiscardServiceTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
    private const string Marker = "CONTEUDO-CLINICO-5d2b";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DiscardServiceTests() => QuestPDF.Settings.License = LicenseType.Community;

    // Every table that holds the patient's clinical content, with the condition that finds the patient's rows.
    private static readonly string[] PatientRows =
    [
        """SELECT count(*) FROM "Notes" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "NoteSections" s WHERE s."NoteId" = ANY(@notes)""",
        """SELECT count(*) FROM "ThemeTags" WHERE "NoteId" = ANY(@notes)""",
        """SELECT count(*) FROM "PatientQuotes" WHERE "NoteId" = ANY(@notes)""",
        """SELECT count(*) FROM "AuditEntries" WHERE "NoteId" = ANY(@notes) OR "DocumentId" = ANY(@docs)""",
        """SELECT count(*) FROM "ExcerptRefs" WHERE "SourceId" = ANY(@notes) OR "SourceId" = ANY(@ext) OR "SourceId" = ANY(@sessions)""",
        """SELECT count(*) FROM "ClinicalDocuments" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "ConformityChecks" WHERE "DocumentId" = ANY(@docs)""",
        """SELECT count(*) FROM "ExternalDocuments" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "ExternalDocumentPages" WHERE "ExternalDocumentId" = ANY(@ext)""",
        """SELECT count(*) FROM "ExtractedFacts" WHERE "ExternalDocumentId" = ANY(@ext)""",
        """SELECT count(*) FROM "Sessions" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "Transcripts" WHERE "SessionId" = ANY(@sessions)""",
        """SELECT count(*) FROM "Appointments" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "ScaleApplications" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "MedicationEntries" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "PatientDiagnoses" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "TreatmentPlans" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "PlanObjectives" o WHERE o."PlanId" = ANY(@plans)""",
        """SELECT count(*) FROM "FollowUpItems" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "PatientInvitations" WHERE "PatientId" = @pa""",
        """SELECT count(*) FROM "Notifications" WHERE "SubjectId" = @pa OR "SubjectId" = ANY(@notes)""",
    ];

    internal sealed record Record(
        Guid Provider, Guid Patient, Guid[] Notes, Guid[] Documents, Guid[] External, Guid[] Sessions, Guid[] Plans, string[] Keys);

    // An expired record (last record 2020-01-01, so the deadline was 2025-01-01) with one of everything.
    internal static async Task<Record> SeedExpiredAsync(RetentionScratch s, FakeNoteFileStore store, Guid? providerId = null, string first = "Ana")
    {
        var provider = providerId ?? await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, first, Ct);
        await s.ExecuteAsync(
            """UPDATE "Patients" SET "ContactEmail" = 'ana@example.test', "Phone" = '11999990000', "DateOfBirth" = '1990-05-04' WHERE "Id" = @pa""",
            Ct, ("pa", patient));

        var signed = await s.AddNoteAsync(provider, patient, new DateTimeOffset(2019, 6, 1, 15, 0, 0, TimeSpan.Zero), Ct,
            signedAt: new DateTimeOffset(2019, 6, 1, 15, 0, 0, TimeSpan.Zero));
        var draft = await s.AddNoteAsync(provider, patient, new DateTimeOffset(2019, 7, 1, 15, 0, 0, TimeSpan.Zero), Ct);
        var noteKey = $"notes/{provider}/{signed}.pdf";
        await s.ExecuteAsync(
            """UPDATE "Notes" SET "Status" = 'Exported', "PdfObjectKey" = @key, "PdfSha256" = repeat('a', 64), "ExportedAt" = "SignedAt" WHERE "Id" = @n""",
            Ct, ("n", signed), ("key", noteKey));
        await s.ExecuteAsync("""INSERT INTO "NoteSections" ("NoteId","Key","Content") VALUES (@n, 'evolucao', @m)""", Ct, ("n", signed), ("m", Marker));
        await s.AddSectionAsync(draft, Ct);
        await s.ExecuteAsync(
            """
            INSERT INTO "AuditEntries" ("NoteId","ActorId","Action","FieldChanged","PreviousValue","NewValue","At")
            VALUES (@n, @p, 'section_edited', 'evolucao', @m, @m, now());
            INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy","AddedById")
            SELECT @n, t."Id", 'human', @p FROM "ThemeTerms" t ORDER BY t."Code" LIMIT 1;
            INSERT INTO "ExcerptRefs" ("Id","SourceType","SourceId","DatedAt","ProviderId","SectionKey","Kind")
            VALUES (gen_random_uuid(), 'note', @n, now(), @p, 'evolucao', 'section');
            INSERT INTO "PatientQuotes" ("NoteId","Text","RecordedAt") VALUES (@n, @m, now());
            """, Ct, ("n", signed), ("p", provider), ("m", Marker));

        var document = await s.AddSignedDocumentAsync(provider, patient, new DateTimeOffset(2019, 12, 1, 15, 0, 0, TimeSpan.Zero), Ct);
        var documentKey = $"documents/{provider}/{document}.pdf";
        await s.ExecuteAsync(
            """
            UPDATE "ClinicalDocuments" SET "PdfObjectKey" = @key, "PdfSha256" = repeat('b', 64), "ExportedAt" = "SignedAt" WHERE "Id" = @d;
            INSERT INTO "AuditEntries" ("DocumentId","ActorId","Action","At") VALUES (@d, @p, 'document_signed', now());
            INSERT INTO "ConformityChecks" ("DocumentId","RulesetId","Passed")
            SELECT @d, "RulesetId", true FROM "ClinicalDocuments" WHERE "Id" = @d;
            """, Ct, ("d", document), ("key", documentKey), ("p", provider));

        var external = Guid.NewGuid();
        var externalKey = $"external/{provider}/{external}";
        await s.ExecuteAsync(
            """
            INSERT INTO "ExternalDocuments" ("Id","PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize","ScanStatus","Kind","ReadStatus")
            VALUES (@e, @pa, @p, 'laudo.pdf', 'application/pdf', @key, 10, 'clean', 'laudo', 'done');
            INSERT INTO "ExternalDocumentPages" ("ExternalDocumentId","ProviderId","PageNumber","Text") VALUES (@e, @p, 1, @m);
            INSERT INTO "ExtractedFacts" ("ExternalDocumentId","Kind","Value","ProviderId") VALUES (@e, 'medication', @m, @p);
            INSERT INTO "ExcerptRefs" ("Id","SourceType","SourceId","DatedAt","ProviderId","Kind","PageNumber")
            VALUES (gen_random_uuid(), 'document', @e, now(), @p, 'section', 1);
            """, Ct, ("e", external), ("pa", patient), ("p", provider), ("key", externalKey), ("m", Marker));

        var session = await s.AddSessionAsync(provider, patient, new DateTimeOffset(2020, 1, 1, 15, 0, 0, TimeSpan.Zero), Ct);
        await s.ExecuteAsync(
            """
            INSERT INTO "Transcripts" ("SessionId","NoteId","Text") VALUES (@s, @n, @m);
            UPDATE "Notes" SET "SessionId" = @s WHERE "Id" = @n;
            """, Ct, ("s", session), ("n", signed), ("m", Marker));
        // Different hours per patient: one provider's appointments never overlap.
        await s.AddAppointmentAsync(provider, patient, new DateTimeOffset(2020, 1, 1, first == "Ana" ? 15 : 17, 0, 0, TimeSpan.Zero), "attended", Ct);
        // A cancelled future appointment does not block the discard and is deleted with the rest.
        await s.AddAppointmentAsync(provider, patient, Now.AddDays(30), "cancelled", Ct);

        var plan = Guid.NewGuid();
        await s.ExecuteAsync(
            """
            INSERT INTO "PatientDiagnoses" ("PatientId","Description","IcdCode") VALUES (@pa, @m, 'F41.1');
            INSERT INTO "MedicationEntries" ("PatientId","Drug","StartedOn") VALUES (@pa, @m, '2019-06-01');
            INSERT INTO "TreatmentPlans" ("Id","PatientId") VALUES (@plan, @pa);
            INSERT INTO "PlanObjectives" ("PlanId","Description") VALUES (@plan, @m);
            INSERT INTO "ScaleApplications" ("PatientId","InstrumentId","Score","AppliedAt")
            SELECT @pa, i."Id", 5, '2019-06-01' FROM "Instruments" i ORDER BY i."Code" LIMIT 1;
            INSERT INTO "FollowUpItems" ("PatientId","NoteId","Description") VALUES (@pa, @n, @m);
            INSERT INTO "PatientInvitations" ("PatientId","Email","TokenHash","ExpiresAt") VALUES (@pa, 'ana@example.test', gen_random_uuid()::text::bytea, now() + interval '1 day');
            INSERT INTO "Notifications" ("ProviderId","Kind","SubjectType","SubjectId","Channel","ScheduledFor","DeliveryStatus","DedupeKey")
            VALUES (@p, 'retention_due', 'patient', @pa, 'in_app', now(), 'sent', 'retention:' || @pa || ':2025-01-01'),
                   (@p, 'unsigned_note', 'note', @draft, 'in_app', now(), 'sent', 'unsigned:' || @draft);
            """, Ct, ("pa", patient), ("p", provider), ("m", Marker), ("plan", plan), ("n", signed), ("draft", draft));

        foreach (var key in new[] { noteKey, documentKey, externalKey })
        {
            store.Objects[key] = [1, 2, 3];
        }
        return new Record(provider, patient, [signed, draft], [document], [external], [session], [plan], [noteKey, documentKey, externalKey]);
    }

    private static async Task<long> RemainingAsync(RetentionScratch s, Record r)
    {
        long total = 0;
        foreach (var sql in PatientRows)
        {
            total += await s.CountAsync(sql, Ct, ("pa", r.Patient), ("notes", r.Notes), ("docs", r.Documents), ("ext", r.External),
                ("sessions", r.Sessions), ("plans", r.Plans));
        }
        return total;
    }

    private static Task<DiscardResult> DiscardAsync(RetentionScratch s, FakeNoteFileStore store, Record r, string name, DateTimeOffset? now = null)
    {
        var db = s.CreateDbContext();
        return DiscardService.DiscardAsync(db, store, NullLogger.Instance, r.Provider, r.Patient, name, now ?? Now, Ct);
    }

    [Fact]
    public async Task DiscardAsync_ExpiredRecord_DeletesAllClinicalContent_KeepsPatientTermAndAudit()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        var other = await SeedExpiredAsync(s, store, r.Provider, "Bia");
        (await RemainingAsync(s, r)).Should().BeGreaterThan(20);

        // Act
        var result = await DiscardAsync(s, store, r, "Ana Silva");

        // Assert: rows
        result.Outcome.Should().Be(DiscardOutcome.Ok);
        (await RemainingAsync(s, r)).Should().Be(0);
        (await RemainingAsync(s, other)).Should().BeGreaterThan(20);

        // Objects: the patient's three are gone after commit, the other patient's stay; the term is stored by id.
        store.Deleted.Should().BeEquivalentTo(r.Keys);
        store.Objects.Keys.Should().Contain(other.Keys);
        await using var db = s.CreateDbContext();
        var term = await db.DisposalTerms.AsNoTracking().SingleAsync(t => t.PatientId == r.Patient, Ct);
        term.Id.Should().Be(result.TermId!.Value);
        term.ObjectKey.Should().Be($"disposal-terms/{r.Provider}/{term.Id}.pdf");
        store.Objects.Should().ContainKey(term.ObjectKey);
        System.Text.Encoding.ASCII.GetString(store.Objects[term.ObjectKey][..5]).Should().Be("%PDF-");
        term.PeriodFrom.Should().Be(new DateOnly(2019, 6, 1));
        term.PeriodTo.Should().Be(new DateOnly(2020, 1, 1));
        term.RetentionUntil.Should().Be(new DateOnly(2025, 1, 1));
        term.GuardYears.Should().Be(RetentionRule.DefaultGuardYears);
        term.Basis.Should().Be(RetentionRule.DefaultBasis);
        term.Counts.Should().Contain(new Dictionary<string, int>
        {
            ["notes"] = 2, ["clinicalDocuments"] = 1, ["externalDocuments"] = 1, ["sessions"] = 1, ["appointments"] = 2,
            ["transcripts"] = 1, ["scaleApplications"] = 1, ["medications"] = 1, ["diagnoses"] = 1, ["treatmentPlans"] = 1,
            ["planObjectives"] = 1, ["auditEntries"] = 2, ["notifications"] = 2, ["invitations"] = 1,
        });

        // The patient row keeps only the name and the discard date.
        var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == r.Patient, Ct);
        (patient.FirstName, patient.LastName).Should().Be(("Ana", "Silva"));
        patient.DiscardedAt.Should().Be(Now);
        patient.ContactEmail.Should().BeNull();
        patient.Phone.Should().BeNull();
        patient.DateOfBirth.Should().BeNull();
        patient.RetentionUntil.Should().BeNull();

        // Audit: ids and counts only.
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == DiscardService.AuditAction, Ct);
        audit.ActorId.Should().Be(r.Provider);
        audit.NewValue.Should().StartWith($"{r.Patient} {term.Id}").And.NotContain("Ana").And.NotContain(Marker);
        audit.PreviousValue.Should().BeNull();
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "PreviousValue" LIKE '%' || @m || '%' AND "NoteId" = ANY(@n)""", Ct,
            ("m", Marker), ("n", r.Notes))).Should().Be(0);
    }

    [Fact]
    public async Task DiscardAsync_BeforeExpiry_IsNotExpired_AndNothingChanges()
    {
        // Arrange: last record 2023, deadline 2028.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        await s.AddSessionAsync(r.Provider, r.Patient, new DateTimeOffset(2023, 3, 1, 15, 0, 0, TimeSpan.Zero), Ct);
        var before = await RemainingAsync(s, r);

        // Act
        var result = await DiscardAsync(s, store, r, "Ana Silva");

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.NotExpired);
        (await RemainingAsync(s, r)).Should().Be(before);
        store.Deleted.Should().BeEmpty();
        store.Puts.Should().Be(0);
    }

    [Fact]
    public async Task DiscardAsync_DeadlineDayItself_IsNotExpired()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);

        // Act: 2025-01-01 is the deadline, so that practice day is still within the guard.
        var result = await DiscardAsync(s, store, r, "Ana Silva", new DateTimeOffset(2025, 1, 1, 15, 0, 0, TimeSpan.Zero));

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.NotExpired);
    }

    [Fact]
    public async Task DiscardAsync_FutureAppointment_IsBlocked()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        await s.AddAppointmentAsync(r.Provider, r.Patient, Now.AddDays(3), "confirmed", Ct);

        // Act
        var result = await DiscardAsync(s, store, r, "Ana Silva");

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.FutureAppointment);
        (await s.CountAsync("""SELECT count(*) FROM "Notes" WHERE "PatientId" = @pa""", Ct, ("pa", r.Patient))).Should().Be(2);
    }

    [Fact]
    public async Task DiscardAsync_WrongName_IsNameMismatch()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);

        // Act
        var result = await DiscardAsync(s, store, r, "Ana Souza");

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.NameMismatch);
        (await s.CountAsync("""SELECT count(*) FROM "Notes" WHERE "PatientId" = @pa""", Ct, ("pa", r.Patient))).Should().Be(2);
    }

    [Theory]
    [InlineData("Ana Silva", true)]
    [InlineData("  ana   SILVA ", true)]
    [InlineData("Âná Sílva", true)]
    [InlineData("Ana", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NameMatches_TrimsAndIgnoresCaseAndAccents(string? typed, bool expected) =>
        DiscardService.NameMatches(typed, "Ana", "Silva").Should().Be(expected);

    [Fact]
    public async Task DiscardAsync_Twice_IsAlreadyDiscarded()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        (await DiscardAsync(s, store, r, "Ana Silva")).Outcome.Should().Be(DiscardOutcome.Ok);

        // Act
        var second = await DiscardAsync(s, store, r, "Ana Silva");

        // Assert
        second.Outcome.Should().Be(DiscardOutcome.AlreadyDiscarded);
        (await s.CountAsync("""SELECT count(*) FROM "DisposalTerms" WHERE "PatientId" = @pa""", Ct, ("pa", r.Patient))).Should().Be(1);
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "Action" = 'patient_discarded'""", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task DiscardAsync_OtherProvidersPatient_IsNotFound()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        var stranger = await s.AddProviderAsync(Ct);

        // Act
        var result = await DiscardAsync(s, store, r with { Provider = stranger }, "Ana Silva");

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.NotFound);
    }

    [Fact]
    public async Task DiscardAsync_StorageDown_RollsBack()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        var before = await RemainingAsync(s, r);
        store.Fail = true;

        // Act
        var result = await DiscardAsync(s, store, r, "Ana Silva");

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.StorageUnavailable);
        (await RemainingAsync(s, r)).Should().Be(before);
        (await s.CountAsync("""SELECT count(*) FROM "DisposalTerms" """, Ct)).Should().Be(0);
        (await s.CountAsync("""SELECT count(*) FROM "Patients" WHERE "Id" = @pa AND "DiscardedAt" IS NULL""", Ct, ("pa", r.Patient))).Should().Be(1);
        store.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscardAsync_ObjectDeleteFails_StillDiscards()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        store.FailDelete = true;

        // Act
        var result = await DiscardAsync(s, store, r, "Ana Silva");

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.Ok);
        (await RemainingAsync(s, r)).Should().Be(0);
        store.DeleteAttempts.Should().Be(r.Keys.Length * DiscardService.DeleteAttempts);
    }

    [Fact]
    public async Task TermPdfLinkAsync_IsScopedToTheProvider()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        var termId = (await DiscardAsync(s, store, r, "Ana Silva")).TermId!.Value;
        var stranger = await s.AddProviderAsync(Ct);
        await using var db = s.CreateDbContext();

        // Act
        var mine = await DisposalTerms.PdfLinkAsync(db, store, r.Provider, termId, Now, Ct);
        var theirs = await DisposalTerms.PdfLinkAsync(db, store, stranger, termId, Now, Ct);
        var list = await DisposalTerms.ListAsync(db, r.Provider, Ct);

        // Assert
        mine!.Url.ToString().Should().Contain($"disposal-terms/{r.Provider}/{termId}.pdf").And.NotContain("Ana");
        mine.ExpiresAt.Should().Be(Now + DisposalTerms.LinkLifetime);
        theirs.Should().BeNull();
        list.Should().ContainSingle().Which.Should().Match<DisposalTermItem>(t =>
            t.Id == termId && t.PatientId == r.Patient && t.PatientName == "Ana Silva" && t.Counts["notes"] == 2);
    }

    [Fact]
    public async Task DiscardAsync_AppointmentOnlyRecord_UsesTheClocksRecordDates()
    {
        // Arrange: the only record is an appointment marked attended (no session row), which the clock counts.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        // 01:30 UTC on 2 Mar is still 1 Mar in the practice.
        await s.AddAppointmentAsync(provider, patient, new DateTimeOffset(2020, 3, 2, 1, 30, 0, TimeSpan.Zero), "attended", Ct);
        await using var db = s.CreateDbContext();

        // Act
        var result = await DiscardService.DiscardAsync(db, store, NullLogger.Instance, provider, patient, "Ana Silva", Now, Ct);

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.Ok);
        await using var read = s.CreateDbContext();
        var term = await read.DisposalTerms.AsNoTracking().SingleAsync(t => t.PatientId == patient, Ct);
        (term.PeriodFrom, term.PeriodTo).Should().Be((new DateOnly(2020, 3, 1), new DateOnly(2020, 3, 1)));
        term.RetentionUntil.Should().Be(new DateOnly(2025, 3, 1));
        term.Counts["appointments"].Should().Be(1);
    }

    [Fact]
    public async Task PatientDelete_DiscardedRecord_IsNotFound_AndATermRowBlocksTheDeleteAsConflict()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        (await DiscardAsync(s, store, r, "Ana Silva")).Outcome.Should().Be(DiscardOutcome.Ok);
        // A record without clinical rows but with a term row (a discard committing between the check and the delete).
        var bare = await s.AddPatientAsync(r.Provider, "Bia", Ct);
        await s.ExecuteAsync(
            """
            INSERT INTO "DisposalTerms" ("ProviderId","PatientId","ObjectKey","PeriodFrom","PeriodTo","GuardYears","Basis","RetentionUntil")
            VALUES (@p, @pa, 'disposal-terms/x.pdf', '2019-01-01', '2019-01-01', 5, 'Res. CFP 001/2009', '2024-01-01')
            """, Ct, ("p", r.Provider), ("pa", bare));
        await using var db = s.CreateDbContext();

        // Act
        var discarded = await Anamnys.Server.Patients.PatientRecords.DeleteAsync(db, r.Provider, r.Patient, Ct);
        var withTerm = await Anamnys.Server.Patients.PatientRecords.DeleteAsync(db, r.Provider, bare, Ct);

        // Assert
        discarded.Should().Be(Anamnys.Server.Patients.RecordOutcome.NotFound);
        withTerm.Should().Be(Anamnys.Server.Patients.RecordOutcome.Conflict);
        (await s.CountAsync("""SELECT count(*) FROM "Patients" WHERE "Id" = ANY(@ids)""", Ct, ("ids", new[] { r.Patient, bare }))).Should().Be(2);
    }

    private const string InsertNote = """
        INSERT INTO "Notes" ("ProviderId","PatientId","Status","InputMode","Format") VALUES (@p, @pa, 'Draft', 'Text', 'DAP')
        """;

    [Fact]
    public async Task InsertAfterDiscard_IsRefusedByTheDatabase_AndTheEfPathSeesIt()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        (await DiscardAsync(s, store, r, "Ana Silva")).Outcome.Should().Be(DiscardOutcome.Ok);

        // Act
        var raw = () => s.ExecuteAsync(InsertNote, Ct, ("p", r.Provider), ("pa", r.Patient));
        await using var db = s.CreateDbContext();
        db.PatientDiagnoses.Add(new PatientDiagnosis { Id = Guid.NewGuid(), PatientId = r.Patient, Description = "x", RecordedAt = Now });
        var ef = () => db.SaveChangesAsync(Ct);

        // Assert
        (await raw.Should().ThrowAsync<Npgsql.PostgresException>()).Which.SqlState.Should().Be(DiscardedPatient.SqlState);
        DiscardedPatient.Is((await ef.Should().ThrowAsync<DbUpdateException>()).Which).Should().BeTrue();
        // Other patients of the provider are not affected.
        var other = await s.AddPatientAsync(r.Provider, "Bia", Ct);
        await s.ExecuteAsync(InsertNote, Ct, ("p", r.Provider), ("pa", other));
    }

    [Fact]
    public async Task InsertRacingAnOpenDiscard_WaitsAndIsRefusedOnceItCommits()
    {
        // Arrange: a discard in progress holds the patient row FOR UPDATE and has set DiscardedAt, not yet committed.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        await using var discard = await s.OpenAsync(Ct);
        await using var tx = await discard.BeginTransactionAsync(Ct);
        await using (var lockRow = new Npgsql.NpgsqlCommand(
            """SELECT 1 FROM "Patients" WHERE "Id" = @pa FOR UPDATE; UPDATE "Patients" SET "DiscardedAt" = now() WHERE "Id" = @pa""", discard, tx))
        {
            lockRow.Parameters.AddWithValue("pa", r.Patient);
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        // Act: the insert runs on another connection while the discard is open.
        await using var writer = await s.OpenAsync(Ct);
        await using var insert = new Npgsql.NpgsqlCommand(InsertNote, writer);
        insert.Parameters.AddWithValue("p", r.Provider);
        insert.Parameters.AddWithValue("pa", r.Patient);
        var pending = insert.ExecuteNonQueryAsync(Ct);
        var finishedEarly = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1), Ct)) == pending;
        await tx.CommitAsync(Ct);

        // Assert
        finishedEarly.Should().BeFalse("the insert waits for the discard's row lock");
        var act = () => pending;
        (await act.Should().ThrowAsync<Npgsql.PostgresException>()).Which.SqlState.Should().Be(DiscardedPatient.SqlState);
        (await s.CountAsync("""SELECT count(*) FROM "Notes" WHERE "PatientId" = @pa AND "Status" = 'Draft'""", Ct, ("pa", r.Patient)))
            .Should().Be(0);
    }

    [Fact]
    public async Task InsertRacingADiscardThatRollsBack_GoesThrough()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var r = await SeedExpiredAsync(s, store);
        await using var discard = await s.OpenAsync(Ct);
        await using var tx = await discard.BeginTransactionAsync(Ct);
        await using (var lockRow = new Npgsql.NpgsqlCommand(
            """SELECT 1 FROM "Patients" WHERE "Id" = @pa FOR UPDATE; UPDATE "Patients" SET "DiscardedAt" = now() WHERE "Id" = @pa""", discard, tx))
        {
            lockRow.Parameters.AddWithValue("pa", r.Patient);
            await lockRow.ExecuteNonQueryAsync(Ct);
        }
        await using var writer = await s.OpenAsync(Ct);
        await using var insert = new Npgsql.NpgsqlCommand(InsertNote, writer);
        insert.Parameters.AddWithValue("p", r.Provider);
        insert.Parameters.AddWithValue("pa", r.Patient);
        var pending = insert.ExecuteNonQueryAsync(Ct);

        // Act
        await tx.RollbackAsync(Ct);

        // Assert
        (await pending).Should().Be(1);
    }
}
