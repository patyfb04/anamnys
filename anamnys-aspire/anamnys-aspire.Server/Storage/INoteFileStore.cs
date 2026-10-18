namespace Anamnys.Server.Storage;

// Private object storage for exported files (design/specs/2026-10-17-note-pdf-export-design.md §5.1).
public interface INoteFileStore
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);

    Uri PresignGet(string key, string downloadFileName, TimeSpan validFor);
}
