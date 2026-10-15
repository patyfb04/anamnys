using System.Net;
using System.Security.Cryptography;
using Anamnys.Server.LiveSession;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anamnys.Tests.LiveSession;

public sealed class WhisperModelProviderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"whisper-{Guid.NewGuid():N}");
    private readonly byte[] _served = "not a real model"u8.ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private WhisperModelProvider Provider(string sha256, StubHandler handler) => new(
        Options.Create(new LiveSessionOptions
        {
            Model = "test",
            ModelSha256 = sha256,
            ModelDirectory = _dir,
            ModelBaseUrl = new Uri("https://models.invalid/"),
        }),
        new StubFactory(handler),
        NullLogger<WhisperModelProvider>.Instance);

    private string ModelPath => Path.Combine(_dir, "ggml-test.bin");

    [Fact]
    public async Task EmptySha256_FailsClosed_WithoutDownloading()
    {
        var handler = new StubHandler(_served);
        using var provider = Provider("", handler);

        var act = () => provider.GetFactoryAsync(Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DownloadWithWrongHash_Throws_AndLeavesNoFiles()
    {
        var handler = new StubHandler(_served);
        using var provider = Provider(new string('0', 64), handler);

        var act = () => provider.GetFactoryAsync(Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Calls.Should().Be(1);
        File.Exists(ModelPath).Should().BeFalse();
        File.Exists(ModelPath + ".download").Should().BeFalse();
    }

    [Fact]
    public async Task ExistingFileWithWrongContent_IsRedownloadedAndReplaced()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(ModelPath, "corrupt", Ct);
        var handler = new StubHandler(_served);
        using var provider = Provider(Convert.ToHexStringLower(SHA256.HashData(_served)), handler);

        // The served bytes verify and are moved into place; whether loading them as a model
        // throws is up to Whisper.net, so the outcome is ignored and only the file is asserted.
        try
        {
            await provider.GetFactoryAsync(Ct);
        }
        catch (Exception)
        {
            // Junk bytes are not a model.
        }

        handler.Calls.Should().Be(1);
        (await File.ReadAllBytesAsync(ModelPath, Ct)).Should().Equal(_served);
        File.Exists(ModelPath + ".download").Should().BeFalse();
    }
}
