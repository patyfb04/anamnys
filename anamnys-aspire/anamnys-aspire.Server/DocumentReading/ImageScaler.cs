using SkiaSharp;

namespace Anamnys.Server.DocumentReading;

// A JPEG or PNG larger than maxPixels on its longest side is scaled down (to PNG) before OCR, like a
// rendered PDF page; smaller images are passed through unchanged. In process (SkiaSharp).
// Only the header is read before the size check: an image of more than MaxDecodedPixels (width times
// height) is refused unread, so a small file declaring a huge image (a decompression bomb) never gets a
// pixel buffer, here or in Tesseract. A JPEG is decoded at the nearest smaller scale its codec supports.
public static class ImageScaler
{
    public const long MaxDecodedPixels = 50_000_000;

    public static byte[] FitWithin(byte[] image, int maxPixels, long maxDecodedPixels = MaxDecodedPixels)
    {
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data) ?? throw new UnreadableDocumentException();
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0 || (long)width * height > maxDecodedPixels)
        {
            throw new UnreadableDocumentException();
        }
        var longest = Math.Max(width, height);
        if (maxPixels <= 0 || longest <= maxPixels)
        {
            return image;
        }
        var scale = (double)maxPixels / longest;
        var decodeSize = codec.EncodedFormat == SKEncodedImageFormat.Jpeg
            ? codec.GetScaledDimensions((float)scale)
            : new SKSizeI(width, height);
        var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        using var bitmap = SKBitmap.Decode(codec, decodeInfo) ?? throw new UnreadableDocumentException();
        var target = new SKImageInfo(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
        using var resized = bitmap.Width == target.Width && bitmap.Height == target.Height
            ? bitmap.Copy()
            : bitmap.Resize(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (resized is null)
        {
            throw new UnreadableDocumentException();
        }
        using var scaled = SKImage.FromBitmap(resized);
        using var png = scaled.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
