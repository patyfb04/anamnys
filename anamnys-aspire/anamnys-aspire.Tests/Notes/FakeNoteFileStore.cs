using Anamnys.Server.Storage;

namespace Anamnys.Tests.Notes;

internal sealed class FakeNoteFileStore : INoteFileStore
{
    public Dictionary<string, byte[]> Objects { get; } = [];
    public int Puts { get; private set; }
    public bool Fail { get; set; }
    public Exception? FailWith { get; set; }
    public List<string> Deleted { get; } = [];
    public bool FailDelete { get; set; }

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

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        await PutAsync(key, buffer.ToArray(), contentType, ct);
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken ct) =>
        Task.FromResult(Objects.TryGetValue(key, out var content) ? content : null);

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        if (FailDelete)
        {
            throw new HttpRequestException("store down");
        }
        Objects.Remove(key);
        Deleted.Add(key);
        return Task.CompletedTask;
    }

    public Uri PresignGet(string key, string downloadFileName, TimeSpan validFor, string contentType = "application/pdf") =>
        new($"http://store.test/{key}?name={downloadFileName}&ttl={(int)validFor.TotalSeconds}");

    public Uri PresignGetInline(string key, string contentType, TimeSpan validFor) =>
        new($"http://store.test/{key}?inline=1&type={contentType}&ttl={(int)validFor.TotalSeconds}");
}
