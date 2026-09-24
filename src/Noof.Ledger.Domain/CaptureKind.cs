namespace Noof.Ledger.Domain;

public enum CaptureKind
{
    Text = 0,
    Voice = 1,

    // Made on the dashboard: there is no Telegram message behind it.
    Manual = 2,
}
