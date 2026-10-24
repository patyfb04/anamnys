using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Anamnys.Server.Documents;
using Anamnys.Server.Exports;
using Anamnys.Server.Patients;
using Anamnys.Server.Scales;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using QuestPDF.Infrastructure;

namespace Anamnys.Tests.Exports;

[Collection(SharedAppHostCollection.Name)]
public class AccountExportBuilderTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 0, 0, TimeSpan.Zero);
    private const string Root = "anamnys-exportacao-2026-10-23";
    private const string Marker = "MARCADOR-SIGILOSO-7f3a";
    private const string OtherMarker = "OUTRO-PROFISSIONAL-9c1e";
    private static readonly byte[] StoredPdf = Encoding.ASCII.GetBytes("%PDF-1.7 stored copy");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static AccountExportBuilderTests() => QuestPDF.Settings.License = LicenseType.Community;

    private sealed record Account(
        PatientSearchSeed Seed, FakeNoteFileStore Store, Guid SignedNoteId, Guid DraftNoteId, Guid ExportedNoteId,
        Guid SupersededDocumentId, Guid CorrectionDocumentId, Guid AnaId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Seed.DisposeAsync();
    }

    // Three patients of the provider (one archived, one with an accented name) and one of another
    // provider. The marker is stored where the export must never read: RawTranscript and audit values.
    private async Task<Account> SeedAccountAsync()
    {
        var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var provider = seed.ProviderId;
        await seed.ExecuteSqlAsync(
            """UPDATE "Providers" SET "CrpNumber" = '12345', "CrpRegion" = '06', "PracticeCity" = 'São Paulo' WHERE "Id" = @id""",
            Ct, ("id", provider));

        await using var db = seed.CreateDbContext();
        var ana = await PatientRecords.CreateAsync(db, provider, new CreatePatientRequest(
            "Ana", "Souza", "ana@example.test", new DateOnly(1990, 5, 4),
            [new DiagnosisInput("Transtorno de ansiedade generalizada", "F41.1", null)],
            [new MedicationInput("Sertralina", "50 mg", "1x ao dia", new DateOnly(2026, 1, 10), null)],
            ["Reduzir a evitação social"]), Now.AddDays(-60), Ct);
        await seed.AddPatientAsync("Álvaro", "Castro", cancellationToken: Ct);
        await seed.AddPatientAsync("Bruno", "Lima", archivedAt: Now.AddDays(-5), cancellationToken: Ct);
        await seed.AddAppointmentAsync(ana, Now.AddDays(-7), "attended", cancellationToken: Ct);
        await seed.AddAppointmentAsync(ana, Now.AddDays(7), cancellationToken: Ct);

        var signed = await AddNoteAsync(seed, ana, "Signed", Now.AddDays(-7));
        var draft = await AddNoteAsync(seed, ana, "ReadyForReview", Now.AddDays(-1));
        var exported = await AddNoteAsync(seed, ana, "Exported", Now.AddDays(-14));
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "AuditEntries" ("NoteId", "ActorId", "Action", "FieldChanged", "PreviousValue", "NewValue", "At")
            VALUES (@note, @provider, 'section_edited', 'data', @marker, @marker, @at)
            """,
            Ct, ("note", signed), ("provider", provider), ("marker", Marker), ("at", Now.AddDays(-7)));
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "ThemeTags" ("NoteId", "TermId", "CreatedBy", "AddedById")
            SELECT @note, t."Id", 'human', @provider
            FROM "ThemeTerms" t JOIN "ThemeVocabularies" v ON v."Id" = t."VocabularyId"
            WHERE v."Active" AND t."Status" = 'active'
            ORDER BY t."Code" LIMIT 1
            """,
            Ct, ("note", signed), ("provider", provider));

        var phq = await db.Instruments.AsNoTracking().SingleAsync(i => i.Code == "PHQ-9", Ct);
        (await ScaleRecording.RecordAsync(db, provider, ana, new RecordScaleRequest(phq.Id, 12, Now.AddDays(-7), null), Now, Ct))
            .Outcome.Should().Be(Server.Scales.RecordOutcome.Recorded);

        var created = await ClinicalDocumentService.CreateAsync(db, provider, ana, "declaracao", null, Now.AddDays(-3), Ct);
        var documentId = created.Id!.Value;
        await ClinicalDocumentService.SaveAsync(
            db, provider, documentId,
            new Dictionary<string, string>
            {
                ["finalidade"] = "Comprovar acompanhamento.",
                ["local_dias_horarios_e_duracao_do_acompanhamento"] = "Consultório, às terças.",
                ["prazo_validade"] = "30 dias.",
            },
            ["sintomas_situacoes_ou_estados_psicologicos"], [], Now.AddDays(-3), Ct);
        (await ClinicalDocumentService.SignAsync(db, provider, documentId, false, Now.AddDays(-3), Ct)).Outcome.Should().Be(DocumentOutcome.Ok);
        var correction = await ClinicalDocumentService.CorrectAsync(db, provider, documentId, Now.AddDays(-2), Ct);
        correction.Outcome.Should().Be(DocumentOutcome.Ok);

        var other = await seed.AddProviderAsync(Ct);
        var zeca = await seed.AddPatientAsync("Zeca", OtherMarker, providerId: other, cancellationToken: Ct);
        await AddNoteAsync(seed, zeca, "Signed", Now.AddDays(-7), other);

        var store = new FakeNoteFileStore();
        store.Objects[$"notes/test/{exported}.pdf"] = StoredPdf;
        return new Account(seed, store, signed, draft, exported, documentId, correction.Id!.Value, ana);
    }

    private static async Task<Guid> AddNoteAsync(PatientSearchSeed seed, Guid patientId, string status, DateTimeOffset createdAt, Guid? providerId = null)
    {
        var id = Guid.NewGuid();
        var signedStatus = status is "Signed" or "Exported";
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "Notes" ("Id", "ProviderId", "PatientId", "Status", "InputMode", "Format", "RawTranscript",
                                 "SignedAt", "SignedByCrp", "CreatedAt", "UpdatedAt", "PdfObjectKey", "PdfSha256", "ExportedAt")
            VALUES (@id, @provider, @patient, @status, 'Text', 'DAP', @raw,
                    CASE WHEN @signed THEN @created END, CASE WHEN @signed THEN '12345/06' END, @created, @created,
                    CASE WHEN @status = 'Exported' THEN 'notes/test/' || @id || '.pdf' END,
                    CASE WHEN @status = 'Exported' THEN repeat('a', 64) END,
                    CASE WHEN @status = 'Exported' THEN @created END);
            INSERT INTO "NoteSections" ("NoteId", "Key", "Content", "Position")
            VALUES (@id, 'data', @data, 0), (@id, 'assessment', 'Humor estável.', 1), (@id, 'plan', 'Manter frequência semanal.', 2);
            """,
            Ct, ("id", id), ("provider", providerId ?? seed.ProviderId), ("patient", patientId), ("status", status),
            ("raw", $"Transcrição original {Marker} {OtherMarker}"), ("signed", signedStatus), ("created", createdAt.ToUniversalTime()),
            ("data", providerId is null ? "Paciente relata melhora do sono." : $"Relato {OtherMarker}"));
        return id;
    }

    private static async Task<Dictionary<string, byte[]>> BuildAsync(Account account)
    {
        await using var db = account.Seed.CreateDbContext();
        var builder = new AccountExportBuilder(db, account.Store, new FakeTimeProvider(Now));
        using var zip = new MemoryStream();
        await builder.BuildAsync(account.Seed.ProviderId, zip, Ct);
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var entries = new Dictionary<string, byte[]>();
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, Ct);
            entries[entry.FullName] = buffer.ToArray();
        }
        return entries;
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static JsonElement Json(byte[] bytes) => JsonDocument.Parse(Text(bytes).TrimStart('﻿')).RootElement;

    [Fact]
    public async Task BuildAsync_SeededAccount_FollowsSpecLayout()
    {
        // Arrange
        await using var account = await SeedAccountAsync();

        // Act
        var entries = await BuildAsync(account);

        // Assert
        var patient = $"{Root}/pacientes/002-AS";
        string[] perPatient = ["paciente.html", "paciente.json", "notas/notas.json", "notas/notas.csv", "documentos/documentos.json",
            "escalas.csv", "diagnosticos.csv", "medicacoes.csv", "objetivos.csv", "temas.csv"];
        var expected = new List<string>
        {
            $"{Root}/index.html", $"{Root}/LEIA-ME.txt", $"{Root}/conta/perfil.json", $"{Root}/conta/agenda.csv", $"{Root}/conta/auditoria.csv",
            $"{patient}/notas/2026-10-09-assinada.pdf", $"{patient}/notas/2026-10-16-assinada.pdf",
            $"{patient}/documentos/2026-10-20-declaracao-substituido.pdf",
        };
        foreach (var folder in new[] { "001-AC", "002-AS", "003-BL" })
        {
            expected.AddRange(perPatient.Select(f => $"{Root}/pacientes/{folder}/{f}"));
        }
        entries.Keys.Should().BeEquivalentTo(expected);
        Text(entries[$"{Root}/index.html"]).Should().Contain("pacientes/002-AS/paciente.html").And.Contain("Ana Souza");
        var readme = Text(entries[$"{Root}/LEIA-ME.txt"]);
        foreach (var name in new[] { "index.html", "perfil.json", "agenda.csv", "auditoria.csv", "paciente.html", "paciente.json",
                     "notas.json", "notas.csv", "documentos.json", "escalas.csv", "diagnosticos.csv", "medicacoes.csv", "objetivos.csv", "temas.csv" })
        {
            readme.Should().Contain(name);
        }
        var json = Json(entries[$"{patient}/paciente.json"]);
        json.GetProperty("diagnosticos")[0].GetProperty("cid").GetString().Should().Be("F41.1");
        json.GetProperty("medicacoes")[0].GetProperty("medicamento").GetString().Should().Be("Sertralina");
        json.GetProperty("planos")[0].GetProperty("objetivos")[0].GetProperty("descricao").GetString().Should().Be("Reduzir a evitação social");
        json.GetProperty("escalas")[0].GetProperty("pontuacao").GetInt32().Should().Be(12);
        json.GetProperty("notas").EnumerateArray().Single(n => n.GetProperty("id").GetGuid() == account.SignedNoteId)
            .GetProperty("temas").GetArrayLength().Should().Be(1);
        Text(entries[$"{patient}/temas.csv"]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
        Text(entries[$"{Root}/conta/agenda.csv"]).Should().Contain("002-AS;").And.NotContain("Ana");
    }

    [Fact]
    public async Task BuildAsync_SecondProviderData_NeverExported()
    {
        // Arrange
        await using var account = await SeedAccountAsync();

        // Act
        var entries = await BuildAsync(account);

        // Assert
        entries.Keys.Select(k => k.Split('/')).Where(p => p.Length > 2 && p[1] == "pacientes").Select(p => p[2]).Distinct()
            .Should().BeEquivalentTo("001-AC", "002-AS", "003-BL");
        foreach (var (name, bytes) in entries)
        {
            Encoding.Latin1.GetString(bytes).Should().NotContain(OtherMarker, name);
            Text(bytes).Should().NotContain(OtherMarker, name);
        }
        var audit = Text(entries[$"{Root}/conta/auditoria.csv"]);
        audit.Should().Contain(account.SignedNoteId.ToString()).And.Contain("document_corrected");
    }

    [Fact]
    public async Task BuildAsync_ArchivedPatient_IsIncludedAndMarked()
    {
        // Arrange
        await using var account = await SeedAccountAsync();

        // Act
        var entries = await BuildAsync(account);

        // Assert
        var json = Json(entries[$"{Root}/pacientes/003-BL/paciente.json"]);
        json.GetProperty("arquivado").GetBoolean().Should().BeTrue();
        json.GetProperty("nome").GetString().Should().Be("Bruno");
        Text(entries[$"{Root}/index.html"]).Should().Contain("Arquivado");
    }

    [Fact]
    public async Task BuildAsync_DraftNoteAndSupersededDocument_AreMarked()
    {
        // Arrange
        await using var account = await SeedAccountAsync();

        // Act
        var entries = await BuildAsync(account);

        // Assert
        var folder = $"{Root}/pacientes/002-AS";
        var notes = Json(entries[$"{folder}/notas/notas.json"]).EnumerateArray().ToDictionary(n => n.GetProperty("id").GetGuid());
        notes[account.DraftNoteId].GetProperty("situacao").GetString().Should().Be("rascunho");
        notes[account.DraftNoteId].GetProperty("pdf").ValueKind.Should().Be(JsonValueKind.Null);
        notes[account.SignedNoteId].GetProperty("situacao").GetString().Should().Be("assinada");
        Text(entries[$"{folder}/notas/notas.csv"]).Should().Contain(";rascunho;");
        var documents = Json(entries[$"{folder}/documentos/documentos.json"]).EnumerateArray().ToDictionary(d => d.GetProperty("id").GetGuid());
        documents[account.SupersededDocumentId].GetProperty("situacao").GetString().Should().Be("substituido");
        documents[account.SupersededDocumentId].GetProperty("substituidoPor").GetGuid().Should().Be(account.CorrectionDocumentId);
        documents[account.SupersededDocumentId].GetProperty("pdf").GetString().Should().EndWith("-substituido.pdf");
        documents[account.CorrectionDocumentId].GetProperty("situacao").GetString().Should().Be("rascunho");
        var html = Text(entries[$"{folder}/paciente.html"]);
        html.Should().Contain("Rascunho").And.Contain("Substituído");
    }

    [Fact]
    public async Task BuildAsync_PdfsMissingFromStorage_AreRenderedAndStoredOnesReused()
    {
        // Arrange
        await using var account = await SeedAccountAsync();

        // Act
        var entries = await BuildAsync(account);

        // Assert
        var folder = $"{Root}/pacientes/002-AS";
        var rendered = entries[$"{folder}/notas/2026-10-16-assinada.pdf"];
        Encoding.ASCII.GetString(rendered, 0, 4).Should().Be("%PDF");
        rendered.Should().NotEqual(StoredPdf);
        entries[$"{folder}/notas/2026-10-09-assinada.pdf"].Should().Equal(StoredPdf);
        Encoding.ASCII.GetString(entries[$"{folder}/documentos/2026-10-20-declaracao-substituido.pdf"], 0, 4).Should().Be("%PDF");
        account.Store.Puts.Should().Be(0);
        await using var db = account.Seed.CreateDbContext();
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == account.SignedNoteId, Ct)).PdfObjectKey.Should().BeNull();
    }

    // QuestPDF compresses page content, so the raw-byte scan of a rendered PDF cannot see its text. The
    // PDFs are therefore also covered at their only input: the models built from AccountData, which never
    // selects RawTranscript nor audit values.
    [Fact]
    public async Task BuildAsync_RawTranscriptAndAuditValues_NeverAppearInAnyEntry()
    {
        // Arrange
        await using var account = await SeedAccountAsync();

        // Act
        var entries = await BuildAsync(account);

        // Assert
        foreach (var (name, bytes) in entries)
        {
            Encoding.Latin1.GetString(bytes).Should().NotContain(Marker, name);
            Text(bytes).Should().NotContain(Marker, name);
        }
        await using var db = account.Seed.CreateDbContext();
        var data = await ExportData.LoadAsync(db, account.Seed.ProviderId, Ct);
        var inputs = new List<object>();
        foreach (var patient in data.Patients.Select(p => ExportViews.Patient(data, p)))
        {
            inputs.AddRange(patient.NotePdfs.Select(p => AccountExportBuilder.NoteModel(data, patient.Patient.Patient, p.Note)));
            inputs.AddRange(patient.DocumentPdfs.Select(p => AccountExportBuilder.DocumentModel(data, patient.Patient.Patient, p.Document)));
        }
        inputs.Should().HaveCount(3);
        JsonSerializer.Serialize(inputs).Should().NotContain(Marker);
        JsonSerializer.Serialize(new object[] { data.Notes, data.SectionsByNote.SelectMany(g => g), data.Audit, data.Documents })
            .Should().NotContain(Marker);
    }

    [Fact]
    public async Task BuildAsync_CsvTextStartingWithFormulaCharacter_IsPrefixedWithApostrophe()
    {
        // Arrange
        await using var account = await SeedAccountAsync();
        await account.Seed.ExecuteSqlAsync(
            """
            UPDATE "PatientDiagnoses" SET "Description" = '=SOMA(1;2)' WHERE "PatientId" = @p;
            UPDATE "MedicationEntries" SET "Posology" = '-1 ao dia' WHERE "PatientId" = @p;
            """,
            Ct, ("p", account.AnaId));

        // Act
        var entries = await BuildAsync(account);

        // Assert
        var folder = $"{Root}/pacientes/002-AS";
        Text(entries[$"{folder}/diagnosticos.csv"]).Should().Contain(";\"'=SOMA(1;2)\";");
        Text(entries[$"{folder}/medicacoes.csv"]).Should().Contain(";'-1 ao dia;");
        Json(entries[$"{folder}/paciente.json"]).GetProperty("diagnosticos")[0].GetProperty("descricao").GetString()
            .Should().Be("=SOMA(1;2)");
        ExportFormat.FormulaSafe("@x").Should().Be("'@x");
        ExportFormat.FormulaSafe("+x").Should().Be("'+x");
        ExportFormat.FormulaSafe("\tx").Should().Be("'\tx");
        ExportFormat.FormulaSafe("\rx").Should().Be("'\rx");
        ExportFormat.FormulaSafe("Ana-Souza").Should().Be("Ana-Souza");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildAsync_SignedDocumentWithoutRuleset_UsesStoredPdfOrMarksUnavailable(bool stored)
    {
        // Arrange
        await using var account = await SeedAccountAsync();
        var key = $"documents/test/{account.SupersededDocumentId}.pdf";
        await account.Seed.ExecuteSqlAsync(
            """
            UPDATE "ClinicalDocuments" SET "RulesetId" = NULL,
                "PdfObjectKey" = CASE WHEN @stored THEN @key END,
                "PdfSha256" = CASE WHEN @stored THEN repeat('b', 64) END,
                "ExportedAt" = CASE WHEN @stored THEN now() END
            WHERE "Id" = @id
            """,
            Ct, ("stored", stored), ("key", key), ("id", account.SupersededDocumentId));
        if (stored)
        {
            account.Store.Objects[key] = StoredPdf;
        }

        // Act
        var entries = await BuildAsync(account);

        // Assert
        var folder = $"{Root}/pacientes/002-AS";
        var pdfPath = $"{folder}/documentos/2026-10-20-declaracao-substituido.pdf";
        var document = Json(entries[$"{folder}/documentos/documentos.json"]).EnumerateArray()
            .Single(d => d.GetProperty("id").GetGuid() == account.SupersededDocumentId);
        var html = Text(entries[$"{folder}/paciente.html"]);
        if (stored)
        {
            entries[pdfPath].Should().Equal(StoredPdf);
            document.GetProperty("pdf").GetString().Should().Be("documentos/2026-10-20-declaracao-substituido.pdf");
            html.Should().NotContain("PDF indisponível");
        }
        else
        {
            entries.Should().NotContainKey(pdfPath);
            document.GetProperty("pdf").ValueKind.Should().Be(JsonValueKind.Null);
            html.Should().Contain("PDF indisponível");
        }
    }

    [Fact]
    public async Task BuildAsync_PendingAiThemeProposal_IsMarkedInPatientPage()
    {
        // Arrange
        await using var account = await SeedAccountAsync();
        await account.Seed.ExecuteSqlAsync(
            """
            INSERT INTO "ThemeTags" ("NoteId", "TermId", "CreatedBy")
            SELECT @note, t."Id", 'ai'
            FROM "ThemeTerms" t JOIN "ThemeVocabularies" v ON v."Id" = t."VocabularyId"
            WHERE v."Active" AND t."Status" = 'active'
            ORDER BY t."Code" DESC LIMIT 1
            """,
            Ct, ("note", account.DraftNoteId));

        // Act
        var entries = await BuildAsync(account);

        // Assert
        var html = Text(entries[$"{Root}/pacientes/002-AS/paciente.html"]);
        html.Split("(proposta da IA)").Should().HaveCount(3, "the theme table and the note's theme line both mark it");
        Text(entries[$"{Root}/LEIA-ME.txt"]).Should().Contain("(proposta da IA)");
    }

    [Fact]
    public async Task BuildAsync_CancelledAppointment_IncludesCancellationReason()
    {
        // Arrange
        await using var account = await SeedAccountAsync();
        var id = await account.Seed.AddAppointmentAsync(account.AnaId, Now.AddDays(-3), "cancelled", cancellationToken: Ct);
        await account.Seed.ExecuteSqlAsync(
            """UPDATE "Appointments" SET "CancelledBy" = 'provider', "CancellationReason" = 'Viagem do profissional' WHERE "Id" = @id""",
            Ct, ("id", id));

        // Act
        var entries = await BuildAsync(account);

        // Assert
        var agenda = Text(entries[$"{Root}/conta/agenda.csv"]).Split("\r\n");
        agenda[0].Should().Contain(";motivo_cancelamento;");
        agenda.Should().Contain(line => line.Contains(";cancelled;") && line.Contains(";provider;Viagem do profissional;"));
        Text(entries[$"{Root}/LEIA-ME.txt"]).Should().Contain("motivo_cancelamento");
    }

    [Fact]
    public async Task BuildAsync_HtmlAndCsv_FollowFormatRules()
    {
        // Arrange
        await using var account = await SeedAccountAsync();

        // Act
        var entries = await BuildAsync(account);

        // Assert
        foreach (var (name, bytes) in entries.Where(e => e.Key.EndsWith(".html")))
        {
            var html = Text(bytes);
            html.Should().Contain("<html lang=\"pt-BR\">", name);
            html.Should().NotContainEquivalentOf("<script", name);
            html.Should().NotContainEquivalentOf("http://", name).And.NotContainEquivalentOf("https://", name);
        }
        foreach (var (name, bytes) in entries.Where(e => e.Key.EndsWith(".csv")))
        {
            bytes[..3].Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF }, name);
            Text(bytes).Split("\r\n")[0].Should().Contain(";", name);
        }
        Json(entries[$"{Root}/conta/perfil.json"]).GetProperty("crp").GetString().Should().Be("12345/06");
        Text(entries[$"{Root}/conta/agenda.csv"]).Should().Contain("-03:00");
    }
}
