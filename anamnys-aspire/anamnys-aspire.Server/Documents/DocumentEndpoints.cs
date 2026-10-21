using Anamnys.Server.Data;
using Anamnys.Server.Notes;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Documents;

public sealed record CreateDocumentRequest(string? Kind, Guid? NoteId);

public sealed record SaveDocumentRequest(Dictionary<string, string>? Fields, List<string>? Confirmations);

public sealed record SignDocumentRequest(bool AcknowledgeMissing);

public sealed record DocumentSettingsRequest(string? DocumentCheckMode);

public static class DocumentEndpoints
{
    private static readonly string[] CheckModes = ["blocking", "advisory"];

    public static void MapDocumentEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapGet("providers/me/document-kinds", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is null)
            {
                return Results.Unauthorized();
            }
            var rulesets = await db.ConformityRulesets.AsNoTracking()
                .Where(r => DocumentCatalog.AvailableKinds.Contains(r.DocumentKind))
                .ToListAsync(ct);
            var kinds = DocumentCatalog.AvailableKinds
                .Select(kind => rulesets.Where(r => r.DocumentKind == kind).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault())
                .OfType<Data.Entities.ConformityRuleset>()
                .Select(r => new
                {
                    kind = r.DocumentKind,
                    title = DocumentCatalog.KindTitle(r.DocumentKind),
                    rulesetVersion = r.Version,
                    elements = r.Elements.Select(code => DocumentCatalog.Element(r.DocumentKind, code))
                        .Select(e => new { code = e.Code, origin = e.Origin.ToString().ToLowerInvariant(), label = e.Label, hint = e.Hint }),
                    prohibitions = r.Prohibitions.Select(DocumentCatalog.Prohibition)
                        .Select(p => new { code = p.Code, byConstruction = p.ByConstruction, confirmationText = p.ConfirmationText }),
                })
                .ToList();
            return Results.Ok(kinds);
        });

        phi.MapGet("providers/me/document-settings", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var mode = await db.Providers.AsNoTracking().Where(p => p.Id == providerId).Select(p => p.DocumentCheckMode).SingleAsync(ct);
            return Results.Ok(new { documentCheckMode = mode });
        });

        phi.MapPut("providers/me/document-settings", async (DocumentSettingsRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            if (request.DocumentCheckMode is null || !CheckModes.Contains(request.DocumentCheckMode))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["documentCheckMode"] = ["Modo inválido."] });
            }
            var provider = await db.Providers.SingleAsync(p => p.Id == providerId, ct);
            provider.DocumentCheckMode = request.DocumentCheckMode;
            provider.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { documentCheckMode = provider.DocumentCheckMode });
        });

        phi.MapGet("providers/me/patients/{patientId:guid}/documents", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var list = await ClinicalDocumentService.ListAsync(db, providerId, patientId, ct);
            return list is null ? Results.NotFound() : Results.Ok(list);
        });

        phi.MapPost("providers/me/patients/{patientId:guid}/documents", async (Guid patientId, CreateDocumentRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await ClinicalDocumentService.CreateAsync(
                db, providerId, patientId, request.Kind ?? "", request.NoteId, DateTimeOffset.UtcNow, ct);
            return result.Outcome == DocumentOutcome.Ok
                ? Results.Json(new { id = result.Id }, statusCode: StatusCodes.Status201Created)
                : ToResult(result);
        });

        var documents = phi.MapGroup("providers/me/documents");

        documents.MapPost("search", async (DocumentSearchRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate();
            return errors.Count > 0
                ? Results.ValidationProblem(errors)
                : Results.Ok(await DocumentList.SearchAsync(db, providerId, request, ct));
        });

        documents.MapGet("{documentId:guid}",async (Guid documentId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var detail = await ClinicalDocumentService.GetAsync(db, providerId, documentId, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        documents.MapPut("{documentId:guid}", async (Guid documentId, SaveDocumentRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await ClinicalDocumentService.SaveAsync(
                db, providerId, documentId, request.Fields ?? [], request.Confirmations ?? [], DateTimeOffset.UtcNow, ct);
            if (result.Outcome != DocumentOutcome.Ok)
            {
                return ToResult(result);
            }
            var detail = await ClinicalDocumentService.GetAsync(db, providerId, documentId, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        documents.MapPost("{documentId:guid}/sign", async (Guid documentId, SignDocumentRequest? request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await ClinicalDocumentService.SignAsync(
                db, providerId, documentId, request?.AcknowledgeMissing ?? false, DateTimeOffset.UtcNow, ct);
            return result.Outcome == DocumentOutcome.Ok ? Results.Ok() : ToResult(result);
        });

        documents.MapPost("{documentId:guid}/correct", async (Guid documentId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await ClinicalDocumentService.CorrectAsync(db, providerId, documentId, DateTimeOffset.UtcNow, ct);
            return result.Outcome == DocumentOutcome.Ok
                ? Results.Json(new { id = result.Id }, statusCode: StatusCodes.Status201Created)
                : ToResult(result);
        });

        documents.MapPost("{documentId:guid}/export", async (Guid documentId, HttpContext http, AnamnysDbContext db, INoteFileStore store, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await DocumentExport.ExportAsync(db, store, providerId, documentId, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                ExportOutcome.Ok => Results.Ok(new { url = result.Url, expiresAt = result.ExpiresAt }),
                ExportOutcome.NotFound => Results.NotFound(),
                ExportOutcome.NotSigned => Results.Conflict(new { message = "Assine o documento antes de exportar.", code = "not_signed" }),
                _ => Results.Json(new { message = "Armazenamento indisponível. Tente de novo em instantes.", code = "storage_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        });
    }

    private static IResult ToResult(DocumentResult result) => result.Outcome switch
    {
        DocumentOutcome.NotFound => Results.NotFound(),
        DocumentOutcome.Archived => Results.Conflict(new { message = "Desarquive o paciente para criar documentos.", code = "archived" }),
        DocumentOutcome.Locked => Results.Conflict(new { message = "Este documento já foi assinado e não pode ser alterado.", code = "locked" }),
        DocumentOutcome.KindNotAvailable => Results.Conflict(new { message = "Este tipo de documento ainda não está disponível.", code = "kind_not_available" }),
        DocumentOutcome.MissingElements => Results.Conflict(new
        {
            message = "Faltam elementos obrigatórios para assinar.",
            code = "missing_elements",
            missing = result.Missing ?? [],
        }),
        DocumentOutcome.ConfirmationsRequired => Results.Conflict(new
        {
            message = "Confirme as vedações antes de assinar.",
            code = "confirmations_required",
            missing = result.Missing ?? [],
        }),
        DocumentOutcome.MissingCrp => Results.UnprocessableEntity(new { message = "Cadastre seu CRP no perfil para assinar.", code = "missing_crp" }),
        DocumentOutcome.MissingCity => Results.UnprocessableEntity(new { message = "Cadastre a cidade de atendimento no perfil para assinar.", code = "missing_city" }),
        _ => Results.ValidationProblem((result.Missing ?? []).ToDictionary(
            code => code, _ => new[] { $"Use no máximo {ClinicalDocumentService.MaxFieldLength} caracteres." })),
    };
}
