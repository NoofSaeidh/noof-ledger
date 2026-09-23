using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class TransactionStatusTests
{
    [Fact]
    public void Stored_values_never_move()
    {
        ((int)TransactionStatus.Captured).Should().Be(0);
        ((int)TransactionStatus.Completed).Should().Be(1);
        ((int)TransactionStatus.Failed).Should().Be(2);
        ((int)TransactionStatus.Cancelled).Should().Be(3);
    }
}
