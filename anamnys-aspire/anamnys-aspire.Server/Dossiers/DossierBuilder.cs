using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using Anamnys.Server.Exports;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Integrity;
using Anamnys.Server.Notes;
using Anamnys.Server.Storage;

namespace Anamnys.Server.Dossiers;

public sealed record DossierManifestProvider(string Name, string? Crp);

public sealed record DossierManifestPeriod(DateOnly? From, DateOnly? To);

public sealed record DossierManifestFile(string Path, string Sha256, long Bytes);

// manifesto.json (design/specs/2026-11-04-patient-dossier-design.md §4): every file of the ZIP but itself, ids only, no
// patient name. Reason is the code (crp, court, patient_request, other), never the note text.
public sealed record DossierManifest(
    int V, Guid DossierId, int Number, DossierManifestProvider Provider, Guid PatientId, DateTimeOffset GeneratedAt,
    DossierManifestPeriod Period, string Reason, IReadOnlyList<DossierManifestFile> Files)
{
    public static JsonSerializerOptions JsonOptions => ExportFormat.Json;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DossierManifest? FromJson(string json) => JsonSerializer.Deserialize<DossierManifest>(json, JsonOptions);
}

public sealed record DossierBuildResult(DossierManifest Manifest, bool IntegrityVerified);

// Writes the dossier ZIP (spec §4) into the output stream. Original files are copied byte for byte (a missing note or
// document PDF is rendered from the signed record and not stored, as in the account export); dossie.pdf, LEIA-ME.txt and
// manifesto.json come last, once every file and its hash is known. The ZIP hash and its timestamp are the caller's.
public static class DossierBuilder
{
    public const string NotesFolder = "notas";
    public const string DocumentsFolder = "documentos";
    public const string ExternalFolder = "externos";
    public const string ExternalBaseName = "documento-externo";
    public const string DisclosuresFolder = "divulgacoes";
    public const string IntegrityReportPath = "integridade/comprovante.pdf";
    public const string TokensFolder = "integridade/carimbos/";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static Task<DossierBuildResult> BuildAsync(
        AnamnysDbContext db, INoteFileStore store, ITimestampAuthority tsa, DossierRequest request, Stream output, CancellationToken ct) =>
        BuildAsync(db, store, tsa, request, output, null, ct);

    // pdfInputs sees every model a PDF is rendered from (tests check that nothing excluded reaches them).
    internal static async Task<DossierBuildResult> BuildAsync(
        AnamnysDbContext db, INoteFileStore store, ITimestampAuthority tsa, DossierRequest request, Stream output,
        Action<object>? pdfInputs, CancellationToken ct)
    {
        var data = await DossierData.LoadAsync(db, tsa, request, ct);
        var names = new DossierFileNames();
        var files = new List<DossierManifestFile>();
        var summary = new List<DossierSummaryRow>();
        var titles = new Dictionary<Guid, string>();

        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Utf8))
        {
            async Task AddAsync(string path, byte[] bytes, bool compress)
            {
                var entry = archive.CreateEntry(path, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                await using (var stream = entry.Open())
                {
                    await stream.WriteAsync(bytes, ct);
                }
                files.Add(new DossierManifestFile(path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength));
            }

            foreach (var note in data.Notes)
            {
                var model = NoteModel(data, note);
                var bytes = await StoredAsync(store, note.PdfObjectKey, ct);
                if (bytes is null)
                {
                    pdfInputs?.Invoke(model);
                    bytes = NotePdf.Render(model);
                }
                var path = names.Reserve($"{NotesFolder}/{ExportFormat.IsoDate(note.SignedAt)}-nota", "pdf");
                await AddAsync(path, bytes, compress: false);
                var title = $"Nota {model.Code} ({model.FormatName})";
                titles[note.Id] = title;
                summary.Add(new DossierSummaryRow(note.SignedAt, "nota", title, path));
            }

            foreach (var document in data.Documents)
            {
                var title = DocumentTitle(document, data.IsSuperseded(document));
                titles[document.Id] = title;
                var bytes = await StoredAsync(store, document.PdfObjectKey, ct);
                if (bytes is null && document.RulesetId is { } rulesetId && data.Rulesets.TryGetValue(rulesetId, out var ruleset))
                {
                    var model = DocumentPdfModel.From(document, ruleset, data.Patient, data.Provider);
                    pdfInputs?.Invoke(model);
                    bytes = DocumentPdf.Render(model);
                }
                if (bytes is null)
                {
                    summary.Add(new DossierSummaryRow(document.SignedAt!.Value, "documento", title, "arquivo não incluído: PDF indisponível"));
                    continue;
                }
                var path = names.Reserve(
                    $"{DocumentsFolder}/{ExportFormat.IsoDate(document.SignedAt!.Value)}-{DossierFileNames.Sanitize(document.Kind)}", "pdf",
                    data.IsSuperseded(document) ? "-substituido" : "");
                await AddAsync(path, bytes, compress: false);
                summary.Add(new DossierSummaryRow(document.SignedAt!.Value, "documento", title, path));
            }

            foreach (var external in data.Externals)
            {
                // The original name may carry the patient's name: it stays inside dossie.pdf, never in a ZIP path.
                var title = $"{ExternalKind(external.Kind)}: {external.Filename}";
                string file;
                var bytes = DossierData.IsIncluded(external) ? await StoredAsync(store, external.ObjectKey, ct) : null;
                if (bytes is not null)
                {
                    file = names.Reserve(
                        $"{ExternalFolder}/{ExportFormat.IsoDate(external.UploadedAt)}-{ExternalBaseName}",
                        DossierFileNames.Extension(external.Filename, external.MimeType));
                    await AddAsync(file, bytes, compress: true);
                }
                else
                {
                    file = external.ScanStatus switch
                    {
                        ExternalDocumentService.Infected => "arquivo não incluído: bloqueado",
                        ExternalDocumentService.Failed => "arquivo não incluído: verificação falhou",
                        ExternalDocumentService.Clean => "arquivo não incluído: indisponível no armazenamento",
                        _ => "arquivo não incluído: pendente de verificação",
                    };
                }
                summary.Add(new DossierSummaryRow(external.UploadedAt, "documento externo", title, file));
            }

            var disclosures = new List<DossierDisclosureRow>();
            foreach (var a in data.Authorizations)
            {
                string term;
                var bytes = a.TermObjectKey is { } key ? await StoredAsync(store, key, ct) : null;
                if (bytes is not null)
                {
                    term = names.Reserve(
                        $"{DisclosuresFolder}/{ExportFormat.IsoDate(a.AuthorizedAt)}-termo",
                        DossierFileNames.ExtensionOf(FileTypeSniffer.MimeTypeOf(bytes)));
                    await AddAsync(term, bytes, compress: true);
                }
                else
                {
                    term = a.TermObjectKey is null ? "nenhum termo anexado" : "termo não incluído: indisponível no armazenamento";
                }
                var document = titles.GetValueOrDefault(a.DocumentId)
                    ?? $"{DocumentCatalog.KindTitle(data.DisclosedKinds[a.DocumentId])} {DocumentPdfModel.CodeFor(a.DocumentId)}";
                // Dated by the authorization, also when only a link or an access brought it into the period.
                summary.Add(new DossierSummaryRow(a.AuthorizedAt, "divulgação", $"{document} para {a.RecipientName} ({a.RecipientRole})", term));
                disclosures.Add(new DossierDisclosureRow(
                    document, a.RecipientName, a.RecipientRole, a.Purpose, a.AuthorizedAt, ObtainedVia(a.ObtainedVia), term,
                    data.Links.Where(l => l.AuthorizationId == a.Id)
                        .Select(l => new DossierLinkRow(
                            l.CreatedAt, l.ExpiresAt, l.RevokedAt, l.BlockedAt, l.ViewsUsed, l.ViewCap, l.DownloadEnabled,
                            data.AccessesByLink[l.Id].Select(x => new DossierAccessRow(x.At, AccessOutcome(x.Outcome), x.UserAgent)).ToList()))
                        .ToList()));
            }

            var tokenFiles = new Dictionary<long, string>();
            foreach (var anchor in data.Anchors)
            {
                var path = $"{TokensFolder}{anchor.Sequence}.tsr";
                await AddAsync(path, anchor.Token, compress: false);
                tokenFiles[anchor.Sequence] = path;
            }

            var crp = data.Provider.CrpNumber is null ? "" : $"{data.Provider.CrpNumber}/{data.Provider.CrpRegion}";
            var report = new IntegritySubjectsReportModel(
                data.Provider.Name, crp, request.GeneratedAt,
                data.Integrity.Select(i => new IntegrityReportSeal(
                    SubjectLabel(i), i.Sequence, i.Result.Status, i.Result.SealedAt, i.Result.Retroactive, i.Result.Anchored)).ToList(),
                data.Anchors.Select(a => new IntegrityReportAnchor(a.Sequence, a.HeadHash, a.TsaUrl, a.AnchoredAt, true)).ToList(),
                TokensFolder);
            pdfInputs?.Invoke(report);
            await AddAsync(IntegrityReportPath, IntegrityReportPdf.Render(report), compress: false);

            var dossier = PdfModel(data, request, crp, summary, disclosures, titles, tokenFiles);
            pdfInputs?.Invoke(dossier);
            await AddAsync("dossie.pdf", DossierPdf.Render(dossier), compress: false);
            await AddAsync("LEIA-ME.txt", Utf8.GetBytes(DossierReadme.Text(request, data.Anchors)), compress: true);

            var manifest = new DossierManifest(
                1, request.DossierId, request.Number, new DossierManifestProvider(data.Provider.Name, crp.Length > 0 ? crp : null),
                request.PatientId, ExportFormat.Local(request.GeneratedAt), new DossierManifestPeriod(request.PeriodFrom, request.PeriodTo),
                request.Reason, files.ToList());
            var entry = archive.CreateEntry("manifesto.json", CompressionLevel.Optimal);
            await using (var stream = entry.Open())
            {
                await stream.WriteAsync(Utf8.GetBytes(manifest.ToJson()), ct);
            }
            return new DossierBuildResult(manifest, data.IntegrityVerified);
        }
    }

    internal static NotePdfModel NoteModel(DossierData data, DossierNote note)
    {
        var entity = new Note
        {
            Id = note.Id, Format = note.Format, SignedAt = note.SignedAt, SignedByCrp = note.SignedByCrp, CreatedAt = note.CreatedAt,
        };
        var session = note.SessionId is { } id ? data.Sessions.GetValueOrDefault(id) : null;
        return NotePdfModel.From(entity, data.SectionsByNote[note.Id].ToList(), data.Patient, data.Provider, session);
    }

    private static DossierPdfModel PdfModel(
        DossierData data, DossierRequest request, string crp, List<DossierSummaryRow> summary, List<DossierDisclosureRow> disclosures,
        IReadOnlyDictionary<Guid, string> titles, IReadOnlyDictionary<long, string> tokenFiles)
    {
        var signedAt = data.Notes.ToDictionary(n => n.Id, n => n.SignedAt);
        foreach (var d in data.Documents)
        {
            signedAt[d.Id] = d.SignedAt!.Value;
        }
        var problems = data.Integrity
            .Where(i => i.Result.Status != SubjectStatus.Intact)
            .Select(i => $"{titles[i.SubjectId]}: {IntegrityReportPdf.StatusText(i.Result.Status)}"
                + (i.Sequence is { } n ? $" (selo nº {n})" : ""))
            .ToList();
        var compliance = data.Documents
            .Select(d => data.Compliance.TryGetValue(d.Id, out var c)
                ? new DossierComplianceRow(
                    titles[d.Id], d.SignedAt!.Value,
                    c.Passed ? "conforme (nenhuma pendência na assinatura)" : $"assinado com pendências ({(c.Mode == "advisory" ? "modo orientativo" : c.Mode)})",
                    c.Missing.Select(code => DocumentCatalog.Element(d.Kind, code).Label).ToList())
                : new DossierComplianceRow(titles[d.Id], d.SignedAt!.Value, "sem verificação registrada", []))
            .ToList();
        var p = data.Patient;
        return new DossierPdfModel(
            request.Number,
            data.Provider.Name,
            crp,
            $"{p.FirstName} {p.LastName}".Trim(),
            p.DateOfBirth,
            request.GeneratedAt,
            request.PeriodFrom,
            request.PeriodTo,
            DossierPdf.ReasonText(request.Reason, request.ReasonNote),
            data.IntegrityVerified,
            problems,
            summary.OrderBy(r => r.At).ToList(),
            disclosures,
            compliance,
            new DossierRetentionRow(p.LastRecordAt, p.RetentionUntil, p.ArchivedAt, data.HasDisposalTerm),
            data.Integrity.Select(i => new DossierIntegrityRow(
                    titles[i.SubjectId], signedAt[i.SubjectId], IntegrityReportPdf.StatusText(i.Result.Status), i.Sequence,
                    i.Result.SealedAt, i.Result.Retroactive, i.Result.Anchored))
                .ToList(),
            data.Anchors.Select(a => new DossierAnchorRow(a.Sequence, a.AnchoredAt, a.TsaUrl, tokenFiles[a.Sequence])).ToList());
    }

    private static string DocumentTitle(ClinicalDocument d, bool superseded) =>
        $"{DocumentCatalog.KindTitle(d.Kind)} {DocumentPdfModel.CodeFor(d.Id)}{(superseded ? " (substituído)" : "")}";

    private static string SubjectLabel(DossierIntegrity i) =>
        i.SubjectType == IntegritySeal.Note ? $"Nota {NotePdfModel.CodeFor(i.SubjectId)}" : $"Documento {DocumentPdfModel.CodeFor(i.SubjectId)}";

    private static string ExternalKind(string kind) => kind switch
    {
        "laudo" => "Laudo",
        "receita" => "Receita",
        "exame" => "Exame",
        "encaminhamento" => "Encaminhamento",
        "relatorio_externo" => "Relatório externo",
        _ => "Outro",
    };

    private static string ObtainedVia(string via) => via switch
    {
        DisclosureAuthorization.VerbalSession => "verbalmente, em sessão",
        DisclosureAuthorization.SignedTerm => "termo assinado",
        DisclosureAuthorization.InWriting => "por escrito",
        _ => via,
    };

    private static string AccessOutcome(string outcome) => outcome switch
    {
        ShareAccess.Opened => "aberto",
        ShareAccess.BadPassword => "senha incorreta",
        ShareAccess.Expired => "tentativa após expirar",
        ShareAccess.Revoked => "tentativa após revogação",
        ShareAccess.CapReached => "tentativa após o limite de visualizações",
        ShareAccess.Blocked => "tentativa com o link bloqueado",
        _ => outcome,
    };

    private static async Task<byte[]?> StoredAsync(INoteFileStore store, string? key, CancellationToken ct) =>
        key is null ? null : await store.GetAsync(key, ct);
}
