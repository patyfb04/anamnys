namespace Anamnys.Application.Interfaces;

public interface IStorageService
{
    /// <summary>Uploads a file stream and returns a public or presigned URL.</summary>
    Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct = default);

    /// <summary>Returns a presigned download URL valid for the given duration.</summary>
    Task<string> GetPresignedUrlAsync(string key, TimeSpan validity, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}
