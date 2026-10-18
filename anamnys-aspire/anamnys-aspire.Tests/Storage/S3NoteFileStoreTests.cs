using System.Net;
using System.Security.Cryptography;
using Anamnys.Server.Storage;
using FluentAssertions;

namespace Anamnys.Tests.Storage;

[Collection(SharedAppHostCollection.Name)]
public class S3NoteFileStoreTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PresignGet_AfterPut_DownloadsSameBytesAsAttachment()
    {
        // Arrange
        using var store = new S3NoteFileStore(fixture.StorageOptions);
        await store.EnsureBucketAsync(Ct);
        var key = $"tests/{Guid.NewGuid()}.pdf";
        var content = "%PDF-1.7 test"u8.ToArray();
        await store.PutAsync(key, content, "application/pdf", Ct);

        // Act
        var url = store.PresignGet(key, "nota-ABCDEF12.pdf", TimeSpan.FromHours(1));
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, Ct);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Content.Headers.ContentDisposition!.FileName!.Trim('"').Should().Be("nota-ABCDEF12.pdf");
        SHA256.HashData(await response.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(SHA256.HashData(content));
    }

    [Fact]
    public async Task Get_WithoutSignature_IsRefused()
    {
        // Arrange
        using var store = new S3NoteFileStore(fixture.StorageOptions);
        await store.EnsureBucketAsync(Ct);
        var key = $"tests/{Guid.NewGuid()}.pdf";
        await store.PutAsync(key, "%PDF"u8.ToArray(), "application/pdf", Ct);

        // Act
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(new Uri(fixture.StorageOptions.PublicUrl), $"{fixture.StorageOptions.Bucket}/{key}"), Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListBucket_WithoutSignature_IsForbidden()
    {
        // Arrange
        using var store = new S3NoteFileStore(fixture.StorageOptions);
        await store.EnsureBucketAsync(Ct);

        // Act
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(new Uri(fixture.StorageOptions.PublicUrl), $"{fixture.StorageOptions.Bucket}/"), Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
