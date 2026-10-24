using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using Anamnys.Server.Notes;
using Anamnys.Server.Storage;

namespace Anamnys.Server.Exports;

// Writes the account ZIP (design/specs/2026-10-23-account-export-design.md §4) straight into the
// destination stream. Text is built from AccountData, which never holds RawTranscript or audit values;
// PDFs are read from storage or rendered from the signing snapshot one at a time and never stored.
public sealed class AccountExportBuilder(AnamnysDbContext db, INoteFileStore store, TimeProvider clock) : IAccountExportBuilder
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task BuildAsync(Guid providerId, Stream destination, CancellationToken ct)
    {
        var data = await ExportData.LoadAsync(db, providerId, ct);
        var generatedAt = clock.GetUtcNow();
        var root = RootFolder(generatedAt);
        var profile = ExportViews.Profile(data, generatedAt);
        var patients = new List<PatientExport>();

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true, Utf8);
        foreach (var entry in data.Patients)
        {
            var folder = $"{root}/pacientes/{entry.Folder}";
            var patient = ExportViews.Patient(data, entry);

            // PDFs first: a document PDF neither stored nor renderable is then left out of the pages.
            foreach (var pdf in patient.NotePdfs)
            {
                var bytes = await StoredAsync(pdf.Note.PdfObjectKey, ct)
                    ?? NotePdf.Render(NoteModel(data, entry.Patient, pdf.Note));
                await BytesAsync(archive, $"{folder}/{pdf.Path}", bytes, ct);
            }
            var unavailable = new HashSet<Guid>();
            foreach (var pdf in patient.DocumentPdfs)
            {
                var bytes = await StoredAsync(pdf.Document.PdfObjectKey, ct)
                    ?? (ExportViews.CanRender(data, pdf.Document)
                        ? DocumentPdf.Render(DocumentModel(data, entry.Patient, pdf.Document))
                        : null);
                if (bytes is null)
                {
                    unavailable.Add(pdf.Document.Id);
                    continue;
                }
                await BytesAsync(archive, $"{folder}/{pdf.Path}", bytes, ct);
            }
            if (unavailable.Count > 0)
            {
                patient = ExportViews.Patient(data, entry, unavailable);
            }
            patients.Add(patient);

            var j = patient.Json;
            await TextAsync(archive, $"{folder}/paciente.html", ExportHtml.Patient(patient), ct);
            await TextAsync(archive, $"{folder}/paciente.json", Json(j), ct);
            await TextAsync(archive, $"{folder}/notas/notas.json", Json(j.Notas), ct);
            await TextAsync(archive, $"{folder}/notas/notas.csv", NotesCsv(j), ct);
            await TextAsync(archive, $"{folder}/documentos/documentos.json", Json(j.Documentos), ct);
            await TextAsync(archive, $"{folder}/escalas.csv", ScalesCsv(j), ct);
            await TextAsync(archive, $"{folder}/diagnosticos.csv", DiagnosesCsv(j), ct);
            await TextAsync(archive, $"{folder}/medicacoes.csv", MedicationsCsv(j), ct);
            await TextAsync(archive, $"{folder}/objetivos.csv", ObjectivesCsv(j), ct);
            await TextAsync(archive, $"{folder}/temas.csv", ThemesCsv(j), ct);
        }

        await TextAsync(archive, $"{root}/index.html", ExportHtml.Index(data, profile, patients), ct);
        await TextAsync(archive, $"{root}/LEIA-ME.txt", ExportReadme.Text(root, generatedAt), ct);
        await TextAsync(archive, $"{root}/conta/perfil.json", Json(profile), ct);
        await TextAsync(archive, $"{root}/conta/agenda.csv", Agenda(data), ct);
        await TextAsync(archive, $"{root}/conta/auditoria.csv", Audit(data), ct);
    }

    public static string RootFolder(DateTimeOffset generatedAt) => $"anamnys-exportacao-{ExportFormat.IsoDate(generatedAt)}";

    internal static NotePdfModel NoteModel(AccountData data, Patient patient, ExportNote note)
    {
        var entity = new Note
        {
            Id = note.Id, Format = note.Format, SignedAt = note.SignedAt, SignedByCrp = note.SignedByCrp, CreatedAt = note.CreatedAt,
        };
        var session = note.SessionId is { } id ? data.Sessions.GetValueOrDefault(id) : null;
        return NotePdfModel.From(entity, data.SectionsByNote[note.Id].ToList(), patient, data.Provider, session);
    }

    internal static DocumentPdfModel DocumentModel(AccountData data, Patient patient, ClinicalDocument document) =>
        DocumentPdfModel.From(document, data.Rulesets[document.RulesetId!.Value], patient, data.Provider);

    private async Task<byte[]?> StoredAsync(string? key, CancellationToken ct) =>
        key is null ? null : await store.GetAsync(key, ct);

    private static async Task TextAsync(ZipArchive archive, string path, string text, CancellationToken ct)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(Utf8.GetBytes(text), ct);
    }

    // PDFs are already compressed.
    private static async Task BytesAsync(ZipArchive archive, string path, byte[] bytes, CancellationToken ct)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, ct);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ExportFormat.Json);

    private static string Agenda(AccountData data)
    {
        var folders = data.Patients.ToDictionary(p => p.Patient.Id, p => p.Folder);
        return ExportFormat.Csv(
            ["paciente", "inicio", "fim", "fuso", "status", "modalidade", "criado_por", "cancelado_em", "cancelado_por",
                "motivo_cancelamento", "sessao_id"],
            data.Appointments.Select(a => new object?[]
            {
                folders.GetValueOrDefault(a.PatientId), a.StartsAt, a.EndsAt, a.Timezone, a.Status, a.Modality, a.CreatedBy,
                a.CancelledAt, a.CancelledBy, a.CancellationReason, a.SessionId,
            }));
    }

    private static string Audit(AccountData data) =>
        ExportFormat.Csv(
            ["data", "acao", "campo", "nota_id", "documento_id"],
            data.Audit.Select(a => new object?[] { a.At, a.Action, a.Field, a.NoteId, a.DocumentId }));

    private static string NotesCsv(PacienteJson j) =>
        ExportFormat.Csv(
            ["id", "codigo", "data", "situacao", "status", "formato", "assinada_em", "crp_assinatura", "pdf", "secoes", "temas"],
            j.Notas.Select(n => new object?[]
            {
                n.Id, n.Codigo, n.Data, n.Situacao, n.Status, n.Formato, n.AssinadaEm, n.CrpAssinatura, n.Pdf,
                string.Join("\n\n", n.Secoes.Select(s => $"{s.Titulo}: {s.Conteudo}")),
                string.Join(", ", n.Temas.Select(t => t.Tema)),
            }));

    private static string ScalesCsv(PacienteJson j) =>
        ExportFormat.Csv(
            ["id", "instrumento", "nome_instrumento", "pontuacao", "pontuacao_minima", "pontuacao_maxima", "aplicada_em", "sessao_id"],
            j.Escalas.Select(s => new object?[]
            {
                s.Id, s.Instrumento, s.NomeInstrumento, s.Pontuacao, s.PontuacaoMinima, s.PontuacaoMaxima, s.AplicadaEm, s.SessaoId,
            }));

    private static string DiagnosesCsv(PacienteJson j) =>
        ExportFormat.Csv(
            ["id", "descricao", "cid", "registrado_em", "resolvido_em"],
            j.Diagnosticos.Select(d => new object?[] { d.Id, d.Descricao, d.Cid, d.RegistradoEm, d.ResolvidoEm }));

    private static string MedicationsCsv(PacienteJson j) =>
        ExportFormat.Csv(
            ["id", "medicamento", "dose", "posologia", "inicio", "fim"],
            j.Medicacoes.Select(m => new object?[] { m.Id, m.Medicamento, m.Dose, m.Posologia, m.Inicio, m.Fim }));

    private static string ObjectivesCsv(PacienteJson j) =>
        ExportFormat.Csv(
            ["plano_id", "plano_criado_em", "plano_revisado_em", "plano_encerrado_em", "objetivo_id", "objetivo", "criado_em"],
            j.Planos.SelectMany(p => p.Objetivos.Count == 0
                ? new[] { new object?[] { p.Id, p.CriadoEm, p.RevisadoEm, p.EncerradoEm, null, null, null } }
                : p.Objetivos.Select(o => new object?[] { p.Id, p.CriadoEm, p.RevisadoEm, p.EncerradoEm, o.Id, o.Descricao, o.CriadoEm })));

    private static string ThemesCsv(PacienteJson j) =>
        ExportFormat.Csv(
            ["nota_id", "data_nota", "codigo", "tema", "tema_pai", "origem", "proposto", "adicionado_em"],
            j.Notas.SelectMany(n => n.Temas.Select(t => new object?[]
            {
                n.Id, n.Data, t.Codigo, t.Tema, t.TemaPai, t.Origem, t.Proposto, t.AdicionadoEm,
            })));
}
