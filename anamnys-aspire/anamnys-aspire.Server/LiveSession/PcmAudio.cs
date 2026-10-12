using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Anamnys.Server.LiveSession;

// PCM 16 kHz mono 16-bit little-endian, owned by whoever holds it. Destroy zeroes the bytes:
// session audio never outlives its transcription (spec §2).
public sealed class PcmAudio(byte[] bytes, int length)
{
    private byte[] _bytes = bytes;

    public int Length { get; private set; } = length;

    public float[] ToSamples()
    {
        var samples = new float[Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(_bytes.AsSpan(i * 2, 2)) / 32768f;
        }
        return samples;
    }

    public void Destroy()
    {
        CryptographicOperations.ZeroMemory(_bytes);
        _bytes = [];
        Length = 0;
    }
}
