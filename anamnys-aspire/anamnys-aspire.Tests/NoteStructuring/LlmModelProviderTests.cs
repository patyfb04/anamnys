using System.Net;
using System.Security.Cryptography;
using Anamnys.Server.NoteStructuring;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anamnys.Tests.NoteStructuring;

public sealed class LlmModelProviderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"llm-{Guid.NewGuid():N}");
    private readonly Dictionary<string, byte[]> _served = new()
    {
        ["part-1.gguf"] = "not a real model, part one"u8.ToArray(),
        ["part-2.gguf"] = "not a real model, part two"u8.ToArray(),
    };

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class StubHandler(Dictionary<string, byte[]> bodies) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = request.RequestUri!.Segments[^1];
            Requested.Add(name);
            return Task.FromResult(bodies.TryGetValue(name, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private LlmModelProvider Provider(List<NoteStructuringOptions.ModelFile> files, StubHandler handler) => new(
        Options.Create(new NoteStructuringOptions
        {
            ModelDirectory = _dir,
            ModelBaseUrl = new Uri("https://models.invalid/"),
            Files = files,
        }),
        new StubFactory(handler),
        NullLogger<LlmModelProvider>.Instance);

    private string PathOf(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task EmptyFiles_FailsClosed_WithoutDownloading()
    {
        var handler = new StubHandler(_served);
        using var provider = Provider([], handler);

        var act = () => provider.GetAsync(Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingSha256_FailsClosed_WithoutDownloading()
    {
        var handler = new StubHandler(_served);
        using var provider = Provider(
            [new() { Name = "part-1.gguf", Sha256 = Sha(_served["part-1.gguf"]) }, new() { Name = "part-2.gguf", Sha256 = "" }],
            handler);

        var act = () => provider.GetAsync(Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadWithWrongHash_Throws_AndLeavesNoFiles()
    {
        var handler = new StubHandler(_served);
        using var provider = Provider([new() { Name = "part-1.gguf", Sha256 = new string('0', 64) }], handler);

        var act = () => provider.GetAsync(Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requested.Should().Equal("part-1.gguf");
        File.Exists(PathOf("part-1.gguf")).Should().BeFalse();
        File.Exists(PathOf("part-1.gguf.download")).Should().BeFalse();
    }

    [Fact]
    public async Task BothParts_WithPinnedHashes_AreDownloadedIntoTheDirectory()
    {
        var handler = new StubHandler(_served);
        using var provider = Provider(
            [
                new() { Name = "part-1.gguf", Sha256 = Sha(_served["part-1.gguf"]) },
                new() { Name = "part-2.gguf", Sha256 = Sha(_served["part-2.gguf"]) },
            ],
            handler);

        // Both parts verify and are moved into place; loading junk bytes as a model fails in
        // llama.cpp, so the outcome is ignored and only the files are asserted.
        try
        {
            await provider.GetAsync(Ct);
        }
        catch (Exception)
        {
            // Junk bytes are not a model.
        }

        handler.Requested.Should().Equal("part-1.gguf", "part-2.gguf");
        (await File.ReadAllBytesAsync(PathOf("part-1.gguf"), Ct)).Should().Equal(_served["part-1.gguf"]);
        (await File.ReadAllBytesAsync(PathOf("part-2.gguf"), Ct)).Should().Equal(_served["part-2.gguf"]);
        Directory.GetFiles(_dir, "*.download").Should().BeEmpty();
    }
}
