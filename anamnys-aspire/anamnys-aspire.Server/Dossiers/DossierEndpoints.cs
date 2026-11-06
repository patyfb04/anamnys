using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;

namespace Anamnys.Server.Dossiers;

// design/specs/2026-11-04-patient-dossier-design.md §3. PHI group (401 before anything), provider-scoped, 404 for another
// provider's patient or dossier, every answer Cache-Control: no-store. No route or file name carries the patient's name.
public static class DossierEndpoints
{
    public static void AddPatientDossier(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<DossierQueue>();
        builder.Services.AddHostedService<DossierSweep>();
        builder.Services.AddHostedService<DossierWorker>();
    }

    public static void MapDossierEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapPost("patients/{patientId:guid}/dossiers", async (
            Guid patientId, DossierRequestBody? body, HttpContext http, AnamnysDbContext db, DossierQueue queue, TimeProvider clock,
            CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await RequestAsync(http, db, queue, providerId, patientId, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        phi.MapGet("patients/{patientId:guid}/dossiers", async (
            Guid patientId, int? page, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ListAsync(http, db, providerId, patientId, page ?? 1, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        phi.MapPost("dossiers/{id:guid}/download", async (
            Guid id, HttpContext http, AnamnysDbContext db, INoteFileStore store, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await DownloadAsync(http, db, store, providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        phi.MapGet("dossiers/{id:guid}/receipt.pdf", async (
            Guid id, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ReceiptAsync(http, db, providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        phi.MapGet("dossiers/{id:guid}/timestamp.tsr", async (
            Guid id, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await TokenAsync(http, db, providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());
    }

    // The ZIP's RFC 3161 token as raw DER, for `openssl ts -verify -data <zip> -in <tsr> -token_in -CAfile <root>`.
    internal static async Task<IResult> TokenAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var result = await DossierService.TokenAsync(db, providerId, id, now, ct);
        if (result.Outcome != DossierOutcome.Ok)
        {
            return Problem(result.Outcome);
        }
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{result.FileName}\"";
        return Results.Bytes(result.Token!, "application/octet-stream");
    }

    internal static async Task<IResult> RequestAsync(
        HttpContext http, AnamnysDbContext db, DossierQueue queue, Guid providerId, Guid patientId, DossierRequestBody? body,
        DateTimeOffset now, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var result = await DossierService.RequestAsync(db, providerId, patientId, body, now, ct);
        if (result.Outcome == DossierOutcome.Ok)
        {
            queue.Enqueue(new DossierJob(result.Id!.Value, providerId));
            return Results.Accepted(value: new { id = result.Id, status = result.Status });
        }
        return Problem(result.Outcome);
    }

    internal static async Task<IResult> ListAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid patientId, int page, DateTimeOffset now, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        return await DossierService.ListAsync(db, providerId, patientId, page, now, ct) is { } list
            ? Results.Ok(list)
            : Results.NotFound();
    }

    internal static async Task<IResult> DownloadAsync(
        HttpContext http, AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var result = await DossierService.DownloadAsync(db, store, providerId, id, now, ct);
        return result.Outcome == DossierOutcome.Ok
            ? Results.Ok(new { url = result.Url, expiresAt = result.ExpiresAt })
            : Problem(result.Outcome);
    }

    internal static async Task<IResult> ReceiptAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var result = await DossierService.ReceiptAsync(db, providerId, id, now, ct);
        if (result.Outcome != DossierOutcome.Ok)
        {
            return Problem(result.Outcome);
        }
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{result.FileName}\"";
        return Results.Bytes(result.Pdf!, "application/pdf");
    }

    private static IResult Problem(DossierOutcome outcome) => outcome switch
    {
        DossierOutcome.NotFound => Results.NotFound(),
        DossierOutcome.InvalidReason => Results.BadRequest(new { message = "Escolha o motivo do dossiê.", code = "invalid_reason" }),
        DossierOutcome.ReasonNoteRequired => Results.BadRequest(
            new { message = $"Descreva o motivo em até {DossierService.MaxReasonNote} caracteres.", code = "reason_note_required" }),
        DossierOutcome.InvalidPeriod => Results.BadRequest(
            new { message = "A data inicial do período deve ser anterior ou igual à data final.", code = "invalid_period" }),
        DossierOutcome.InProgress => Results.Conflict(
            new { message = "Já existe um dossiê sendo gerado para este paciente.", code = "dossier_in_progress" }),
        DossierOutcome.Expired => Results.Conflict(new { message = "Este dossiê expirou. Gere um novo.", code = "expired" }),
        _ => Results.Conflict(new { message = "O dossiê ainda não está pronto.", code = "not_ready" }),
    };
}
