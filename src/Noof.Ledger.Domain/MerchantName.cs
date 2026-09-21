namespace Noof.Ledger.Domain;

public static class MerchantName
{
    public static string Fold(string raw)
    {
        var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return string.Join(' ', words).ToUpperInvariant();
    }
}
