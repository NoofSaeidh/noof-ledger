using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class CategorizationAuthorityTests
{
    [Theory]
    [InlineData(CategorizationAuthority.None, 0)]
    [InlineData(CategorizationAuthority.Model, 1)]
    [InlineData(CategorizationAuthority.Rule, 2)]
    [InlineData(CategorizationAuthority.User, 4)]
    public void Underlying_value_is_pinned(CategorizationAuthority value, int expected)
    {
        ((int)value).Should().Be(expected);
    }
}
