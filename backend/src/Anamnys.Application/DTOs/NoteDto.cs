using Anamnys.Domain.Enums;

namespace Anamnys.Application.DTOs;

public record NoteDto(
    Guid Id,
    Guid PatientId,
    Guid ProviderId,
    string Status,
    string InputMode,
    string? RawTranscript,
    StructuredNoteDto? StructuredContent,
    IReadOnlyList<BillingCodeDto> BillingCodes,
    string? PriorAuthLetter,
    IReadOnlyList<AuditEntryDto> AuditTrail,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SignedAt
);

public record StructuredNoteDto(
    string Format,
    Dictionary<string, string> Sections,
    DateTimeOffset GeneratedAt
);

public record BillingCodeDto(
    string CptCode,
    string Icd10Code,
    string Description,
    double ConfidenceScore,
    int DenialRiskScore,
    IReadOnlyList<string> Modifiers,
    IReadOnlyList<string> MissingDocumentation
);

public record AuditEntryDto(
    DateTimeOffset Timestamp,
    string Action,
    string? FieldChanged,
    string? PreviousValue,
    string? NewValue,
    Guid ActorId
);

public record TranscribeJobDto(
    string JobId,
    string Status,
    PipelineProgressDto? Progress,
    Guid? NoteId
);

public record PipelineProgressDto(
    string Step,
    string Label,
    int Percentage
);

public record PatientDto(
    Guid Id,
    Guid ProviderId,
    string FirstName,
    string LastName,
    DateOnly DateOfBirth,
    IReadOnlyList<string> Diagnoses,
    IReadOnlyList<string> CurrentMedications,
    string? TreatmentPlan,
    string? PreferredLanguage,
    DateTimeOffset? LastVisit
);

public record CreatePatientRequest(
    string FirstName,
    string LastName,
    DateOnly DateOfBirth,
    IReadOnlyList<string>? Diagnoses,
    IReadOnlyList<string>? CurrentMedications,
    string? TreatmentPlan,
    string? PreferredLanguage
);

public record AuthUserDto(
    Guid Id,
    string Email,
    string Name,
    string Specialty,
    string Token
);

public record PaginatedDto<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize
);
