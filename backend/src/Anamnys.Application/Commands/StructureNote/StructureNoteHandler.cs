using Anamnys.Application.DTOs;
using Anamnys.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Anamnys.Application.Commands.StructureNote;

public class StructureNoteHandler : IRequestHandler<StructureNoteCommand, NoteDto>
{
    private readonly INoteStructuringService _structuring;
    private readonly IBillingEngine _billing;
    private readonly ILogger<StructureNoteHandler> _logger;

    public StructureNoteHandler(
        INoteStructuringService structuring,
        IBillingEngine billing,
        ILogger<StructureNoteHandler> logger)
    {
        _structuring = structuring;
        _billing = billing;
        _logger = logger;
    }

    public async Task<NoteDto> Handle(StructureNoteCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Structuring note {NoteId}", request.NoteId);

        var structured = await _structuring.StructureAsync(
            request.RawTranscript,
            request.Format,
            request.Specialty,
            cancellationToken);

        var billingCodes = await _billing.DeriveCodesAsync(
            structured,
            request.Specialty,
            ct: cancellationToken);

        // Persistence and SignalR push happen in the Hangfire pipeline job.
        // This handler returns the structured content for direct use in tests.
        return new NoteDto(
            Id: request.NoteId,
            PatientId: Guid.Empty,
            ProviderId: Guid.Empty,
            Status: "ready_for_review",
            InputMode: "voice_batch",
            RawTranscript: request.RawTranscript,
            StructuredContent: new StructuredNoteDto(
                Format: structured.Format.ToString(),
                Sections: structured.Sections,
                GeneratedAt: structured.GeneratedAt),
            BillingCodes: billingCodes.Select(b => new BillingCodeDto(
                b.CptCode, b.Icd10Code, b.Description,
                b.ConfidenceScore, b.DenialRiskScore,
                b.Modifiers, b.MissingDocumentation)).ToList(),
            PriorAuthLetter: null,
            AuditTrail: [],
            CreatedAt: DateTimeOffset.UtcNow,
            SignedAt: null
        );
    }
}
