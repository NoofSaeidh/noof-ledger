namespace Noof.Ledger.Domain;

public enum CaptureKind
{
    Text = 0,
    Voice = 1,

    // Made on the dashboard: there is no Telegram message behind it.
    Manual = 2,

    // A receipt photo, an image document, or a text message that names a fiscal QR link (Phase 6).
    Photo = 3,
}
