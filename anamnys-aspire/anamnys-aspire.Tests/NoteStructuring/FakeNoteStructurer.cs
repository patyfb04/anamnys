using Anamnys.Server.NoteStructuring;

namespace Anamnys.Tests.NoteStructuring;

internal sealed class FakeNoteStructurer(Func<string, string, IReadOnlyList<StructuredSection>> structure) : INoteStructurer
{
    public int Calls { get; private set; }
    public NoteSource? LastSource { get; private set; }

    public Task<IReadOnlyList<StructuredSection>> StructureAsync(string transcript, string format, NoteSource source, CancellationToken ct)
    {
        Calls++;
        LastSource = source;
        return Task.FromResult(structure(transcript, format));
    }
}
