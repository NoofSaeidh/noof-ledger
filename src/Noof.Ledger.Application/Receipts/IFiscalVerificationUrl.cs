namespace Noof.Ledger.Application.Receipts;

public interface IFiscalVerificationUrl
{
    string Prefix { get; }

    string Host { get; }

    string PathPrefix { get; }

    bool TryFind(string text, out string url);

    string? StripUrl(string? text);
}
