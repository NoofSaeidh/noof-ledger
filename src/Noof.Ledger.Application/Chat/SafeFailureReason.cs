using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Application.Chat;

// Which pipeline classified the failure - only enough context to pick a sensible fallback category
// when the exception itself is not one of the ones this can name (ModelCallException, a database
// driver type).
public enum FailureArea
{
    Categorization,
    ReceiptCategorization,
    ReceiptExtraction,
    Transcription,
}

// A short, fixed, safe-to-show-the-operator label for why an attempt failed - never the raw
// exception message, which can carry a stack frame, a verification URL or a vl payload
// (docs/receipts.md's "never logged" rule extends here: shown text is even more exposed than a log
// line). Classified by exception TYPE, not by echoing Message text into the reason itself, so a
// provider's wording change can never leak a raw message into the bot's own words. The Tax
// Administration case is the one exception: it is still a ModelCallException under the hood (thrown
// deliberately by ExtractReceiptWorker when a fetch failure leaves no key to fall back on), so it is
// told apart by matching that one known, hand-written phrase - never by pattern-matching an
// upstream provider's own error text.
public static class SafeFailureReason
{
    public const string DatabaseError = "a database error";
    public const string ModelDidNotAnswer = "the model did not answer";
    public const string TaxAdministrationUnreachable = "the Tax Administration is unreachable";
    public const string ReceiptPhotoUnreadable = "the receipt photo could not be read";
    public const string VoiceNoteUnreadable = "the voice note could not be downloaded";
    public const string Unknown = "a temporary problem";

    public static string Describe(Exception exception, FailureArea area) => exception switch
    {
        ModelCallException when area == FailureArea.ReceiptExtraction && IsTaxAdministrationOutage(exception) =>
            TaxAdministrationUnreachable,
        ModelCallException => ModelDidNotAnswer,
        _ when IsDatabaseError(exception) => DatabaseError,
        _ when area == FailureArea.ReceiptExtraction => ReceiptPhotoUnreadable,
        _ when area == FailureArea.Transcription => VoiceNoteUnreadable,
        _ => Unknown,
    };

    static bool IsTaxAdministrationOutage(Exception exception) =>
        exception.Message.Contains("Tax Administration", StringComparison.OrdinalIgnoreCase);

    static bool IsDatabaseError(Exception exception)
    {
        var typeName = exception.GetType().FullName ?? string.Empty;
        return typeName.Contains("Npgsql", StringComparison.Ordinal) || typeName.Contains("DbUpdateException", StringComparison.Ordinal);
    }
}
