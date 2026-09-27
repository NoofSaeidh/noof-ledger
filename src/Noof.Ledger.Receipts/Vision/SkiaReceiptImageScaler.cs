using Noof.Ledger.Application.Receipts;
using SkiaSharp;

namespace Noof.Ledger.Receipts.Vision;

// A long side of 1568px is comfortably inside every vision-capable model's own recommended input
// size; ChatReceiptVision.MaxImageBytes is a byte-count refusal, this is what keeps a normal photo
// well clear of it in the first place.
internal sealed class SkiaReceiptImageScaler : IReceiptImageScaler
{
    internal const int MaxLongSidePixels = 1568;
    const int JpegQuality = 85;

    // SKSamplingOptions.Default is nearest-neighbour in SkiaSharp 4.151.1 - aliasing on exactly the
    // thin thermal-print digits the vision model must read. No phone or Telegram resampler works that
    // way, so downscaling with it degrades the "send it uncompressed for an exact read" path itself.
    static readonly SKSamplingOptions DownscaleSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public ReceiptPhoto ScaleForVision(ReceiptPhoto photo)
    {
        using var bitmap = TryDecode(photo.Bytes);
        if (bitmap is null)
            return photo;

        var longSide = Math.Max(bitmap.Width, bitmap.Height);
        if (longSide <= MaxLongSidePixels)
            return photo;

        var scale = MaxLongSidePixels / (double)longSide;
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

        using var resized = bitmap.Resize(new SKImageInfo(width, height), DownscaleSampling);
        if (resized is null)
            return photo;

        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return new ReceiptPhoto(data.ToArray(), "image/jpeg");
    }

    static SKBitmap? TryDecode(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return SKBitmap.Decode(bytes.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
