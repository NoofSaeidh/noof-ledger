using System.Buffers.Binary;
using SkiaSharp;

namespace Noof.Ledger.Receipts.Tests;

// Builds a PNG whose IHDR chunk declares huge pixel dimensions while the file itself stays a few
// dozen bytes on disk: SkiaSharp-encode a real 1x1 image, then patch the IHDR's width/height fields
// and recompute the chunk's CRC32 by hand. IHDR is always the first chunk in a valid PNG, immediately
// after the 8-byte signature, so its offsets are fixed: length(8..11)=13, type "IHDR"(12..15),
// width(16..19), height(20..23), ...(24..28), CRC(29..32) over type+data (bytes 12..28).
internal static class HugeDeclaredDimensionPng
{
    internal static byte[] Build(int declaredWidth, int declaredHeight)
    {
        var patched = RealTinyPng();

        BinaryPrimitives.WriteInt32BigEndian(patched.AsSpan(16, 4), declaredWidth);
        BinaryPrimitives.WriteInt32BigEndian(patched.AsSpan(20, 4), declaredHeight);

        var crc = Crc32(patched.AsSpan(12, 17));
        BinaryPrimitives.WriteUInt32BigEndian(patched.AsSpan(29, 4), crc);

        return patched;
    }

    static byte[] RealTinyPng()
    {
        using var bitmap = new SKBitmap(1, 1);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFFu;
    }

    static readonly uint[] Crc32Table = BuildCrc32Table();

    static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

            table[n] = c;
        }

        return table;
    }
}
