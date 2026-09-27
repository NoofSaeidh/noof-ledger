using Noof.Ledger.Application.Receipts;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.SkiaSharp;

namespace Noof.Ledger.Receipts.Qr;

internal sealed class ZxingQrReader : IQrReader
{
    const int MaxDownscaledDimension = 1600;
    const int UpscaleFactor = 2;
    static readonly SKSamplingOptions UpscaleSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    public string? Read(Stream image)
    {
        var bitmap = TryDecodeBitmap(image);
        if (bitmap is null)
            return null;

        using (bitmap)
        {
            var reader = NewReader();

            var direct = TryRead(reader, bitmap);
            if (direct is not null)
                return direct;

            using var rescaled = TryRescale(bitmap);
            return rescaled is null ? null : TryRead(reader, rescaled);
        }
    }

    static SKBitmap? TryDecodeBitmap(Stream image)
    {
        try
        {
            if (image.CanSeek)
                image.Position = 0;

            using var memory = new MemoryStream();
            image.CopyTo(memory);
            var bytes = memory.ToArray();

            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            if (codec is null || UntrustedImagePixelLimit.Exceeds(codec.Info))
                return null;

            return SKBitmap.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    static BarcodeReader NewReader() => new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            PossibleFormats = [BarcodeFormat.QR_CODE],
            TryHarder = true,
            TryInverted = true,
        },
    };

    static string? TryRead(BarcodeReader reader, SKBitmap bitmap)
    {
        try
        {
            return reader.Decode(bitmap)?.Text;
        }
        catch
        {
            return null;
        }
    }

    static SKBitmap? TryRescale(SKBitmap bitmap)
    {
        try
        {
            var longestSide = Math.Max(bitmap.Width, bitmap.Height);
            if (longestSide == 0)
                return null;

            var downscale = longestSide > MaxDownscaledDimension ? MaxDownscaledDimension / (double)longestSide : 1.0;
            var scale = downscale * UpscaleFactor;

            var width = Math.Max(1, (int)(bitmap.Width * scale));
            var height = Math.Max(1, (int)(bitmap.Height * scale));

            return bitmap.Resize(new SKImageInfo(width, height), UpscaleSampling);
        }
        catch
        {
            return null;
        }
    }
}
