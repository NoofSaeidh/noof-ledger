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

    [Fact]
    public void Downscaling_blends_a_fine_stripe_pattern_instead_of_aliasing_it()
    {
        // Exactly 2x the limit, so every output column sits on the boundary between one black and
        // one white source column. A nearest-neighbour resize always lands on one side or the other,
        // producing a solid black or solid white image; a linear-filtered one averages the pair into
        // mid-grey. SkiaSharp's SKSamplingOptions.Default is nearest-neighbour in 4.151.1.
        var original = EncodeStripes(SkiaReceiptImageScaler.MaxLongSidePixels * 2, 40);

        var scaled = scaler.ScaleForVision(new ReceiptPhoto(original, "image/png"));

        using var bitmap = SKBitmap.Decode(scaled.Bytes.ToArray());
        var sampledReds = Enumerable.Range(0, bitmap.Width)
            .Where(x => x % 97 == 0)
            .Select(x => (int)bitmap.GetPixel(x, bitmap.Height / 2).Red)
            .ToArray();
        var averageRed = sampledReds.Average();

        averageRed.Should().BeInRange(80, 175,
            "a linear-filtered downscale of alternating black/white columns should come back grey, not pure black or white");
    }

    [Fact]
    public void A_photo_with_EXIF_orientation_6_is_rotated_upright_before_it_is_scaled()
    {
        // Orientation 6 ("rotate 90deg CW to display correctly") is what a phone stamps on a photo
        // sent as an uncompressed document - Telegram-compressed photos are pre-rotated and carry no
        // such tag. The raw sensor data here is landscape (2000x1000); once rotated upright it is
        // portrait (1000x2000), over the limit on its height, so a correct fix scales it down to a
        // portrait 1568-tall image. A scaler that ignores the tag keeps it landscape (1568 wide).
        var landscapeSensorData = EncodeJpeg(2000, 1000);
        var withOrientation6 = WithExifOrientation(landscapeSensorData, orientation: 6);

        var scaled = scaler.ScaleForVision(new ReceiptPhoto(withOrientation6, "image/jpeg"));

        using var bitmap = SKBitmap.Decode(scaled.Bytes.ToArray());
        bitmap.Width.Should().BeLessThan(bitmap.Height, "orientation 6 corrected upright is portrait, not landscape");
        bitmap.Height.Should().Be(SkiaReceiptImageScaler.MaxLongSidePixels);
    }

    static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        var tiff = new byte[]
        {
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // TIFF header, IFD0 at offset 8
            0x01, 0x00, // one entry
            0x12, 0x01, // tag 0x0112 - Orientation
            0x03, 0x00, // type 3 - SHORT
            0x01, 0x00, 0x00, 0x00, // count 1
            (byte)(orientation & 0xFF), (byte)(orientation >> 8), 0x00, 0x00, // value, padded to 4 bytes
            0x00, 0x00, 0x00, 0x00, // no next IFD
        };
        var exifHeader = "Exif\0\0"u8.ToArray();
        var segmentData = exifHeader.Concat(tiff).ToArray();
        var app1Length = (ushort)(segmentData.Length + 2);
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(app1Length >> 8), (byte)(app1Length & 0xFF) }
            .Concat(segmentData).ToArray();

        return jpeg[..2].Concat(app1).Concat(jpeg[2..]).ToArray();
    }

    static byte[] EncodeJpeg(int width, int height) => Encode(width, height, SKEncodedImageFormat.Jpeg);

    static byte[] EncodePng(int width, int height) => Encode(width, height, SKEncodedImageFormat.Png);

    static byte[] EncodeStripes(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var x = 0; x < width; x++)
        {
            var color = x % 2 == 0 ? SKColors.Black : SKColors.White;
            for (var y = 0; y < height; y++)
                bitmap.SetPixel(x, y, color);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

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
