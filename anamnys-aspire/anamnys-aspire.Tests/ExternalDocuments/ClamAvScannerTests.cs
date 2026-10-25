using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Anamnys.Server.ExternalDocuments;
using FluentAssertions;

namespace Anamnys.Tests.ExternalDocuments;

public class ClamAvScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("stream: OK")]
    [InlineData("stream: OK\0")]
    [InlineData("stream: OK\n")]
    public void ParseVerdict_Ok_IsClean(string reply)
    {
        // Act
        var verdict = ClamdProtocol.ParseVerdict(reply);

        // Assert
        verdict.Clean.Should().BeTrue();
        verdict.Signature.Should().BeNull();
    }

    [Theory]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND", "Win.Test.EICAR_HDB-1")]
    [InlineData("stream: Eicar-Signature FOUND\0", "Eicar-Signature")]
    public void ParseVerdict_Found_IsInfectedWithTheSignatureName(string reply, string signature)
    {
        // Act
        var verdict = ClamdProtocol.ParseVerdict(reply);

        // Assert
        verdict.Clean.Should().BeFalse();
        verdict.Signature.Should().Be(signature);
    }

    [Theory]
    [InlineData("INSTREAM size limit exceeded. ERROR")]
    [InlineData("stream: Can't allocate memory ERROR\0")]
    [InlineData("")]
    [InlineData("PONG")]
    [InlineData("something unexpected")]
    public void ParseVerdict_ErrorOrUnknownReply_GivesNoVerdict(string reply)
    {
        // Act
        var act = () => ClamdProtocol.ParseVerdict(reply);

        // Assert
        act.Should().Throw<ScanException>();
    }

    [Fact]
    public async Task WriteInstream_FramesTheCommandChunksAndTheTerminator()
    {
        // Arrange
        var content = new byte[ClamdProtocol.ChunkSize + 10];
        new Random(1).NextBytes(content);
        using var wire = new MemoryStream();

        // Act
        await ClamdProtocol.WriteInstreamAsync(wire, content, Ct);

        // Assert
        var bytes = wire.ToArray();
        Encoding.ASCII.GetString(bytes, 0, 10).Should().Be("zINSTREAM\0");
        var position = 10;
        var received = new List<byte>();
        var chunks = new List<int>();
        while (true)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position, 4));
            position += 4;
            if (length == 0)
            {
                break;
            }
            chunks.Add(length);
            received.AddRange(bytes.AsSpan(position, length).ToArray());
            position += length;
        }
        chunks.Should().Equal(ClamdProtocol.ChunkSize, 10);
        received.Should().Equal(content);
        position.Should().Be(bytes.Length);
    }

    [Fact]
    public async Task WriteInstream_EmptyContent_SendsOnlyTheTerminator()
    {
        // Arrange
        using var wire = new MemoryStream();

        // Act
        await ClamdProtocol.WriteInstreamAsync(wire, [], Ct);

        // Assert
        wire.ToArray().Skip(10).Should().Equal(0, 0, 0, 0);
    }

    [Fact]
    public async Task ReadReply_StopsAtTheNul()
    {
        // Arrange
        using var wire = new MemoryStream(Encoding.ASCII.GetBytes("stream: OK\0trailing"));

        // Act
        var reply = await ClamdProtocol.ReadReplyAsync(wire, Ct);

        // Assert
        reply.Should().Be("stream: OK");
    }

    [Fact]
    public async Task Scan_AgainstAClamdThatAnswersOk_IsClean_AndSendsTheContentInChunks()
    {
        // Arrange
        var content = new byte[3 * ClamdProtocol.ChunkSize / 2];
        new Random(2).NextBytes(content);
        using var clamd = new FakeClamd("stream: OK\0");
        var scanner = new ClamAvScanner(new ExternalDocumentOptions { ClamAvHost = "127.0.0.1", ClamAvPort = clamd.Port });

        // Act
        var verdict = await scanner.ScanAsync(content, Ct);

        // Assert
        verdict.Clean.Should().BeTrue();
        var received = await clamd.Received;
        received.Take(10).Should().Equal(Encoding.ASCII.GetBytes("zINSTREAM\0"));
        received.Length.Should().Be(10 + 4 + ClamdProtocol.ChunkSize + 4 + ClamdProtocol.ChunkSize / 2 + 4);
    }

    [Fact]
    public async Task Scan_AgainstAClamdThatFindsAVirus_IsInfected()
    {
        // Arrange
        using var clamd = new FakeClamd("stream: Eicar-Test-Signature FOUND\0");
        var scanner = new ClamAvScanner(new ExternalDocumentOptions { ClamAvHost = "127.0.0.1", ClamAvPort = clamd.Port });

        // Act
        var verdict = await scanner.ScanAsync("data"u8.ToArray(), Ct);

        // Assert
        verdict.Clean.Should().BeFalse();
        verdict.Signature.Should().Be("Eicar-Test-Signature");
    }

    [Fact]
    public async Task Scan_AgainstAClamdThatReportsAnError_Throws()
    {
        // Arrange
        using var clamd = new FakeClamd("INSTREAM size limit exceeded. ERROR\0");
        var scanner = new ClamAvScanner(new ExternalDocumentOptions { ClamAvHost = "127.0.0.1", ClamAvPort = clamd.Port });

        // Act
        var act = () => scanner.ScanAsync("data"u8.ToArray(), Ct);

        // Assert
        await act.Should().ThrowAsync<ScanException>();
    }

    [Fact]
    public async Task Scan_NothingListening_Throws()
    {
        // Arrange
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var scanner = new ClamAvScanner(new ExternalDocumentOptions { ClamAvHost = "127.0.0.1", ClamAvPort = port });

        // Act
        var act = () => scanner.ScanAsync("data"u8.ToArray(), Ct);

        // Assert
        await act.Should().ThrowAsync<ScannerUnavailableException>();
    }

    [Fact]
    public async Task FakeScanner_FlagsTheEicarString_AndNothingElse()
    {
        // Arrange
        var scanner = new FakeVirusScanner();
        var eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

        // Act
        var infected = await scanner.ScanAsync([.. "%PDF-1.4\n"u8, .. eicar], Ct);
        var clean = await scanner.ScanAsync("%PDF-1.4\nhello"u8.ToArray(), Ct);

        // Assert
        infected.Clean.Should().BeFalse();
        clean.Clean.Should().BeTrue();
    }

    // Accepts one connection, reads until the zero-length terminator and answers.
    private sealed class FakeClamd : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public FakeClamd(string reply)
        {
            _listener.Start();
            Received = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var all = new List<byte>();
                var buffer = new byte[8192];
                while (!EndsWithTerminator(all))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0)
                    {
                        break;
                    }
                    all.AddRange(buffer.AsSpan(0, read).ToArray());
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
                return all.ToArray();
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<byte[]> Received { get; }

        public void Dispose() => _listener.Stop();

        // Walks the frames after the command, so payload bytes that happen to be zero are not mistaken for the end.
        private static bool EndsWithTerminator(List<byte> all)
        {
            if (all.Count < 14)
            {
                return false;
            }
            var position = 10;
            while (position + 4 <= all.Count)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(all.GetRange(position, 4).ToArray());
                if (length == 0)
                {
                    return true;
                }
                position += 4 + length;
            }
            return false;
        }
    }
}
