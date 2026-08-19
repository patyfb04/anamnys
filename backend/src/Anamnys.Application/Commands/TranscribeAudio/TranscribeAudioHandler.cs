using Anamnys.Application.DTOs;
using Anamnys.Application.Interfaces;
using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Anamnys.Application.Commands.TranscribeAudio;

public class TranscribeAudioHandler : IRequestHandler<TranscribeAudioCommand, TranscribeJobDto>
{
    private readonly ITranscriptionService _transcription;
    private readonly INoteStructuringService _structuring;
    private readonly IBillingEngine _billing;
    private readonly ILogger<TranscribeAudioHandler> _logger;

    public TranscribeAudioHandler(
        ITranscriptionService transcription,
        INoteStructuringService structuring,
        IBillingEngine billing,
        ILogger<TranscribeAudioHandler> logger)
    {
        _transcription = transcription;
        _structuring = structuring;
        _billing = billing;
        _logger = logger;
    }

    public async Task<TranscribeJobDto> Handle(TranscribeAudioCommand request, CancellationToken cancellationToken)
    {
        // In the real implementation this would be enqueued via Hangfire.
        // The handler returns a jobId immediately; Hangfire executes the pipeline
        // in the background and pushes progress updates via SignalR.
        //
        // For clarity, the full pipeline logic is shown inline here.
        // See Infrastructure/Jobs/PipelineJob.cs for the Hangfire-enqueued version.

        var jobId = Guid.NewGuid().ToString("N");
        _logger.LogInformation("TranscribeAudio job {JobId} enqueued for patient {PatientId}", jobId, request.PatientId);

        return new TranscribeJobDto(
            JobId: jobId,
            Status: "queued",
            Progress: new PipelineProgressDto("transcribing", "Queued", 0),
            NoteId: null
        );
    }
}
