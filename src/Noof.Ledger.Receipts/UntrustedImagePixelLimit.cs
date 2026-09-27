using SkiaSharp;

namespace Noof.Ledger.Receipts;

// A compressed image (PNG/JPEG) can declare arbitrary pixel dimensions in its header while staying
// tiny on disk - SKBitmap.Decode and SKCodec.GetPixels both allocate a full width*height pixel
// buffer from those declared dimensions before validating them against the compressed data, so an
// attacker-controlled file can force a multi-gigabyte allocation regardless of any byte-size cap on
// the download itself. Every place that decodes an untrusted image reads SKCodec.Info first (which
// only parses the header, no pixel allocation) and refuses anything over this one limit. 50 megapixels
// comfortably clears a 12 MP (4000x3000) phone photo with headroom to spare.
internal static class UntrustedImagePixelLimit
{
    internal const long MaxPixels = 50_000_000;

    internal static bool Exceeds(SKImageInfo info) => (long)info.Width * info.Height > MaxPixels;
}
