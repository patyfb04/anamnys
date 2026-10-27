using Anamnys.Server.Data;
using Anamnys.Server.DocumentFacts;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;
using Microsoft.AspNetCore.Http.Features;

namespace Anamnys.Server.ExternalDocuments;

public static class ExternalDocumentEndpoints
{
    // The file plus the multipart envelope and the small text fields.
    private const long MaxRequestBytes = ExternalDocumentService.MaxBytes + 1024 * 1024;

    public static void AddExternalDocuments(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("ExternalDocuments").Get<ExternalDocumentOptions>() ?? new ExternalDocumentOptions();
        builder.Services.AddSingleton(options);
        switch (options.Scanner)
        {
            case "clamav":
                builder.Services.AddSingleton<IVirusScanner, ClamAvScanner>();
                break;
            case "fake" when builder.Environment.IsDevelopment():
                builder.Services.AddSingleton<IVirusScanner, FakeVirusScanner>();
                break;
            default:
                throw new InvalidOperationException(
                    "ExternalDocuments:Scanner must be \"clamav\" (or \"fake\", Development only).");
        }
        builder.Services.AddSingleton<ScanQueue>();
        builder.Services.AddHostedService<ScanWorker>();
    }

    public static void MapExternalDocumentEndpoints(this RouteGroupBuilder phi)
    {
        var group = phi.MapGroup("providers/me");

        group.MapPost("patients/{patientId:guid}/external-documents", async (
            Guid patientId, HttpContext http, AnamnysDbContext db, INoteFileStore store, ScanQueue queue, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            // Refuse before reading the body when the patient is not the provider's or is archived.
            var state = await ExternalDocumentService.PatientStateAsync(db, providerId, patientId, ct);
            if (state != ExternalDocumentOutcome.Ok)
            {
                return Refusal(state);
            }
            if (!http.Request.HasFormContentType)
            {
                return Results.BadRequest(new { message = "Envie o arquivo como multipart/form-data.", code = "invalid_form" });
            }

            var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false })
            {
                limit.MaxRequestBodySize = MaxRequestBytes;
            }
            // The file is patient data: keep it in memory instead of letting the form reader spill it to a temp file
            // (default threshold 64 KB). The whole request is capped at MaxRequestBytes above.
            http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions
            {
                MemoryBufferThreshold = (int)MaxRequestBytes,
                MultipartBodyLengthLimit = MaxRequestBytes,
            }));
            IFormCollection form;
            try
            {
                form = await http.Request.ReadFormAsync(ct);
            }
            catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Refusal(ExternalDocumentOutcome.TooLarge);
            }
            catch (InvalidDataException e) when (e.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
            {
                // The multipart reader's own limit (MultipartBodyLengthLimit) was exceeded.
                return Refusal(ExternalDocumentOutcome.TooLarge);
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { message = "Formulário inválido.", code = "invalid_form" });
            }

            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return Refusal(ExternalDocumentOutcome.Empty);
            }
            if (file.Length > ExternalDocumentService.MaxBytes)
            {
                return Refusal(ExternalDocumentOutcome.TooLarge);
            }
            var content = new byte[file.Length];
            await using (var input = file.OpenReadStream())
            {
                await input.ReadExactlyAsync(content, ct);
            }

            DateOnly? documentDate = null;
            var errors = new Dictionary<string, string[]>();
            if (!string.IsNullOrWhiteSpace(form["documentDate"]))
            {
                if (DateOnly.TryParseExact(form["documentDate"].ToString(), "yyyy-MM-dd", out var parsed))
                {
                    documentDate = parsed;
                }
                else
                {
                    errors["documentDate"] = ["Data inválida."];
                }
            }
            var upload = new ExternalDocumentUpload(
                file.FileName ?? "", content, form["kind"].ToString(), documentDate, form["description"].ToString());
            foreach (var (field, messages) in upload.Validate(ExternalDocumentUpload.Today()))
            {
                errors[field] = messages;
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var result = await ExternalDocumentService.UploadAsync(db, store, queue, providerId, patientId, upload, DateTimeOffset.UtcNow, ct);
            return result.Outcome == ExternalDocumentOutcome.Ok
                ? Results.Created($"/api/phi/providers/me/patients/{patientId}/external-documents", new { id = result.Id, scanStatus = ExternalDocumentService.Pending })
                : Refusal(result.Outcome);
        });

        group.MapGet("patients/{patientId:guid}/external-documents", async (
            Guid patientId, HttpContext http, AnamnysDbContext db, FactQueue facts, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await ExternalDocumentService.ListAsync(db, providerId, patientId, ct) is { } items
                ? Results.Ok(items.Select(i => i with { Extracting = facts.IsBusy(i.Id) }))
                : Results.NotFound();
        });

        group.MapPost("external-documents/search", async (
            ExternalDocumentSearchRequest? request, HttpContext http, AnamnysDbContext db, FactQueue facts, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            request ??= new ExternalDocumentSearchRequest(null, null, null, null);
            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var found = await ExternalDocumentService.SearchAsync(db, providerId, request, ct);
            return Results.Ok(found with { Items = found.Items.Select(i => i with { Extracting = facts.IsBusy(i.Id) }).ToList() });
        });

        group.MapPost("external-documents/{id:guid}/open", async (Guid id, HttpContext http, AnamnysDbContext db, INoteFileStore store, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await ExternalDocumentService.OpenAsync(db, store, providerId, id, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                ExternalDocumentOutcome.Ok => Results.Ok(new { url = result.Url, expiresAt = result.ExpiresAt }),
                ExternalDocumentOutcome.NotFound => Results.NotFound(),
                _ => Results.Conflict(new { message = "O documento ainda não está disponível.", code = "not_available" }),
            };
        });

        group.MapDelete("external-documents/{id:guid}", async (Guid id, HttpContext http, AnamnysDbContext db, INoteFileStore store, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var outcome = await ExternalDocumentService.DeleteAsync(db, store, providerId, id, DateTimeOffset.UtcNow, ct);
            return outcome == ExternalDocumentOutcome.Ok ? Results.NoContent() : Refusal(outcome);
        });
    }

    private static IResult Refusal(ExternalDocumentOutcome outcome) => outcome switch
    {
        ExternalDocumentOutcome.NotFound => Results.NotFound(),
        ExternalDocumentOutcome.Archived => Results.Conflict(new { message = "Paciente arquivado: o prontuário é somente leitura.", code = "archived" }),
        ExternalDocumentOutcome.Empty => Results.BadRequest(new { message = "O arquivo está vazio.", code = "empty" }),
        ExternalDocumentOutcome.TooLarge => Results.BadRequest(new { message = "O arquivo excede 20 MB.", code = "too_large" }),
        ExternalDocumentOutcome.TypeMismatch => Results.BadRequest(new { message = "A extensão do arquivo não corresponde ao seu conteúdo.", code = "type_mismatch" }),
        _ => Results.BadRequest(new { message = "Envie um arquivo PDF, JPG ou PNG.", code = "unsupported_type" }),
    };
}
