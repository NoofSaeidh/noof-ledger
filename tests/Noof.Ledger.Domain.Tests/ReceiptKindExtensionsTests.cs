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

    [Theory]
    [InlineData(ReceiptKind.Sale)]
    [InlineData(ReceiptKind.Refund)]
    public void Sale_and_refund_are_fiscal_money_kinds(ReceiptKind kind)
    {
        kind.IsFiscalMoneyKind().Should().BeTrue();
    }

    [Theory]
    [InlineData(ReceiptKind.Copy)]
    [InlineData(ReceiptKind.Training)]
    [InlineData(ReceiptKind.Proforma)]
    [InlineData(ReceiptKind.Advance)]
    [InlineData(ReceiptKind.Exchange)]
    public void Every_other_kind_is_not_a_fiscal_money_kind(ReceiptKind kind)
    {
        kind.IsFiscalMoneyKind().Should().BeFalse();
    }

    [Fact]
    public void An_exchange_slip_is_not_a_non_money_kind_either()
    {
        ReceiptKind.Exchange.IsNonMoneyKind().Should().BeFalse("a slip moves money, between the operator's own wallets");
    }

    [Fact]
    public void Every_kind_is_exactly_one_of_non_money_fiscal_money_or_an_exchange_slip()
    {
        Enum.GetValues<ReceiptKind>().Should().OnlyContain(kind =>
            (kind.IsNonMoneyKind() ? 1 : 0) + (kind.IsFiscalMoneyKind() ? 1 : 0) + (kind == ReceiptKind.Exchange ? 1 : 0) == 1);
    }
}
