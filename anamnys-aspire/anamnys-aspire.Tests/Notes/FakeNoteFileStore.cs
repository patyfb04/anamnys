using Anamnys.Server.Storage;

namespace Anamnys.Tests.Notes;

internal sealed class FakeNoteFileStore : INoteFileStore
{
    public Dictionary<string, byte[]> Objects { get; } = [];
    public int Puts { get; private set; }
    public bool Fail { get; set; }
    public Exception? FailWith { get; set; }

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }
        if (Fail)
        {
            throw new HttpRequestException("store down");
        }
        Puts++;
        Objects[key] = content;
        return Task.CompletedTask;
    }

    public Uri PresignGet(string key, string downloadFileName, TimeSpan validFor) =>
        new($"http://store.test/{key}?name={downloadFileName}&ttl={(int)validFor.TotalSeconds}");
}
