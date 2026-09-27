using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;

namespace Noof.Ledger.Host.Tests;

public class SafeFailureReasonTests
{
    [Fact]
    public void A_model_call_exception_is_the_model_did_not_answer()
    {
        var exception = new ModelCallException(ModelFailureKind.Transient, "the vision model produced no tool call");

        SafeFailureReason.Describe(exception, FailureArea.Categorization).Should().Be(SafeFailureReason.ModelDidNotAnswer);
    }

    [Fact]
    public void A_model_call_exception_naming_the_Tax_Administration_is_told_apart_only_in_receipt_extraction()
    {
        var exception = new ModelCallException(
            ModelFailureKind.Transient, "the Tax Administration is unreachable and no AI key is configured for the vision fallback");

        SafeFailureReason.Describe(exception, FailureArea.ReceiptExtraction).Should().Be(SafeFailureReason.TaxAdministrationUnreachable);
        SafeFailureReason.Describe(exception, FailureArea.ReceiptCategorization).Should().Be(SafeFailureReason.ModelDidNotAnswer);
    }

    [Fact]
    public void A_database_driver_exception_is_a_database_error_by_its_type_name_alone()
    {
        var exception = new Npgsql.FakeNpgsqlException("connection refused");

        SafeFailureReason.Describe(exception, FailureArea.Categorization).Should().Be(SafeFailureReason.DatabaseError);
    }

    [Fact]
    public void An_unclassified_failure_during_receipt_extraction_says_the_photo_could_not_be_read()
    {
        var exception = new HttpRequestException("telegram unreachable");

        SafeFailureReason.Describe(exception, FailureArea.ReceiptExtraction).Should().Be(SafeFailureReason.ReceiptPhotoUnreadable);
    }

    [Fact]
    public void An_unclassified_failure_during_transcription_says_the_voice_note_could_not_be_downloaded()
    {
        var exception = new HttpRequestException("telegram unreachable");

        SafeFailureReason.Describe(exception, FailureArea.Transcription).Should().Be(SafeFailureReason.VoiceNoteUnreadable);
    }

    [Fact]
    public void An_unclassified_failure_outside_receipts_or_transcription_falls_back_to_a_temporary_problem()
    {
        var exception = new InvalidOperationException("db blip");

        SafeFailureReason.Describe(exception, FailureArea.Categorization).Should().Be(SafeFailureReason.Unknown);
    }

    [Fact]
    public void The_reason_never_contains_the_raw_exception_message()
    {
        var exception = new InvalidOperationException("secret-adjacent detail nobody should see in Telegram");

        SafeFailureReason.Describe(exception, FailureArea.Categorization).Should().NotContain("secret-adjacent");
    }
}
