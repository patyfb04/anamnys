using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;

namespace Anamnys.Application.Interfaces;

public interface INoteStructuringService
{
    /// <summary>
    /// Uses LLamaSharp (Phi-4 14B) to structure a raw transcript into the given note format.
    /// </summary>
    Task<StructuredNote> StructureAsync(
        string rawTranscript,
        NoteFormat format,
        Specialty specialty,
        CancellationToken ct = default);
}
