using Anamnys.Application.DTOs;
using Anamnys.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Anamnys.Api.Controllers;

[ApiController]
[Route("api/patients")]
[Authorize]
public class PatientsController : ControllerBase
{
    private readonly AppDbContext _db;

    public PatientsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<PaginatedDto<PatientDto>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var providerId = GetProviderId();

        var query = _db.Patients.Where(p => p.ProviderId == providerId);
        var total = await query.CountAsync();
        var items = await query
            // Postgres defaults to NULLS FIRST on DESC — that would put patients with no
            // visits yet ahead of ones with real visit history. Force nulls last instead.
            .OrderByDescending(p => p.LastVisit.HasValue)
            .ThenByDescending(p => p.LastVisit)
            .ThenByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => ToDto(p))
            .ToListAsync();

        return Ok(new PaginatedDto<PatientDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PatientDto>> Get(Guid id)
    {
        var providerId = GetProviderId();
        var p = await _db.Patients.FirstOrDefaultAsync(
            x => x.Id == id && x.ProviderId == providerId);
        return p == null ? NotFound() : Ok(ToDto(p));
    }

    [HttpPost]
    public async Task<ActionResult<PatientDto>> Create([FromBody] CreatePatientRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
            return BadRequest(new { message = "First and last name are required." });

        if (!Domain.SupportedLanguages.IsValid(request.PreferredLanguage))
            return BadRequest(new { message = $"Unsupported language code '{request.PreferredLanguage}'." });

        var patient = new Domain.Entities.Patient
        {
            ProviderId = GetProviderId(),
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            DateOfBirth = request.DateOfBirth,
            Diagnoses = request.Diagnoses?.Where(d => !string.IsNullOrWhiteSpace(d)).ToList() ?? new(),
            CurrentMedications = request.CurrentMedications?.Where(m => !string.IsNullOrWhiteSpace(m)).ToList() ?? new(),
            TreatmentPlan = string.IsNullOrWhiteSpace(request.TreatmentPlan) ? null : request.TreatmentPlan.Trim(),
            PreferredLanguage = string.IsNullOrWhiteSpace(request.PreferredLanguage) ? null : request.PreferredLanguage.Trim().ToLowerInvariant(),
        };

        _db.Patients.Add(patient);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = patient.Id }, ToDto(patient));
    }

    private Guid GetProviderId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static PatientDto ToDto(Domain.Entities.Patient p) => new(
        p.Id, p.ProviderId, p.FirstName, p.LastName,
        p.DateOfBirth, p.Diagnoses, p.CurrentMedications,
        p.TreatmentPlan, p.PreferredLanguage, p.LastVisit);
}
