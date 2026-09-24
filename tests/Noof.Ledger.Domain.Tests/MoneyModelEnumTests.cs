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
    }

    [Fact]
    public void Entry_role_values_never_move()
    {
        ((int)EntryRole.Principal).Should().Be(0);
        Enum.GetValues<EntryRole>().Should().HaveCount(1, "Fee is kept for Phase 7 and is not declared until then");
    }
}
