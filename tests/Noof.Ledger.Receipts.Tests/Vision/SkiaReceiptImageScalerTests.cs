using AwesomeAssertions;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Receipts.Vision;
using SkiaSharp;

namespace Noof.Ledger.Receipts.Tests.Vision;

public class SkiaReceiptImageScalerTests
{
    readonly SkiaReceiptImageScaler scaler = new();

    [Fact]
    public void A_photo_already_within_the_limit_is_returned_unchanged()
    {
        var original = EncodeJpeg(1200, 800);

        var scaled = scaler.ScaleForVision(new ReceiptPhoto(original, "image/jpeg"));

        scaled.Bytes.ToArray().Should().Equal(original);
        scaled.MediaType.Should().Be("image/jpeg");
    }

    [Fact]
    public void A_photo_over_the_limit_is_downscaled_to_1568px_on_its_long_side_and_re_encoded_as_jpeg()
    {
        var original = EncodePng(3000, 2000);

        var scaled = scaler.ScaleForVision(new ReceiptPhoto(original, "image/png"));

        scaled.MediaType.Should().Be("image/jpeg");
        using var bitmap = SKBitmap.Decode(scaled.Bytes.ToArray());
        Math.Max(bitmap.Width, bitmap.Height).Should().Be(SkiaReceiptImageScaler.MaxLongSidePixels);
        bitmap.Width.Should().Be(1568);
        bitmap.Height.Should().Be((int)Math.Round(2000 * (1568.0 / 3000.0)));
    }

    [Fact]
    public void A_tall_photo_over_the_limit_scales_by_its_height()
    {
        var original = EncodePng(1000, 4000);

        var scaled = scaler.ScaleForVision(new ReceiptPhoto(original, "image/png"));

        using var bitmap = SKBitmap.Decode(scaled.Bytes.ToArray());
        bitmap.Height.Should().Be(SkiaReceiptImageScaler.MaxLongSidePixels);
        bitmap.Width.Should().Be((int)Math.Round(1000 * (1568.0 / 4000.0)));
    }

    [Fact]
    public void An_image_that_cannot_be_decoded_is_returned_unchanged_instead_of_throwing()
    {
        var garbage = "not an image"u8.ToArray();

        var scaled = scaler.ScaleForVision(new ReceiptPhoto(garbage, "image/jpeg"));

        scaled.Bytes.ToArray().Should().Equal(garbage);
    }

    static byte[] EncodeJpeg(int width, int height) => Encode(width, height, SKEncodedImageFormat.Jpeg);

    static byte[] EncodePng(int width, int height) => Encode(width, height, SKEncodedImageFormat.Png);

    static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}
