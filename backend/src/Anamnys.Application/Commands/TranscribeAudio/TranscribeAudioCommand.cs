using Anamnys.Application.DTOs;
using MediatR;

namespace Anamnys.Application.Commands.TranscribeAudio;

/// <summary>
/// Enqueues an audio file for the full 5-step AI pipeline.
/// Returns immediately with a jobId; progress is pushed via SignalR.
/// </summary>
public record TranscribeAudioCommand(
    Guid ProviderId,
    Guid PatientId,
    string AudioFilePath,
    string MimeType
) : IRequest<TranscribeJobDto>;
