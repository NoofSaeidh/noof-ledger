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
    // way, so downscaling with it would throw away exactly the detail a full-resolution file brings.
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

    // SKBitmap.Decode ignores EXIF orientation. Telegram-compressed photos are pre-rotated so this
    // never mattered before, but a phone photo sent as a document (the path this branch now
    // recommends for an exact read) typically carries an orientation tag - decoding through SKCodec
    // and applying codec.EncodedOrigin ourselves is what SKBitmap.Decode does not do.
    static SKBitmap? TryDecode(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(bytes.ToArray()));
            if (codec is null || UntrustedImagePixelLimit.Exceeds(codec.Info))
                return null;

            var decoded = new SKBitmap(codec.Info.Width, codec.Info.Height);
            var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                decoded.Dispose();
                return null;
            }

            return ApplyExifOrientation(decoded, codec.EncodedOrigin);
        }
        catch
        {
            return null;
        }
    }

    // Only the three rotation-only origins a camera or a scanner's own upright pass actually produces
    // (Default, BottomRight = 180deg, RightTop/LeftBottom = 90deg) are corrected. The mirrored origins
    // (TopRight, BottomLeft, LeftTop, RightBottom) come from a flipped scan, not a phone camera, and
    // are not handled - docs/BACKLOG.md.
    static SKBitmap ApplyExifOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        SKBitmap rotated;
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight:
                rotated = new SKBitmap(bitmap.Width, bitmap.Height);
                using (var canvas = new SKCanvas(rotated))
                {
                    canvas.RotateDegrees(180, bitmap.Width / 2f, bitmap.Height / 2f);
                    canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
                }

                break;
            case SKEncodedOrigin.RightTop:
                rotated = new SKBitmap(bitmap.Height, bitmap.Width);
                using (var canvas = new SKCanvas(rotated))
                {
                    canvas.Translate(rotated.Width, 0);
                    canvas.RotateDegrees(90);
                    canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
                }

                break;
            case SKEncodedOrigin.LeftBottom:
                rotated = new SKBitmap(bitmap.Height, bitmap.Width);
                using (var canvas = new SKCanvas(rotated))
                {
                    canvas.Translate(0, rotated.Height);
                    canvas.RotateDegrees(-90);
                    canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
                }

                break;
            default:
                return bitmap;
        }

        bitmap.Dispose();
        return rotated;
    }
}
