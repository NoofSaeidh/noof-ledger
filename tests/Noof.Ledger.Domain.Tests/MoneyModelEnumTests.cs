using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class MoneyModelEnumTests
{
    [Fact]
    public void Transaction_kind_values_never_move()
    {
        ((int)TransactionKind.Expense).Should().Be(0);
        ((int)TransactionKind.Income).Should().Be(1);
        ((int)TransactionKind.BalanceCheck).Should().Be(2);
        Enum.GetValues<TransactionKind>().Should().HaveCount(3, "3 is kept for Phase 7's Transfer and is not declared until then");
    }

    [Fact]
    public void Capture_kind_values_never_move()
    {
        ((int)CaptureKind.Text).Should().Be(0);
        ((int)CaptureKind.Voice).Should().Be(1);
        ((int)CaptureKind.Manual).Should().Be(2);
        ((int)CaptureKind.Photo).Should().Be(3);
    }

    [Fact]
    public void Job_kind_values_never_move()
    {
        ((int)JobKind.Categorize).Should().Be(0);
        ((int)JobKind.Correct).Should().Be(1);
        ((int)JobKind.Reinterpret).Should().Be(2);
        ((int)JobKind.Transcribe).Should().Be(3);
        ((int)JobKind.ExtractReceipt).Should().Be(4);
        ((int)JobKind.CategorizeReceipt).Should().Be(5);
    }

    [Fact]
    public void Wallet_payment_default_only_covers_card_and_cash()
    {
        ((int)WalletPaymentDefault.Card).Should().Be(0);
        ((int)WalletPaymentDefault.Cash).Should().Be(1);
        Enum.GetValues<WalletPaymentDefault>().Should().HaveCount(2,
            "a wallet is never the default for Transfer, Voucher, Other or Mixed (R-3)");
    }

    // These three enums are stored as smallint (M-5, Phase 6 final review: Application no longer
    // keeps its own copy). A value moving here would silently renumber every persisted receipt row.
    [Fact]
    public void Receipt_source_values_never_move()
    {
        ((int)ReceiptSource.FiscalQr).Should().Be(0);
        ((int)ReceiptSource.Vision).Should().Be(1);
        Enum.GetValues<ReceiptSource>().Should().HaveCount(2);
    }

    [Fact]
    public void Receipt_kind_values_never_move()
    {
        ((int)ReceiptKind.Sale).Should().Be(0);
        ((int)ReceiptKind.Refund).Should().Be(1);
        ((int)ReceiptKind.Copy).Should().Be(2);
        ((int)ReceiptKind.Training).Should().Be(3);
        ((int)ReceiptKind.Proforma).Should().Be(4);
        ((int)ReceiptKind.Advance).Should().Be(5);
    }

    [Fact]
    public void Payment_method_values_never_move()
    {
        ((int)PaymentMethod.Card).Should().Be(0);
        ((int)PaymentMethod.Cash).Should().Be(1);
        ((int)PaymentMethod.Transfer).Should().Be(2);
        ((int)PaymentMethod.Voucher).Should().Be(3);
        ((int)PaymentMethod.Other).Should().Be(4);
        ((int)PaymentMethod.Mixed).Should().Be(5);
    }

    [Fact]
    public void Entry_role_values_never_move()
    {
        ((int)EntryRole.Principal).Should().Be(0);
        Enum.GetValues<EntryRole>().Should().HaveCount(1, "Fee is kept for Phase 7 and is not declared until then");
    }
}
