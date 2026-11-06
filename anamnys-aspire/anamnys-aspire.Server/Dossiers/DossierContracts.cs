namespace Anamnys.Server.Dossiers;

// POST /api/phi/patients/{patientId}/dossiers (design/specs/2026-11-04-patient-dossier-design.md §3). Reason is crp,
// court, patient_request or other; ReasonNote (1..200) only for other. Periods are practice dates, both optional.
public sealed record DossierRequestBody(string? Reason, string? ReasonNote, DateOnly? PeriodFrom, DateOnly? PeriodTo);

// One line of the patient's dossier list; the reason note is never listed. IntegrityVerified is null until generated.
public sealed record DossierListItem(
    Guid Id, int Number, string Status, string Reason, DateOnly? PeriodFrom, DateOnly? PeriodTo, DateTimeOffset RequestedAt,
    DateTimeOffset? GeneratedAt, DateTimeOffset? ExpiresAt, long? SizeBytes, bool Anchored, bool? IntegrityVerified);

public sealed record DossierListPage(IReadOnlyList<DossierListItem> Items, int Total, int Page, int PageSize);
