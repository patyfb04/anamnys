using Anamnys.Server.NoteStructuring;

namespace Anamnys.Tests.NoteStructuring;

internal sealed class FakeNoteStructurer(Func<string, string, IReadOnlyList<StructuredSection>> structure) : INoteStructurer
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<StructuredSection>> StructureAsync(string transcript, string format, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(structure(transcript, format));
    }
}
