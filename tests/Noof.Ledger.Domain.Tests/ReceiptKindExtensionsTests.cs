using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class ReceiptKindExtensionsTests
{
    [Theory]
    [InlineData(ReceiptKind.Copy)]
    [InlineData(ReceiptKind.Training)]
    [InlineData(ReceiptKind.Proforma)]
    [InlineData(ReceiptKind.Advance)]
    public void Copy_training_proforma_and_advance_carry_no_money(ReceiptKind kind)
    {
        kind.IsNonMoneyKind().Should().BeTrue();
    }

    [Theory]
    [InlineData(ReceiptKind.Sale)]
    [InlineData(ReceiptKind.Refund)]
    public void Sale_and_refund_carry_money(ReceiptKind kind)
    {
        kind.IsNonMoneyKind().Should().BeFalse();
    }
}
