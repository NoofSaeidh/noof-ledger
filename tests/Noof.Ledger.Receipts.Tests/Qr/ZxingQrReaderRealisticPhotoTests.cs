using AwesomeAssertions;
using Noof.Ledger.Receipts.Qr;
using Noof.Ledger.Receipts.Tests.FiscalQr;

namespace Noof.Ledger.Receipts.Tests.Qr;

// A realistic fiscal QR ("https://suf.purs.gov.rs/v/?vl=<base64>") is dense: the payload built by
// SyntheticQrPayloadBuilder (encrypted block + signature + checksum) is a version-22, 105x105-module
// QR (QrDensityTests pins this) - far denser than the short test string ZxingQrReaderTests uses.
// These cases reproduce the shape of a phone photo Telegram actually delivers - a compressed,
// <=1280px-long-side JPEG of a fiscal receipt with the QR occupying a modest fraction of the frame -
// which is what exposed the "Vision used: no QR" production failure a low-density synthetic QR never
// would. See the measured before/after table in this change's commit message and
// docs/OPEN-QUESTIONS.md: the benchmark's own downscale must use a filtered resampler
// (SKFilterMode.Linear + SKMipmapMode.Linear) rather than SkiaSharp's nearest-neighbour default, or
// the pass/fail pattern below reflects the benchmark's own aliasing rather than what a phone or
// Telegram's real resampler produces. Under that filtered downscale, giving ZxingQrReader's own 2x
// upscale a linear filter (rather than nearest) is a measured, strict improvement to its decode
// boundary at this density - the cases below cover both a case that already decoded and several that
// only decode with that fix.
public class ZxingQrReaderRealisticPhotoTests
{
    static readonly string RealisticVl = SyntheticQrPayloadBuilder.UrlFor(new SyntheticQrPayloadBuilder().Build());

    readonly ZxingQrReader reader = new();

    [Theory]
    [InlineData(0.30, 0, 0, false)]
    [InlineData(0.22, 4, 0, false)]
    [InlineData(0.20, 0, 0, false)]
    [InlineData(0.25, 0, 1.2, false)]
    [InlineData(0.26, 0, 0, false)]
    [InlineData(0.20, 6, 0, true)]
    public void Decodes_a_compressed_phone_photo_of_a_fiscal_receipt(
        double qrWidthFraction, double rotationDegrees, double blurSigma, bool unevenLighting)
    {
        var jpeg = SyntheticReceiptPhoto.Build(new SyntheticReceiptPhotoOptions
        {
            QrContent = RealisticVl,
            QrWidthFraction = qrWidthFraction,
            RotationDegrees = rotationDegrees,
            BlurSigma = blurSigma,
            UnevenLighting = unevenLighting,
        });

        using var stream = new MemoryStream(jpeg);
        var result = reader.Read(stream);

        result.Should().Be(RealisticVl);
    }
}
