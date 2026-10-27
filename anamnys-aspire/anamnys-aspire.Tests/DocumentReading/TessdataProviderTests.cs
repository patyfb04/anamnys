using System.Net;
using System.Security.Cryptography;
using Anamnys.Server.DocumentReading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anamnys.Tests.DocumentReading;

public sealed class TessdataProviderTests : IDisposable
{
    private static readonly byte[] Data = "not real tessdata"u8.ToArray();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tessdata-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private TessdataProvider Provider(string sha, StubHandler handler) => new(
        Options.Create(new DocumentReadingOptions
        {
            TessdataDirectory = _dir,
            TessdataUrl = new Uri("https://tessdata.invalid/por.traineddata"),
            TessdataSha256 = sha,
        }),
        new StubFactory(handler),
        NullLogger<TessdataProvider>.Instance);

    [Fact]
    public async Task GetDirectoryAsync_WithoutPinnedHash_FailsClosedWithoutDownloading()
    {
        // Arrange
        var handler = new StubHandler(Data);

        // Act
        var act = () => Provider("", handler).GetDirectoryAsync(Ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDirectoryAsync_HashMismatch_RejectsTheFileAndLeavesNothingBehind()
    {
        // Arrange
        var handler = new StubHandler(Data);

        // Act
        var act = () => Provider(new string('0', 64), handler).GetDirectoryAsync(Ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public async Task GetDirectoryAsync_VerifiedDownload_IsPlacedOnce_AndReused()
    {
        // Arrange
        var handler = new StubHandler(Data);
        var provider = Provider(Convert.ToHexStringLower(SHA256.HashData(Data)), handler);

        // Act
        var first = await provider.GetDirectoryAsync(Ct);
        var second = await provider.GetDirectoryAsync(Ct);

        // Assert
        first.Should().Be(_dir).And.Be(second);
        File.ReadAllBytes(Path.Combine(_dir, "por.traineddata")).Should().Equal(Data);
        handler.Requested.Should().ContainSingle();
    }
}
