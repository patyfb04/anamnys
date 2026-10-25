using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Anamnys.Server.Storage;

// Uploads go to the internal endpoint; URLs are signed for the public one, because SigV4 signs the
// host the browser will call.
public sealed class S3NoteFileStore(StorageOptions options) : INoteFileStore, IDisposable
{
    private readonly AmazonS3Client _service = Client(options.ServiceUrl, options);
    private readonly AmazonS3Client _public = Client(options.PublicUrl, options);

    public void Dispose()
    {
        _service.Dispose();
        _public.Dispose();
    }

    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        var buckets = await _service.ListBucketsAsync(ct);
        if (buckets.Buckets?.Any(b => b.BucketName == options.Bucket) != true)
        {
            await _service.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, ct);
        }
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        using var stream = new MemoryStream(content, writable: false);
        await PutAsync(key, stream, contentType, ct);
    }

    // The stream must be seekable so the SDK knows the length.
    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        await _service.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
        }, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            using var response = await _service.GetObjectAsync(options.Bucket, key, ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await _service.DeleteObjectAsync(options.Bucket, key, ct);

    // downloadFileName must not contain quotes (callers pass nota-{CODE}.pdf).
    public Uri PresignGet(string key, string downloadFileName, TimeSpan validFor, string contentType = "application/pdf") =>
        Presign(key, $"attachment; filename=\"{downloadFileName}\"", contentType, validFor);

    public Uri PresignGetInline(string key, string contentType, TimeSpan validFor) =>
        Presign(key, "inline", contentType, validFor);

    private Uri Presign(string key, string contentDisposition, string contentType, TimeSpan validFor)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validFor),
            Protocol = options.PublicUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP,
        };
        request.ResponseHeaderOverrides.ContentDisposition = contentDisposition;
        request.ResponseHeaderOverrides.ContentType = contentType;
        return new Uri(_public.GetPreSignedURL(request));
    }

    private static AmazonS3Client Client(string url, StorageOptions options) =>
        new(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config
        {
            ServiceURL = url,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });
}
