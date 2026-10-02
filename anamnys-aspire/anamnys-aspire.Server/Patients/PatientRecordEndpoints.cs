using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authentication;

namespace Anamnys.Server.Patients;

// See design/specs/2026-10-01-patient-records-design.md §4. Handlers are thin: resolve the
// provider from the session, validate, delegate to PatientRecords, map the outcome. A
// patient another provider owns is 404, exactly like one that does not exist.
public static class PatientRecordEndpoints
{
    public static void MapPatientRecordEndpoints(this RouteGroupBuilder phi)
    {
        var patients = phi.MapGroup("providers/me/patients");

        patients.MapPost("", async (CreatePatientRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate(Today());
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var id = await PatientRecords.CreateAsync(db, providerId, request, DateTimeOffset.UtcNow, ct);
            return Results.Created($"/api/phi/providers/me/patients/{id}", new CreatedResponse(id));
        });

        patients.MapGet("{patientId:guid}", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var detail = await PatientRecords.GetAsync(db, providerId, patientId, DateTimeOffset.UtcNow, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        patients.MapPut("{patientId:guid}", async (Guid patientId, UpdatePatientRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate(Today());
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return await PatientRecords.UpdateAsync(db, providerId, patientId, request, DateTimeOffset.UtcNow, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        patients.MapDelete("{patientId:guid}", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await PatientRecords.DeleteAsync(db, providerId, patientId, ct) switch
            {
                RecordOutcome.Ok => Results.NoContent(),
                RecordOutcome.Conflict => Results.Conflict(new
                {
                    message = "Este paciente tem registros clínicos ou acesso ao portal e não pode ser excluído. Arquive-o.",
                }),
                _ => Results.NotFound(),
            };
        });

        patients.MapPost("{patientId:guid}/archive", (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            SetArchivedAsync(patientId, true, http, db, ct));
        patients.MapPost("{patientId:guid}/unarchive", (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            SetArchivedAsync(patientId, false, http, db, ct));

        // Diagnoses
        patients.MapPost("{patientId:guid}/diagnoses", async (Guid patientId, DiagnosisInput input, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = input.Validate(Today());
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return Created(await PatientRecords.AddDiagnosisAsync(db, providerId, patientId, input, DateTimeOffset.UtcNow, ct));
        });
        patients.MapPut("{patientId:guid}/diagnoses/{itemId:guid}", async (Guid patientId, Guid itemId, DiagnosisInput input, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = input.Validate(Today());
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return NoContentOrNotFound(await PatientRecords.UpdateDiagnosisAsync(db, providerId, patientId, itemId, input, ct));
        });
        patients.MapDelete("{patientId:guid}/diagnoses/{itemId:guid}", async (Guid patientId, Guid itemId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await ProviderIdAsync(http) is { } providerId
                ? NoContentOrNotFound(await PatientRecords.RemoveDiagnosisAsync(db, providerId, patientId, itemId, ct))
                : Results.Unauthorized());

        // Medications
        patients.MapPost("{patientId:guid}/medications", async (Guid patientId, MedicationInput input, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = input.Validate(Today());
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return Created(await PatientRecords.AddMedicationAsync(db, providerId, patientId, input, ct));
        });
        patients.MapPut("{patientId:guid}/medications/{itemId:guid}", async (Guid patientId, Guid itemId, MedicationInput input, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = input.Validate(Today());
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return NoContentOrNotFound(await PatientRecords.UpdateMedicationAsync(db, providerId, patientId, itemId, input, ct));
        });
        patients.MapDelete("{patientId:guid}/medications/{itemId:guid}", async (Guid patientId, Guid itemId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await ProviderIdAsync(http) is { } providerId
                ? NoContentOrNotFound(await PatientRecords.RemoveMedicationAsync(db, providerId, patientId, itemId, ct))
                : Results.Unauthorized());

        // Plan objectives
        patients.MapPost("{patientId:guid}/objectives", async (Guid patientId, ObjectiveInput input, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = input.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return Created(await PatientRecords.AddObjectiveAsync(db, providerId, patientId, input, DateTimeOffset.UtcNow, ct));
        });
        patients.MapPut("{patientId:guid}/objectives/{itemId:guid}", async (Guid patientId, Guid itemId, ObjectiveInput input, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = input.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return NoContentOrNotFound(await PatientRecords.UpdateObjectiveAsync(db, providerId, patientId, itemId, input, ct));
        });
        patients.MapDelete("{patientId:guid}/objectives/{itemId:guid}", async (Guid patientId, Guid itemId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await ProviderIdAsync(http) is { } providerId
                ? NoContentOrNotFound(await PatientRecords.RemoveObjectiveAsync(db, providerId, patientId, itemId, ct))
                : Results.Unauthorized());
    }

    private static async Task<IResult> SetArchivedAsync(Guid patientId, bool archived, HttpContext http, AnamnysDbContext db, CancellationToken ct)
    {
        if (await ProviderIdAsync(http) is not { } providerId)
        {
            return Results.Unauthorized();
        }
        return NoContentOrNotFound(await PatientRecords.SetArchivedAsync(db, providerId, patientId, archived, DateTimeOffset.UtcNow, ct));
    }

    // Realm in the path, provider cookie authenticated explicitly (see ProfileEndpoints).
    internal static async Task<Guid?> ProviderIdAsync(HttpContext http)
    {
        var auth = await http.AuthenticateAsync(AuthSchemes.ProviderCookie);
        return auth.Succeeded ? auth.Principal!.LocalIdOrNull() : null;
    }

    // "Not in the future" is judged on the practice's calendar, as the search's date filters are.
    private static DateOnly Today() => PatientSearchQuery.PracticeToday(DateTimeOffset.UtcNow);

    private static IResult Created(Guid? id) =>
        id is { } value ? Results.Json(new CreatedResponse(value), statusCode: StatusCodes.Status201Created) : Results.NotFound();

    private static IResult NoContentOrNotFound(bool found) => found ? Results.NoContent() : Results.NotFound();
}
