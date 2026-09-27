namespace Noof.Ledger.Application.Receipts;

public sealed class FiscalVerificationUrlOptions
{
    public const string ConfigurationSection = "Receipts";

    public string VerificationUrlPrefix { get; init; } = "";
}
