using System.Security.Cryptography;
using System.Text;

namespace Noof.Ledger.Receipts.Tests.FiscalQr;

internal sealed class SyntheticQrPayloadBuilder
{
    public byte Version { get; set; } = 1;
    public string RequestedBy { get; set; } = "12345678";
    public string SignedBy { get; set; } = "87654321";
    public uint TotalCounter { get; set; } = 42;
    public uint TransactionTypeCounter { get; set; } = 7;
    public long TotalAmountRaw { get; set; } = 1_2345_6000;
    public DateTimeOffset IssuedAt { get; set; } = new(2026, 1, 15, 12, 30, 0, TimeSpan.Zero);
    public byte InvoiceType { get; set; }
    public byte TransactionType { get; set; }
    public byte[] BuyerId { get; set; } = [];
    public bool LargeEncryptedBlock { get; set; }

    public byte[] Build()
    {
        var encryptedLength = LargeEncryptedBlock ? 512 : 256;

        var body = new List<byte> { Version };
        body.AddRange(AsciiBytes(RequestedBy, 8));
        body.AddRange(AsciiBytes(SignedBy, 8));
        body.AddRange(BitConverter.GetBytes(TotalCounter));
        body.AddRange(BitConverter.GetBytes(TransactionTypeCounter));
        body.AddRange(BitConverter.GetBytes(TotalAmountRaw));
        body.AddRange(BigEndian(BitConverter.GetBytes(IssuedAt.ToUnixTimeMilliseconds())));
        body.Add(InvoiceType);
        body.Add(TransactionType);
        body.Add((byte)BuyerId.Length);
        body.AddRange(BuyerId);
        body.AddRange(new byte[encryptedLength]);
        body.AddRange(new byte[256]);

        var withoutChecksum = body.ToArray();
        var checksum = MD5.HashData(withoutChecksum);

        return [.. withoutChecksum, .. checksum];
    }

    public static string UrlFor(byte[] payload) =>
        $"https://suf.purs.gov.rs/v/?vl={Uri.EscapeDataString(Convert.ToBase64String(payload))}";

    static byte[] AsciiBytes(string value, int length)
    {
        var bytes = new byte[length];
        Encoding.ASCII.GetBytes(value, 0, Math.Min(value.Length, length), bytes, 0);
        return bytes;
    }

    static byte[] BigEndian(byte[] littleEndian)
    {
        if (BitConverter.IsLittleEndian)
            Array.Reverse(littleEndian);

        return littleEndian;
    }
}
