using Anamnys.Application.DTOs;
using Anamnys.Domain.Enums;
using MediatR;

namespace Anamnys.Application.Commands.StructureNote;

public record StructureNoteCommand(
    Guid NoteId,
    string RawTranscript,
    NoteFormat Format,
    Specialty Specialty
) : IRequest<NoteDto>;
