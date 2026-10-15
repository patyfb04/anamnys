namespace Anamnys.Server.NoteStructuring;

public sealed record StructuredSection(string Key, string Content);

public sealed class TranscriptTooLongException : Exception;

public interface INoteStructurer
{
    Task<IReadOnlyList<StructuredSection>> StructureAsync(string transcript, string format, CancellationToken ct);
}
