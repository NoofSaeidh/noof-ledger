using System.Security.Cryptography;
using System.Text;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Receipts.FiscalQr;

// Byte layout ported from turanjanin/serbian-fiscal-receipts-parser (MIT):
// https://github.com/turanjanin/serbian-fiscal-receipts-parser
internal sealed class FiscalQrDecoder(FiscalVerificationUrl verificationUrl) : IFiscalQrDecoder
{
    const int HeaderLength = 44;
    const int SignatureLength = 256;
    const int ChecksumLength = 16;

    public FiscalQrDecodeResult Decode(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Failure("The link is not an https URL.");

        if (!string.Equals(uri.Host, verificationUrl.Host, StringComparison.OrdinalIgnoreCase))
            return Failure("The link is not on the Tax Administration's verification host.");

        if (!uri.AbsolutePath.StartsWith(verificationUrl.PathPrefix, StringComparison.Ordinal))
            return Failure("The link does not use the verification path.");

        var vl = ReadVlParameter(uri.Query);
        if (vl is null)
            return Failure("The link has no vl parameter.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(Pad(vl));
        }
        catch (FormatException)
        {
            return Failure("The vl parameter is not valid base64.");
        }

        return Decode(bytes, url);
    }

    static FiscalQrDecodeResult Decode(byte[] bytes, string verificationUrl)
    {
        if (bytes.Length is < 572 or > 848)
            return Failure($"The payload is {bytes.Length} bytes, outside the expected 572-848 range.");

        var buyerIdLength = bytes[43];
        var encryptedLength = bytes.Length > 592 ? 512 : 256;
        var expectedLength = HeaderLength + buyerIdLength + encryptedLength + SignatureLength + ChecksumLength;
        if (expectedLength != bytes.Length)
            return Failure("The payload's declared sections do not add up to its length.");

        var withoutChecksum = bytes.AsSpan(0, bytes.Length - ChecksumLength);
        var checksum = bytes.AsSpan(bytes.Length - ChecksumLength);
        if (!MD5.HashData(withoutChecksum).AsSpan().SequenceEqual(checksum))
            return Failure("The payload's MD5 checksum does not match.");

        var requestedBy = ReadAscii(bytes, 1, 8);
        var signedBy = ReadAscii(bytes, 9, 8);
        var totalCounter = BitConverter.ToUInt32(bytes, 17);
        var transactionTypeCounter = BitConverter.ToUInt32(bytes, 21);
        var totalRaw = BitConverter.ToInt64(bytes, 25);
        var dateTimeMs = ReadInt64BigEndian(bytes, 33);
        var invoiceType = bytes[41];
        var transactionType = bytes[42];

        if (!TryMapKind(invoiceType, transactionType, out var kind))
            return Failure($"Unrecognised invoice type {invoiceType}/transaction type {transactionType}.");

        var payload = new FiscalQrPayload(
            VerificationUrl: verificationUrl,
            Total: totalRaw / 10000m,
            IssuedAt: DateTimeOffset.FromUnixTimeMilliseconds(dateTimeMs),
            RequestedBy: requestedBy,
            SignedBy: signedBy,
            Kind: kind,
            TotalCounter: totalCounter,
            TransactionTypeCounter: transactionTypeCounter);

        return new FiscalQrDecodeResult(payload, null);
    }

    static bool TryMapKind(byte invoiceType, byte transactionType, out ReceiptKind kind)
    {
        switch (invoiceType)
        {
            case 0:
                switch (transactionType)
                {
                    case 0: kind = ReceiptKind.Sale; return true;
                    case 1: kind = ReceiptKind.Refund; return true;
                    default: kind = default; return false;
                }
            case 1: kind = ReceiptKind.Proforma; return true;
            case 2: kind = ReceiptKind.Copy; return true;
            case 3: kind = ReceiptKind.Training; return true;
            case 4: kind = ReceiptKind.Advance; return true;
            default: kind = default; return false;
        }
    }

    static string? ReadVlParameter(string query)
    {
        if (query.Length == 0)
            return null;

        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0)
                continue;

            var key = pair[..separator];
            if (!string.Equals(key, "vl", StringComparison.Ordinal))
                continue;

            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            return value.Replace(' ', '+');
        }

        return null;
    }

    static string Pad(string base64)
    {
        var remainder = base64.Length % 4;
        return remainder == 0 ? base64 : base64 + new string('=', 4 - remainder);
    }

    static string ReadAscii(byte[] bytes, int offset, int length) =>
        Encoding.ASCII.GetString(bytes, offset, length).TrimEnd('\0');

    static long ReadInt64BigEndian(byte[] bytes, int offset)
    {
        var span = bytes.AsSpan(offset, 8).ToArray();
        if (BitConverter.IsLittleEndian)
            Array.Reverse(span);

        return BitConverter.ToInt64(span);
    }

    static FiscalQrDecodeResult Failure(string reason) => new(null, reason);
}
