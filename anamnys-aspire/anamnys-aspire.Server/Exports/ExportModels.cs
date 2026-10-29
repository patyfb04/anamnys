using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using Anamnys.Server.Notes;

namespace Anamnys.Server.Exports;

// The JSON shapes of the export. Field names are documented in LEIA-ME.txt (ExportReadme).
public sealed record PerfilJson(
    string Nome, string Email, string? Crp, string? CidadeAtendimento, string Especialidade,
    string FormatoNotaPreferido, string ModoVerificacaoDocumentos, DateTimeOffset ContaCriadaEm,
    DateTimeOffset ExportacaoGeradaEm, int TotalPacientes);

public sealed record PacienteJson(
    string Pasta, string Nome, string Sobrenome, DateOnly? Nascimento, string? Email, string? Telefone,
    string? IdiomaPreferido, DateTimeOffset? UltimaConsulta, bool Arquivado, DateTimeOffset? ArquivadoEm,
    DateTimeOffset CadastradoEm, IReadOnlyList<DiagnosticoJson> Diagnosticos, IReadOnlyList<MedicacaoJson> Medicacoes,
    IReadOnlyList<PlanoJson> Planos, IReadOnlyList<EscalaJson> Escalas, IReadOnlyList<NotaJson> Notas,
    IReadOnlyList<DocumentoJson> Documentos);

public sealed record DiagnosticoJson(Guid Id, string Descricao, string? Cid, DateTimeOffset RegistradoEm, DateOnly? ResolvidoEm);

public sealed record MedicacaoJson(Guid Id, string Medicamento, string? Dose, string? Posologia, DateOnly Inicio, DateOnly? Fim);

public sealed record PlanoJson(
    Guid Id, DateTimeOffset CriadoEm, DateTimeOffset? RevisadoEm, DateTimeOffset? EncerradoEm, IReadOnlyList<ObjetivoJson> Objetivos);

public sealed record ObjetivoJson(Guid Id, string Descricao, DateTimeOffset CriadoEm);

public sealed record EscalaJson(
    Guid Id, string Instrumento, string NomeInstrumento, int Pontuacao, int PontuacaoMinima, int PontuacaoMaxima,
    DateTimeOffset AplicadaEm, Guid? SessaoId);

public sealed record NotaJson(
    Guid Id, string Codigo, DateTimeOffset Data, string Situacao, string Status, string Formato,
    DateTimeOffset CriadaEm, DateTimeOffset AtualizadaEm, DateTimeOffset? AssinadaEm, string? CrpAssinatura,
    string? Pdf, IReadOnlyList<SecaoJson> Secoes, IReadOnlyList<TemaJson> Temas);

public sealed record SecaoJson(string Chave, string Titulo, string Conteudo, bool EditadaPeloProfissional);

public sealed record TemaJson(string Codigo, string Tema, string? TemaPai, string Origem, bool Proposto, DateTimeOffset AdicionadoEm);

public sealed record DocumentoJson(
    Guid Id, string Codigo, string Tipo, string NomeTipo, string Situacao, DateTimeOffset CriadoEm,
    DateTimeOffset AtualizadoEm, DateTimeOffset? AssinadoEm, Guid? SubstituidoPor, Guid? NotaId, string? Pdf,
    AssinaturaJson? Assinatura, IReadOnlyList<CampoJson> Campos, IReadOnlyList<EquipeJson> Equipe, string Corpo);

public sealed record AssinaturaJson(string Nome, string Crp, string Cidade, string NomePaciente);

public sealed record CampoJson(string Codigo, string Rotulo, string Valor);

public sealed record EquipeJson(string Nome, string Categoria, string Registro, string Analise);

// conta/termos-de-descarte/termos.json (F-06): one row per discarded record; pdf is relative to the root folder.
public sealed record TermoDescarteJson(
    Guid Id, string Codigo, DateTimeOffset DescartadoEm, string Paciente, DateOnly PeriodoInicio, DateOnly PeriodoFim,
    int AnosGuarda, DateOnly GuardaAte, string Fundamento, IReadOnlyDictionary<string, int> Contagens, string Pdf);

public sealed record TermPdfPlan(ExportTerm Term, string Path, TermoDescarteJson Json);

public sealed record NotePdfPlan(ExportNote Note, string Path);

public sealed record DocumentPdfPlan(ClinicalDocument Document, string Path);

public sealed record PatientExport(ExportPatient Patient, PacienteJson Json, IReadOnlyList<NotePdfPlan> NotePdfs, IReadOnlyList<DocumentPdfPlan> DocumentPdfs);

public static class ExportViews
{
    public const string Signed = "assinada";
    public const string Draft = "rascunho";
    public const string DocumentSigned = "assinado";
    public const string DocumentSuperseded = "substituido";

    public static PerfilJson Profile(AccountData data, DateTimeOffset generatedAt)
    {
        var p = data.Provider;
        return new(
            p.Name, p.Email, p.CrpNumber is null ? null : $"{p.CrpNumber}/{p.CrpRegion}", p.PracticeCity,
            p.Specialty, p.PreferredNoteFormat, p.DocumentCheckMode, ExportFormat.Local(p.CreatedAt),
            ExportFormat.Local(generatedAt), data.Patients.Count);
    }

    public static DateTimeOffset NoteDate(AccountData data, ExportNote note)
    {
        var session = note.SessionId is { } id ? data.Sessions.GetValueOrDefault(id) : null;
        return session?.ScheduledStart ?? session?.ActualStart ?? note.CreatedAt;
    }

    public static string FormatName(string format) => format == "Biopsychosocial" ? "Biopsicossocial" : format;

    // unavailablePdfs: signed documents whose PDF is neither in storage nor renderable (no ruleset).
    public static PatientExport Patient(AccountData data, ExportPatient entry, IReadOnlySet<Guid>? unavailablePdfs = null)
    {
        var patient = entry.Patient;
        var notes = data.Notes.Where(n => n.PatientId == patient.Id)
            .OrderBy(n => NoteDate(data, n)).ThenBy(n => n.Id)
            .ToList();
        var notePdfs = NumberedPaths(
                notes.Where(n => n.SignedAt != null).ToList(),
                n => $"notas/{ExportFormat.IsoDate(NoteDate(data, n))}-assinada",
                _ => "")
            .Select(x => new NotePdfPlan(x.Item, x.Path))
            .ToList();
        var notePdfById = notePdfs.ToDictionary(x => x.Note.Id, x => x.Path);

        var documents = data.Documents.Where(d => d.PatientId == patient.Id)
            .OrderBy(d => d.SignedAt ?? d.CreatedAt).ThenBy(d => d.Id)
            .ToList();
        // Numbered over every signed document, so a missing PDF never renames the others.
        var documentPdfs = NumberedPaths(
                documents.Where(d => d.SignedAt != null).ToList(),
                d => $"documentos/{ExportFormat.IsoDate(d.SignedAt!.Value)}-{d.Kind}",
                d => d.SupersededBy is null ? "" : "-substituido")
            .Where(x => (x.Item.PdfObjectKey != null || CanRender(data, x.Item)) && unavailablePdfs?.Contains(x.Item.Id) != true)
            .Select(x => new DocumentPdfPlan(x.Item, x.Path))
            .ToList();
        var documentPdfById = documentPdfs.ToDictionary(x => x.Document.Id, x => x.Path);

        var plans = data.ObjectivesByPatient[patient.Id]
            .GroupBy(o => o.PlanId)
            .Select(g => new PlanoJson(
                g.Key, ExportFormat.Local(g.First().PlanCreatedAt), ExportFormat.Local(g.First().PlanReviewedAt),
                ExportFormat.Local(g.First().PlanClosedAt),
                g.Where(o => o.ObjectiveId != null)
                    .Select(o => new ObjetivoJson(o.ObjectiveId!.Value, o.Description!, ExportFormat.Local(o.CreatedAt!.Value)))
                    .ToList()))
            .ToList();

        var json = new PacienteJson(
            entry.Folder,
            patient.FirstName,
            patient.LastName,
            patient.DateOfBirth,
            patient.ContactEmail,
            patient.Phone,
            patient.PreferredLanguage,
            ExportFormat.Local(patient.LastVisit),
            patient.ArchivedAt is not null,
            ExportFormat.Local(patient.ArchivedAt),
            ExportFormat.Local(patient.CreatedAt),
            data.DiagnosesByPatient[patient.Id]
                .OrderBy(d => d.ResolvedOn != null).ThenBy(d => d.RecordedAt)
                .Select(d => new DiagnosticoJson(d.Id, d.Description, d.IcdCode, ExportFormat.Local(d.RecordedAt), d.ResolvedOn))
                .ToList(),
            data.MedicationsByPatient[patient.Id]
                .OrderBy(m => m.EndedOn != null).ThenByDescending(m => m.StartedOn)
                .Select(m => new MedicacaoJson(m.Id, m.Drug, m.Dose, m.Posology, m.StartedOn, m.EndedOn))
                .ToList(),
            plans,
            data.ScalesByPatient[patient.Id]
                .Select(s => new EscalaJson(
                    s.Id, s.Code, s.Name, s.Score, s.MinScore, s.MaxScore, ExportFormat.Local(s.AppliedAt), s.SessionId))
                .ToList(),
            notes.Select(n => Note(data, n, notePdfById.GetValueOrDefault(n.Id))).ToList(),
            documents.Select(d => Document(data, d, documentPdfById.GetValueOrDefault(d.Id))).ToList());

        return new PatientExport(entry, json, notePdfs, documentPdfs);
    }

    private static NotaJson Note(AccountData data, ExportNote note, string? pdf) =>
        new(
            note.Id,
            NotePdfModel.CodeFor(note.Id),
            ExportFormat.Local(NoteDate(data, note)),
            note.SignedAt is null ? Draft : Signed,
            note.Status,
            FormatName(note.Format),
            ExportFormat.Local(note.CreatedAt),
            ExportFormat.Local(note.UpdatedAt),
            ExportFormat.Local(note.SignedAt),
            note.SignedByCrp,
            pdf,
            data.SectionsByNote[note.Id]
                .OrderBy(s => s.Position)
                .Select(s => new SecaoJson(s.Key, NotePdfModel.SectionTitle(s.Key), s.Content.Trim(), s.EditedAt is not null))
                .ToList(),
            data.ThemesByNote[note.Id]
                .Select(t => new TemaJson(
                    t.Code, t.Label, t.ParentLabel, t.Origin == "ai" ? "ia" : "profissional", t.Proposed, ExportFormat.Local(t.AddedAt)))
                .ToList());

    private static DocumentoJson Document(AccountData data, ClinicalDocument d, string? pdf) =>
        new(
            d.Id,
            DocumentPdfModel.CodeFor(d.Id),
            d.Kind,
            DocumentCatalog.KindTitle(d.Kind),
            d.SignedAt is null ? Draft : d.SupersededBy is null ? DocumentSigned : DocumentSuperseded,
            ExportFormat.Local(d.CreatedAt),
            ExportFormat.Local(d.UpdatedAt),
            ExportFormat.Local(d.SignedAt),
            d.SupersededBy,
            d.NoteId,
            pdf,
            d.SignedAt is null
                ? null
                : new AssinaturaJson(d.SignedByName ?? "", d.SignedByCrp ?? "", d.SignedCity ?? "", d.SignedPatientName ?? ""),
            d.Fields
                .Where(f => f.Value.Trim().Length > 0)
                .OrderBy(f => FieldPosition(data, d, f.Key)).ThenBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new CampoJson(f.Key, DocumentCatalog.Element(d.Kind, f.Key).Label, f.Value.Trim()))
                .ToList(),
            d.Team.Select(t => new EquipeJson(t.Name, t.Category, t.Registry, t.Analysis)).ToList(),
            d.Body);

    public const string TermsFolder = "conta/termos-de-descarte";

    // Disposal terms by discard date; the file name carries the date only, never the patient's name.
    public static IReadOnlyList<TermPdfPlan> Terms(AccountData data) =>
        NumberedPaths(data.Terms, t => $"{TermsFolder}/{ExportFormat.IsoDate(t.Term.CreatedAt)}-termo", _ => "")
            .Select(x => new TermPdfPlan(x.Item, x.Path, new TermoDescarteJson(
                x.Item.Term.Id,
                Retention.DisposalTerms.CodeFor(x.Item.Term.Id),
                ExportFormat.Local(x.Item.Term.CreatedAt),
                x.Item.PatientName.Trim(),
                x.Item.Term.PeriodFrom,
                x.Item.Term.PeriodTo,
                x.Item.Term.GuardYears,
                x.Item.Term.RetentionUntil,
                x.Item.Term.Basis,
                x.Item.Term.Counts,
                x.Path)))
            .ToList();

    public static bool CanRender(AccountData data, ClinicalDocument d) =>
        d.RulesetId is { } id && data.Rulesets.ContainsKey(id);

    // The ruleset's order when the document has one (signed ones always do), else alphabetical.
    private static int FieldPosition(AccountData data, ClinicalDocument d, string code)
    {
        if (d.RulesetId is not { } id || !data.Rulesets.TryGetValue(id, out var ruleset))
        {
            return int.MaxValue;
        }
        var position = DocumentBody.ProviderFieldOrder(d.Kind, ruleset.Elements).ToList().IndexOf(code);
        return position < 0 ? int.MaxValue : position;
    }

    // "<base>.pdf", then "<base>-2.pdf", "<base>-3.pdf" for the same day and kind; the suffix
    // (e.g. "-substituido") goes last.
    private static IEnumerable<(T Item, string Path)> NumberedPaths<T>(
        IReadOnlyList<T> items, Func<T, string> baseName, Func<T, string> suffix) =>
        items.GroupBy(baseName)
            .SelectMany(g => g.Select((item, i) => (item, $"{g.Key}{(i == 0 ? "" : $"-{i + 1}")}{suffix(item)}.pdf")));
}
