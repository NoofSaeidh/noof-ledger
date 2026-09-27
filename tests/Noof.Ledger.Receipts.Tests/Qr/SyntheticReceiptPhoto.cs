using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;

namespace Noof.Ledger.Receipts.Tests.Qr;

internal sealed record SyntheticReceiptPhotoOptions
{
    public required string QrContent { get; init; }
    public double QrWidthFraction { get; init; } = 0.22;
    public double RotationDegrees { get; init; }
    public double BlurSigma { get; init; }
    public bool UnevenLighting { get; init; }
    public bool TextNoise { get; init; } = true;
    public int SourceLongSide { get; init; } = 2400;
    public int TargetLongSide { get; init; } = 1280;
    public int JpegQuality { get; init; } = 80;
    public int Seed { get; init; } = 1;
}

internal static class SyntheticReceiptPhoto
{
    static readonly SKSamplingOptions DownscaleSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public static byte[] Build(SyntheticReceiptPhotoOptions options)
    {
        var width = options.SourceLongSide * 3 / 4;
        var height = options.SourceLongSide;

        using var canvas = new SKBitmap(width, height);
        using (var skCanvas = new SKCanvas(canvas))
        {
            skCanvas.Clear(new SKColor(250, 249, 246));

            var random = new Random(options.Seed);
            if (options.TextNoise)
                DrawTextNoise(skCanvas, random, width, height);

            var qrPixelSize = (int)(width * options.QrWidthFraction);
            using var qr = EncodeQr(options.QrContent, qrPixelSize);
            var qrLeft = (width - qr.Width) / 2;
            var qrTop = height - qr.Height - height / 12;
            skCanvas.DrawBitmap(qr, qrLeft, qrTop, SKSamplingOptions.Default);

            if (options.UnevenLighting)
                DrawUnevenLighting(skCanvas, width, height);
        }

        using var rotated = options.RotationDegrees == 0 ? canvas.Copy() : Rotate(canvas, options.RotationDegrees);
        using var blurred = options.BlurSigma > 0 ? Blur(rotated, options.BlurSigma) : null;

        using var downscaled = Downscale(blurred ?? rotated, options.TargetLongSide);
        return EncodeJpeg(downscaled, options.JpegQuality);
    }

    static SKBitmap EncodeQr(string content, int pixelSize)
    {
        var writer = new BarcodeWriter
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions { Width = pixelSize, Height = pixelSize, Margin = 1 },
        };
        return writer.Write(content);
    }

    static void DrawTextNoise(SKCanvas canvas, Random random, int width, int height)
    {
        using var paint = new SKPaint { Color = new SKColor(60, 60, 60) };
        var top = height / 20;
        var bottom = height * 6 / 10;
        var y = top;
        while (y < bottom)
        {
            var rowHeight = random.Next(6, 14);
            var lineCount = random.Next(1, 4);
            var x = width / 10;
            for (var i = 0; i < lineCount && x < width * 9 / 10; i++)
            {
                var barWidth = random.Next(width / 12, width / 4);
                canvas.DrawRect(x, y, Math.Min(barWidth, width * 9 / 10 - x), rowHeight, paint);
                x += barWidth + width / 20;
            }

            y += rowHeight + random.Next(6, 16);
        }
    }

    static void DrawUnevenLighting(SKCanvas canvas, int width, int height)
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(width, height),
            [new SKColor(255, 255, 255, 0), new SKColor(0, 0, 0, 110)],
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Multiply };
        canvas.DrawRect(0, 0, width, height, paint);
    }

    static SKBitmap Rotate(SKBitmap source, double degrees)
    {
        var radians = Math.Abs(degrees) * Math.PI / 180.0;
        var boundedWidth = (int)(source.Width * Math.Cos(radians) + source.Height * Math.Sin(radians));
        var boundedHeight = (int)(source.Width * Math.Sin(radians) + source.Height * Math.Cos(radians));

        var rotated = new SKBitmap(boundedWidth, boundedHeight);
        using var skCanvas = new SKCanvas(rotated);
        skCanvas.Clear(new SKColor(250, 249, 246));
        skCanvas.Translate(boundedWidth / 2f, boundedHeight / 2f);
        skCanvas.RotateDegrees((float)degrees);
        skCanvas.Translate(-source.Width / 2f, -source.Height / 2f);
        skCanvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        return rotated;
    }

    static SKBitmap Blur(SKBitmap source, double sigma)
    {
        var blurred = new SKBitmap(source.Width, source.Height);
        using var skCanvas = new SKCanvas(blurred);
        using var filter = SKImageFilter.CreateBlur((float)sigma, (float)sigma);
        using var paint = new SKPaint { ImageFilter = filter };
        skCanvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default, paint);
        return blurred;
    }

    static SKBitmap Downscale(SKBitmap source, int targetLongSide)
    {
        var longestSide = Math.Max(source.Width, source.Height);
        if (longestSide <= targetLongSide)
            return source.Copy();

        var scale = targetLongSide / (double)longestSide;
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));
        return source.Resize(new SKImageInfo(width, height), DownscaleSampling);
    }

    static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}
