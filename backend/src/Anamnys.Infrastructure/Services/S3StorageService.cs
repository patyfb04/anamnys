using Amazon.S3;
using Amazon.S3.Model;
using Anamnys.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anamnys.Infrastructure.Services;

public class S3StorageOptions
{
    public string BucketName { get; set; } = "clinical-draft-audio";
    public string ServiceUrl { get; set; } = "https://s3.us-east-005.backblazeb2.com"; // or Cloudflare R2 endpoint
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-005";
    public int PresignedUrlExpiryHours { get; set; } = 1;
}

/// <summary>
/// S3-compatible storage service. Works with Cloudflare R2, Backblaze B2, MinIO.
/// Switch providers by changing ServiceUrl + credentials.
/// </summary>
public class S3StorageService : IStorageService
{
    private readonly AmazonS3Client _client;
    private readonly S3StorageOptions _opts;
    private readonly ILogger<S3StorageService> _logger;

    public S3StorageService(IOptions<S3StorageOptions> opts, ILogger<S3StorageService> logger)
    {
        _opts = opts.Value;
        _logger = logger;
        var config = new AmazonS3Config
        {
            ServiceURL = _opts.ServiceUrl,
            ForcePathStyle = true, // required for R2 / B2 / MinIO
        };
        _client = new AmazonS3Client(_opts.AccessKey, _opts.SecretKey, config);
    }

    public async Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
        };
        await _client.PutObjectAsync(request, ct);
        _logger.LogInformation("Uploaded {Key} to {Bucket}", key, _opts.BucketName);
        return key;
    }

    public async Task<string> GetPresignedUrlAsync(string key, TimeSpan validity, CancellationToken ct = default)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
            Expires = DateTime.UtcNow.Add(validity),
            Verb = HttpVerb.GET,
        };
        return _client.GetPreSignedURL(request);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await _client.DeleteObjectAsync(_opts.BucketName, key, ct);
    }
}
