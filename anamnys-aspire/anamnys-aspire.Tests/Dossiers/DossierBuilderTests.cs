using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anamnys.Server.Documents;
using Anamnys.Server.Dossiers;
using Anamnys.Server.Integrity;
using Anamnys.Server.Notes;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Retention;
using FluentAssertions;
using QuestPDF.Infrastructure;
using static Anamnys.Tests.Integrity.IntegrityTestData;

namespace Anamnys.Tests.Dossiers;

// The dossier ZIP (design/specs/2026-11-04-patient-dossier-design.md §4, §5, §7: content, period, manifest, integrity).
[Collection(SharedAppHostCollection.Name)]
public class DossierBuilderTests(SharedAppHostFixture fixture)
{
    private const string Marker = "MARCADOR-SIGILOSO-d0551e";
    private const string OtherMarker = "OUTRO-PACIENTE-4b2f";
    private const string PatientName = "Benedita";
    private static readonly DateTimeOffset GeneratedAt = new(2026, 11, 4, 14, 0, 0, TimeSpan.Zero);
    private static readonly byte[] ExternalPdf = Encoding.ASCII.GetBytes("%PDF-1.4 exame externo");
    private static readonly byte[] LateExternalPdf = Encoding.ASCII.GetBytes("%PDF-1.4 fim do mes");
    private static readonly DateTimeOffset BoundaryInstant = new(2026, 11, 1, 2, 0, 0, TimeSpan.Zero);
    private const string LinkInPeriodRecipient = "Escola Aurora";
    private const string OutOfPeriodRecipient = "Dr. Fora do Periodo";
    private static readonly byte[] TermPng =[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DossierBuilderTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static readonly Dictionary<string, string> Atestado = new()
    {
        ["nome_solicitante"] = "Escola X",
        ["finalidade"] = "Afastamento",
        ["descricao_condicoes_psicologicas"] = "Linha um.",
        ["prazo_validade"] = "30 dias.",
    };

    private sealed record Scenario(
        RetentionScratch Db, FakeNoteFileStore Store, Guid Provider, Guid Patient, Guid NoteIn, Guid DocumentV1, Guid DocumentV2,
        long HeadSequence) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed record Built(Dictionary<string, byte[]> Entries, DossierBuildResult Result, List<object> Inputs)
    {
        public DossierPdfModel Model => Inputs.OfType<DossierPdfModel>().Single();
    }

    private static DateTimeOffset Oct(int day) => new(2026, 10, day, 14, 0, 0, TimeSpan.Zero);

    private async Task<Scenario> SeedAsync()
    {
        var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, PatientName, Ct);
        await s.ExecuteAsync("""UPDATE "Patients" SET "DateOfBirth" = '1985-03-02' WHERE "Id" = @p""", Ct, ("p", patient));
        var otherPatient = await s.AddPatientAsync(provider, "Outra", Ct);

        var noteIn = await SignedNoteAsync(s, provider, patient, Oct(10), Ct);
        await SignedNoteAsync(s, provider, patient, new DateTimeOffset(2026, 8, 1, 14, 0, 0, TimeSpan.Zero), Ct);
        var draft = await AddReadyNoteAsync(s, provider, patient, Ct);
        await s.ExecuteAsync(
            """
            UPDATE "Notes" SET "RawTranscript" = @m WHERE "PatientId" = @p;
            UPDATE "NoteSections" SET "Content" = @m WHERE "NoteId" = @d;
            INSERT INTO "AuditEntries" ("NoteId","ActorId","Action","FieldChanged","PreviousValue","NewValue","At")
            VALUES (@n, @prov, 'section_edited', 'data', @m, @m, now());
            """, Ct, ("m", Marker), ("p", patient), ("d", draft), ("n", noteIn), ("prov", provider));

        // Another patient of the same provider, sealed after the patient's records.
        var otherNote = await AddReadyNoteAsync(s, provider, otherPatient, Ct);
        await s.ExecuteAsync("""UPDATE "NoteSections" SET "Content" = @m WHERE "NoteId" = @n""", Ct, ("m", OtherMarker), ("n", otherNote));

        var v1 = await SignedDocumentAsync(s, provider, patient, Oct(12), Ct);
        Guid v2;
        await using (var db = s.CreateDbContext())
        {
            v2 = (await ClinicalDocumentService.CorrectAsync(db, provider, v1, Oct(13), Ct)).Id!.Value;
            await ClinicalDocumentService.SaveAsync(db, provider, v2, Atestado, ["informacao_alem_do_solicitado"], [], Oct(13), Ct);
            (await ClinicalDocumentService.SignAsync(db, provider, v2, false, Oct(13), Ct)).Outcome.Should().Be(DocumentOutcome.Ok);
            var draftDocument = (await ClinicalDocumentService.CreateAsync(db, provider, patient, "atestado", null, Oct(14), Ct)).Id!.Value;
            await ClinicalDocumentService.SaveAsync(
                db, provider, draftDocument, new Dictionary<string, string> { ["finalidade"] = Marker }, [], [], Oct(14), Ct);
            (await NoteEditing.SignAsync(db, provider, otherNote, Oct(15), Ct)).Should().Be(SignOutcome.Signed);
        }

        // A signed document whose correction is still a draft: not "substituído".
        var v3 = await SignedDocumentAsync(s, provider, patient, Oct(20), Ct);
        await using (var db = s.CreateDbContext())
        {
            (await ClinicalDocumentService.CorrectAsync(db, provider, v3, Oct(21), Ct)).Outcome.Should().Be(DocumentOutcome.Ok);
        }

        // External documents: clean in the period (its original name carries the patient's name), blocked, failed, pending,
        // clean at 31/10 23:00 in São Paulo (already 01/11 in UTC), clean outside the period, deleted, another patient's.
        async Task ExternalAsync(Guid owner, string name, string status, DateTimeOffset at, byte[]? bytes, bool deleted = false)
        {
            var id = Guid.NewGuid();
            var key = bytes is null ? null : $"external/{provider}/{id}";
            if (key is not null)
            {
                store.Objects[key] = bytes!;
            }
            await s.ExecuteAsync(
                """
                INSERT INTO "ExternalDocuments" ("Id","PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize","ScanStatus","UploadedAt","Kind","DeletedAt")
                VALUES (@id, @pa, @p, @name, 'application/pdf', @key, 10, @status, @at, 'exame', CASE WHEN @deleted THEN now() END)
                """, Ct, ("id", id), ("pa", owner), ("p", provider), ("name", name), ("key", (object?)key ?? DBNull.Value),
                ("status", status), ("at", at.ToUniversalTime()), ("deleted", deleted));
        }
        await ExternalAsync(patient, $"Exame de sangue {PatientName} Silva (março).pdf", "clean", Oct(11), ExternalPdf);
        await ExternalAsync(patient, "bloqueado.pdf", "infected", Oct(16), null);
        await ExternalAsync(patient, "falhou.pdf", "failed", Oct(19), ExternalPdf);
        await ExternalAsync(patient, "fim-do-mes.pdf", "clean", BoundaryInstant, LateExternalPdf);
        await ExternalAsync(patient, "pendente.pdf", "pending", Oct(17), ExternalPdf);
        await ExternalAsync(patient, "antigo.pdf", "clean", new DateTimeOffset(2026, 8, 5, 14, 0, 0, TimeSpan.Zero), ExternalPdf);
        await ExternalAsync(patient, "excluido.pdf", "clean", Oct(18), ExternalPdf, deleted: true);
        await ExternalAsync(otherPatient, $"{OtherMarker}.pdf", "clean", Oct(11), Encoding.ASCII.GetBytes($"%PDF {OtherMarker}"));

        // A disclosure of the first version with a term and a link opened twice; the hashes carry the marker.
        var authorization = Guid.NewGuid();
        var link = Guid.NewGuid();
        store.Objects[$"disclosures/{provider}/{authorization}"] = TermPng;
        await s.ExecuteAsync(
            """
            INSERT INTO "DisclosureAuthorizations" ("Id","DocumentId","PatientId","RecipientName","RecipientRole","Purpose","AuthorizedAt",
                                                    "RecordedBy","ProviderId","ObtainedVia","TermObjectKey","ConfirmedAt")
            VALUES (@a, @d, @pa, 'Dr. Pedro Lima', 'Psiquiatra', 'Continuidade do cuidado', @at, @p, @p, 'signed_term', @key, @at);
            INSERT INTO "ShareLinks" ("Id","DocumentId","AuthorizationId","TokenHash","PasswordHash","ExpiresAt","CreatedAt","ViewsUsed")
            VALUES (@l, @d, @a, @m, @m, @at + interval '7 days', @at, 1);
            INSERT INTO "ShareAccesses" ("ShareLinkId","At","Outcome","UserAgent") VALUES
            (@l, @at + interval '1 hour', 'bad_password', 'Mozilla/5.0 Teste'),
            (@l, @at + interval '2 hours', 'opened', 'Mozilla/5.0 Teste');
            """, Ct, ("a", authorization), ("d", v1), ("pa", patient), ("p", provider), ("at", Oct(14).ToUniversalTime()),
            ("key", $"disclosures/{provider}/{authorization}"), ("l", link), ("m", Marker));

        // Two September authorizations: one with a link created in October (in the dossier), one with nothing in October.
        await s.ExecuteAsync(
            """
            INSERT INTO "DisclosureAuthorizations" ("Id","DocumentId","PatientId","RecipientName","RecipientRole","Purpose","AuthorizedAt",
                                                    "RecordedBy","ProviderId","ObtainedVia","ConfirmedAt")
            VALUES (@a1, @d, @pa, @r1, 'Escola', 'Relatório escolar', @sep, @p, @p, 'in_writing', @sep),
                   (@a2, @d, @pa, @r2, 'Advogado', 'Processo', @sep, @p, @p, 'verbal_session', @sep);
            INSERT INTO "ShareLinks" ("Id","DocumentId","AuthorizationId","TokenHash","PasswordHash","ExpiresAt","CreatedAt")
            VALUES (gen_random_uuid(), @d, @a1, gen_random_uuid()::text, 'x', @oct + interval '7 days', @oct),
                   (@l2, @d, @a2, gen_random_uuid()::text, 'x', @sep + interval '7 days', @sep);
            INSERT INTO "ShareAccesses" ("ShareLinkId","At","Outcome") VALUES (@l2, @sep + interval '1 day', 'opened');
            """, Ct, ("a1", Guid.NewGuid()), ("a2", Guid.NewGuid()), ("d", v1), ("pa", patient), ("p", provider),
            ("r1", LinkInPeriodRecipient), ("r2", OutOfPeriodRecipient), ("l2", Guid.NewGuid()),
            ("sep", new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero)), ("oct", Oct(20).ToUniversalTime()));

        // Another provider's patient on the same day.
        var other = await s.AddProviderAsync(Ct, withCrp: true);
        var stranger = await s.AddPatientAsync(other, OtherMarker, Ct);
        await SignedNoteAsync(s, other, stranger, Oct(10), Ct);

        await using (var db = s.CreateDbContext())
        {
            (await IntegrityAnchoring.AnchorAsync(db, provider, Tsa, Ct)).Should().NotBeNull();
        }
        var head = (await SealsAsync(s, provider, Ct)).Max(x => x.Sequence);
        return new Scenario(s, store, provider, patient, noteIn, v1, v2, head);
    }

    private static DossierRequest Request(Scenario s, DateOnly? from, DateOnly? to) =>
        new(Guid.NewGuid(), 7, s.Provider, s.Patient, "other", "Pedido da escola", from, to, GeneratedAt);

    private static readonly DateOnly October1 = new(2026, 10, 1);
    private static readonly DateOnly October31 = new(2026, 10, 31);

    private static async Task<Built> BuildAsync(Scenario s, DossierRequest request)
    {
        await using var db = s.Db.CreateDbContext();
        var inputs = new List<object>();
        using var zip = new MemoryStream();
        var result = await DossierBuilder.BuildAsync(db, s.Store, Tsa, request, zip, inputs.Add, Ct);
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var entries = new Dictionary<string, byte[]>();
        foreach (var entry in archive.Entries)
        {
            await using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, Ct);
            entries[entry.FullName] = buffer.ToArray();
        }
        return new Built(entries, result, inputs);
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public async Task BuildAsync_Period_FollowsLayoutAndLeavesOutsideItemsOut()
    {
        // Arrange
        await using var s = await SeedAsync();

        // Act
        var built = await BuildAsync(s, Request(s, October1, October31));

        // Assert
        built.Entries.Keys.Should().BeEquivalentTo(
            "dossie.pdf", "manifesto.json", "LEIA-ME.txt",
            "notas/2026-10-10-nota.pdf",
            "documentos/2026-10-12-atestado-substituido.pdf",
            "documentos/2026-10-13-atestado.pdf",
            "documentos/2026-10-20-atestado.pdf",
            "externos/2026-10-11-documento-externo.pdf",
            "externos/2026-10-31-documento-externo.pdf",
            "divulgacoes/2026-10-14-termo.png",
            "integridade/comprovante.pdf",
            $"integridade/carimbos/{s.HeadSequence}.tsr");
        built.Entries["externos/2026-10-11-documento-externo.pdf"].Should().Equal(ExternalPdf);
        built.Entries["externos/2026-10-31-documento-externo.pdf"].Should().Equal(LateExternalPdf);
        built.Entries["divulgacoes/2026-10-14-termo.png"].Should().Equal(TermPng);
        Encoding.ASCII.GetString(built.Entries["dossie.pdf"], 0, 4).Should().Be("%PDF");
        built.Model.PeriodFrom.Should().Be(October1);
        built.Model.Reason.Should().Be("Outro: Pedido da escola");
        string FileOf(string original) => built.Model.Summary.Single(r => r.Title.EndsWith(original)).File;
        FileOf("bloqueado.pdf").Should().Be("arquivo não incluído: bloqueado");
        FileOf("falhou.pdf").Should().Be("arquivo não incluído: verificação falhou");
        FileOf("pendente.pdf").Should().Be("arquivo não incluído: pendente de verificação");
        FileOf("fim-do-mes.pdf").Should().Be("externos/2026-10-31-documento-externo.pdf");
        built.Model.Summary.Should().NotContain(r => r.Title.Contains("antigo") || r.Title.Contains("excluido"));
    }

    // A disclosure enters when its authorization, a link (created, revoked, expiring) or an access falls in the period.
    [Fact]
    public async Task BuildAsync_DisclosurePeriod_IncludesOldAuthorizationWithLinkInPeriodOnly()
    {
        // Arrange
        await using var s = await SeedAsync();

        // Act
        var built = await BuildAsync(s, Request(s, October1, October31));

        // Assert
        built.Model.Disclosures.Select(d => d.Recipient).Should().BeEquivalentTo("Dr. Pedro Lima", LinkInPeriodRecipient);
        var old = built.Model.Disclosures.Single(d => d.Recipient == LinkInPeriodRecipient);
        old.Links.Should().ContainSingle();
        old.TermFile.Should().Be("nenhum termo anexado");
        built.Model.Summary.Should().NotContain(r => r.Title.Contains(OutOfPeriodRecipient));
        (await BuildAsync(s, Request(s, null, null))).Model.Disclosures.Should().HaveCount(3);
    }

    [Fact]
    public async Task BuildAsync_NoPeriod_IncludesWholeHistory()
    {
        // Arrange
        await using var s = await SeedAsync();

        // Act
        var built = await BuildAsync(s, Request(s, null, null));

        // Assert
        built.Entries.Keys.Should().Contain(["notas/2026-08-01-nota.pdf", "notas/2026-10-10-nota.pdf", "externos/2026-08-05-documento-externo.pdf"]);
        built.Model.Integrity.Should().HaveCount(5);
        DossierPdf.PeriodText(null, null).Should().Be("histórico completo");
    }

    // Rendered PDFs are compressed, so they are covered at their input (like the account export test); every entry is
    // still scanned raw.
    [Fact]
    public async Task BuildAsync_DraftsTranscriptAuditHashesAndOtherPatients_NeverAppear()
    {
        // Arrange
        await using var s = await SeedAsync();

        // Act
        var built = await BuildAsync(s, Request(s, null, null));

        // Assert
        foreach (var (name, bytes) in built.Entries)
        {
            Encoding.Latin1.GetString(bytes).Should().NotContain(Marker, name).And.NotContain(OtherMarker, name);
            Text(bytes).Should().NotContain(Marker, name).And.NotContain(OtherMarker, name);
            name.Should().NotContain(PatientName);
        }
        var inputs = built.Inputs.Where(i => i is not DossierPdfModel).ToList();
        inputs.Should().Contain(i => i is NotePdfModel).And.Contain(i => i is DocumentPdfModel).And.Contain(i => i is IntegritySubjectsReportModel);
        JsonSerializer.Serialize(inputs).Should().NotContain(Marker).And.NotContain(OtherMarker);
        JsonSerializer.Serialize(built.Model).Should().NotContain(Marker).And.NotContain(OtherMarker);
        Text(built.Entries["manifesto.json"]).Should().NotContain(PatientName).And.NotContain("Silva");
        // The patient-named original file name stays inside dossie.pdf only.
        built.Model.Summary.Should().Contain(r => r.Title.Contains($"{PatientName} Silva") && r.File.StartsWith("externos/"));
        built.Entries.Keys.Should().NotContain(k => k.Contains(PatientName, StringComparison.OrdinalIgnoreCase)
            || k.Contains("Silva", StringComparison.OrdinalIgnoreCase) || k.Contains("sangue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_Manifest_ListsEveryOtherFileWithItsHash()
    {
        // Arrange
        await using var s = await SeedAsync();
        var request = Request(s, October1, October31);

        // Act
        var built = await BuildAsync(s, request);

        // Assert
        var json = JsonDocument.Parse(Text(built.Entries["manifesto.json"])).RootElement;
        json.GetProperty("v").GetInt32().Should().Be(1);
        json.GetProperty("dossierId").GetGuid().Should().Be(request.DossierId);
        json.GetProperty("number").GetInt32().Should().Be(7);
        json.GetProperty("patientId").GetGuid().Should().Be(s.Patient);
        json.GetProperty("reason").GetString().Should().Be("other");
        json.GetProperty("provider").GetProperty("crp").GetString().Should().Be("123456/06");
        json.GetProperty("period").GetProperty("from").GetString().Should().Be("2026-10-01");
        json.GetProperty("period").GetProperty("to").GetString().Should().Be("2026-10-31");
        json.GetProperty("generatedAt").GetString().Should().Be("2026-11-04T11:00:00-03:00");
        var files = json.GetProperty("files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("path").GetString()!, f => (Sha: f.GetProperty("sha256").GetString(), Bytes: f.GetProperty("bytes").GetInt64()));
        files.Keys.Should().BeEquivalentTo(built.Entries.Keys.Where(k => k != "manifesto.json"));
        foreach (var (path, file) in files)
        {
            file.Sha.Should().Be(Convert.ToHexStringLower(SHA256.HashData(built.Entries[path])), path);
            file.Bytes.Should().Be(built.Entries[path].LongLength, path);
        }
        built.Result.Manifest.Files.Select(f => f.Path).Should().Equal(json.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
        DossierManifest.FromJson(Text(built.Entries["manifesto.json"]))!.Files.Should().BeEquivalentTo(built.Result.Manifest.Files);
    }

    [Fact]
    public async Task BuildAsync_Model_CarriesDisclosuresComplianceRetentionAndIntegrity()
    {
        // Arrange
        await using var s = await SeedAsync();

        // Act
        var built = await BuildAsync(s, Request(s, October1, October31));

        // Assert
        var m = built.Model;
        m.PatientName.Should().Be($"{PatientName} Silva");
        m.PatientBirthDate.Should().Be(new DateOnly(1985, 3, 2));
        var disclosure = m.Disclosures.Single(d => d.Recipient == "Dr. Pedro Lima");
        disclosure.ObtainedVia.Should().Be("termo assinado");
        disclosure.TermFile.Should().Be("divulgacoes/2026-10-14-termo.png");
        disclosure.Document.Should().EndWith("(substituído)");
        var link = disclosure.Links.Should().ContainSingle().Subject;
        link.Accesses.Select(a => a.Outcome).Should().Equal("senha incorreta", "aberto");
        link.Accesses.Should().OnlyContain(a => a.UserAgent == "Mozilla/5.0 Teste");
        m.Compliance.Should().HaveCount(3).And.OnlyContain(c => c.Status.StartsWith("conforme"));
        // Only v1 was replaced by a signed correction; v3's correction is still a draft.
        m.Compliance.Should().ContainSingle(c => c.Document.EndsWith("(substituído)"))
            .Which.Document.Should().Contain(DocumentPdfModel.CodeFor(s.DocumentV1));
        m.Retention.RetentionUntil.Should().NotBeNull();
        m.Retention.ArchivedAt.Should().BeNull();
        m.Retention.HasDisposalTerm.Should().BeFalse();
        m.IntegrityVerified.Should().BeTrue();
        m.IntegrityProblems.Should().BeEmpty();
        m.Integrity.Should().HaveCount(4).And.OnlyContain(i => i.Status == "íntegro" && i.Anchored && i.Sequence != null);
        built.Result.IntegrityVerified.Should().BeTrue();

        var anchor = m.Anchors.Should().ContainSingle().Subject;
        anchor.File.Should().Be($"integridade/carimbos/{s.HeadSequence}.tsr");
        var heads = await SealsAsync(s.Db, s.Provider, Ct);
        Tsa.Verify(built.Entries[anchor.File], heads.Single(x => x.Sequence == s.HeadSequence).Hash).Should().NotBeNull();
        var report = built.Inputs.OfType<IntegritySubjectsReportModel>().Single();
        report.Seals.Should().HaveCount(4).And.OnlyContain(x => x.Status == SubjectStatus.Intact);
        report.Anchors.Should().ContainSingle(a => a.Sequence == s.HeadSequence);
        var readme = Text(built.Entries["LEIA-ME.txt"]);
        readme.Should().Contain("sha256sum -c").And.Contain("openssl ts -verify -digest").And.Contain($"carimbos/{s.HeadSequence}.tsr");
        readme.Should().Contain("Rascunhos").And.Contain("comprovante de entrega");
        foreach (var (name, bytes) in built.Entries.Where(e => !e.Key.EndsWith(".pdf")))
        {
            (name + Text(bytes)).Should().NotContainEquivalentOf("cofre", name);
        }
        JsonSerializer.Serialize(m).Should().NotContainEquivalentOf("cofre");
    }

    [Fact]
    public async Task BuildAsync_TamperedNote_IsNotVerifiedAndTheProblemIsOnTheCover()
    {
        // Arrange
        await using var s = await SeedAsync();
        await s.Db.ExecuteAsync("""UPDATE "NoteSections" SET "Content" = 'alterado' WHERE "NoteId" = @n AND "Key" = 'data'""", Ct, ("n", s.NoteIn));

        // Act
        var built = await BuildAsync(s, Request(s, October1, October31));

        // Assert
        built.Result.IntegrityVerified.Should().BeFalse();
        built.Model.IntegrityVerified.Should().BeFalse();
        built.Model.IntegrityProblems.Should().ContainSingle().Which.Should().StartWith($"Nota {NotePdfModel.CodeFor(s.NoteIn)}")
            .And.Contain("conteúdo difere do selado");
        built.Inputs.OfType<IntegritySubjectsReportModel>().Single().Seals
            .Should().ContainSingle(x => x.Status == SubjectStatus.ContentChanged);
    }

    [Fact]
    public void FileNames_SanitizeAndNumberCollisions()
    {
        DossierFileNames.Sanitize("Exame de sangue (março).pdf").Should().Be("Exame-de-sangue-marco");
        DossierFileNames.Sanitize("  ***.pdf").Should().Be("arquivo");
        DossierFileNames.Sanitize(new string('a', 80) + ".png").Should().HaveLength(DossierFileNames.MaxNameLength);
        DossierFileNames.Sanitize("../../etc/passwd").Should().Be("passwd");
        DossierFileNames.Extension("foto.JPEG", "image/jpeg").Should().Be("jpeg");
        DossierFileNames.Extension("sem-extensao", "image/png").Should().Be("png");

        var names = new DossierFileNames();
        names.Reserve("notas/2026-10-10-nota", "pdf").Should().Be("notas/2026-10-10-nota.pdf");
        names.Reserve("notas/2026-10-10-nota", "pdf").Should().Be("notas/2026-10-10-nota-2.pdf");
        names.Reserve("documentos/d", "pdf", "-substituido").Should().Be("documentos/d-substituido.pdf");
        names.Reserve("documentos/d", "pdf", "-substituido").Should().Be("documentos/d-2-substituido.pdf");
        names.Reserve("externos/A", "pdf").Should().Be("externos/A.pdf");
        names.Reserve("externos/a", "pdf").Should().Be("externos/a-2.pdf");
    }
}
