using System.Text;
using AwesomeAssertions;
using Noof.Ledger.Receipts.Qr;
using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;

namespace Noof.Ledger.Receipts.Tests.Qr;

public class ZxingQrReaderTests
{
    const string PayloadUrl = "https://suf.purs.gov.rs/v/?vl=synthetic-test-payload";

    readonly ZxingQrReader reader = new();

    [Fact]
    public void Reads_a_plain_qr_image()
    {
        using var bitmap = EncodeQr(PayloadUrl, 250);
        using var stream = ToPngStream(bitmap);

        reader.Read(stream).Should().Be(PayloadUrl);
    }

    [Fact]
    public void Reads_a_qr_image_rotated_90_degrees()
    {
        using var bitmap = EncodeQr(PayloadUrl, 250);
        using var rotated = Rotate90(bitmap);
        using var stream = ToPngStream(rotated);

        reader.Read(stream).Should().Be(PayloadUrl);
    }

    [Fact]
    public void Reads_a_small_qr_on_a_large_white_canvas()
    {
        using var small = EncodeQr(PayloadUrl, 80);
        using var canvas = PlaceOnLargeCanvas(small, 2000);
        using var stream = ToPngStream(canvas);

        reader.Read(stream).Should().Be(PayloadUrl);
    }

    [Fact]
    public void Reads_a_qr_image_with_surrounding_noise()
    {
        using var bitmap = EncodeQr(PayloadUrl, 300);
        using var withBorder = PlaceOnLargeCanvas(bitmap, 500);
        AddNoiseAroundCenter(withBorder, exclude: new SKRectI(80, 80, 420, 420));
        using var stream = ToPngStream(withBorder);

        reader.Read(stream).Should().Be(PayloadUrl);
    }

    [Fact]
    public void Returns_null_for_a_non_image_stream()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("this is not an image"));

        reader.Read(stream).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_an_empty_stream_without_throwing()
    {
        using var stream = new MemoryStream();

        var act = () => reader.Read(stream);

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    static SKBitmap EncodeQr(string content, int size)
    {
        var writer = new BarcodeWriter
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions { Width = size, Height = size, Margin = 1 },
        };
        return writer.Write(content);
    }

    static SKBitmap Rotate90(SKBitmap source)
    {
        var rotated = new SKBitmap(source.Height, source.Width);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.White);
        canvas.Translate(rotated.Width, 0);
        canvas.RotateDegrees(90);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        return rotated;
    }

    static SKBitmap PlaceOnLargeCanvas(SKBitmap small, int canvasSize)
    {
        var canvas = new SKBitmap(canvasSize, canvasSize);
        using var skCanvas = new SKCanvas(canvas);
        skCanvas.Clear(SKColors.White);
        var offset = (canvasSize - small.Width) / 2;
        skCanvas.DrawBitmap(small, offset, offset, SKSamplingOptions.Default);
        return canvas;
    }

    static void AddNoiseAroundCenter(SKBitmap bitmap, SKRectI exclude)
    {
        var random = new Random(42);
        for (var i = 0; i < 4000; i++)
        {
            var x = random.Next(bitmap.Width);
            var y = random.Next(bitmap.Height);
            if (exclude.Contains(x, y))
                continue;

            bitmap.SetPixel(x, y, random.Next(2) == 0 ? SKColors.Black : SKColors.Gray);
        }
    }

    static MemoryStream ToPngStream(SKBitmap bitmap)
    {
        var stream = new MemoryStream();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        data.SaveTo(stream);
        stream.Position = 0;
        return stream;
    }
}
