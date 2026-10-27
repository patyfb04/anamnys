using System.Buffers.Binary;
using System.Text;
using Anamnys.Server.DocumentReading;
using FluentAssertions;
using SkiaSharp;

namespace Anamnys.Tests.DocumentReading;

public class ImageScalerTests
{
    // A few hundred bytes declaring a 20000 x 20000 RGB image (400 MP, 1.6 GB decoded): only the header
    // is valid, so any decode attempt would fail or allocate; the size check must refuse it first.
    private static byte[] ForgedPng(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        WriteChunk(stream, "IHDR", header);
        WriteChunk(stream, "IDAT", [0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01]);
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typeAndData);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    [Theory]
    [InlineData(4200)]
    [InlineData(0)]
    public void FitWithin_HeaderDeclaringMoreThanTheDecodeCap_IsUnreadableBeforeAnyDecode(int maxPixels)
    {
        // Arrange
        var forged = ForgedPng(20_000, 20_000);

        // Act
        var act = () => ImageScaler.FitWithin(forged, maxPixels);

        // Assert
        forged.Length.Should().BeLessThan(200);
        act.Should().Throw<UnreadableDocumentException>();
    }

    [Fact]
    public void FitWithin_HeaderWithinTheDecodeCap_IsNotRefusedBySize()
    {
        // Arrange
        var forged = ForgedPng(100, 100);

        // Act
        var result = ImageScaler.FitWithin(forged, 4200);

        // Assert
        result.Should().BeSameAs(forged);
    }

    [Fact]
    public void FitWithin_LargeJpeg_IsDecodedScaledAndFitsTheLimit()
    {
        // Arrange
        var jpeg = Jpeg(1000, 1400);

        // Act
        var result = ImageScaler.FitWithin(jpeg, 421);

        // Assert
        using var codec = SKCodec.Create(SKData.CreateCopy(result));
        codec.EncodedFormat.Should().Be(SKEncodedImageFormat.Png);
        codec.Info.Height.Should().Be(421);
        codec.Info.Width.Should().BeInRange(299, 301);
    }
}
