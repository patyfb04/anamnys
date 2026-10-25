using System.Buffers.Binary;
using System.Text;

namespace Anamnys.Server.ExternalDocuments;

// The clamd wire protocol, null-terminated commands ("z" prefix): INSTREAM sends length-prefixed chunks
// (4-byte big-endian) closed by a zero length and is answered with one line.
public static class ClamdProtocol
{
    public const int ChunkSize = 64 * 1024;
    private static readonly byte[] InstreamCommand = Encoding.ASCII.GetBytes("zINSTREAM\0");

    public static async Task WriteInstreamAsync(Stream stream, byte[] content, CancellationToken ct)
    {
        await stream.WriteAsync(InstreamCommand, ct);
        var header = new byte[4];
        for (var offset = 0; offset < content.Length; offset += ChunkSize)
        {
            var length = Math.Min(ChunkSize, content.Length - offset);
            BinaryPrimitives.WriteInt32BigEndian(header, length);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(content.AsMemory(offset, length), ct);
        }
        BinaryPrimitives.WriteInt32BigEndian(header, 0);
        await stream.WriteAsync(header, ct);
        await stream.FlushAsync(ct);
    }

    // The reply up to its terminating NUL (or the end of the stream).
    public static async Task<string> ReadReplyAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 4096 && await stream.ReadAsync(one, ct) == 1 && one[0] != 0)
        {
            buffer.Add(one[0]);
        }
        return Encoding.ASCII.GetString(buffer.ToArray()).Trim();
    }

    // "stream: OK" | "stream: <name> FOUND" | "<reason> ERROR". Anything else is no verdict.
    public static ScanVerdict ParseVerdict(string reply)
    {
        var text = reply.Trim('\0', ' ', '\n', '\r');
        if (text.EndsWith(" OK", StringComparison.Ordinal))
        {
            return new ScanVerdict(true);
        }
        if (text.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var name = text[..^" FOUND".Length];
            name = name.StartsWith("stream:", StringComparison.Ordinal) ? name["stream:".Length..] : name;
            name = name.Trim();
            return new ScanVerdict(false, name.Length == 0 ? "unknown" : name);
        }
        throw new ScanException(text.EndsWith("ERROR", StringComparison.Ordinal) ? "clamd reported an error." : "clamd gave an unexpected reply.");
    }
}
