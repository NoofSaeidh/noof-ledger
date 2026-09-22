using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class DashboardPageSourceTests
{
    static string SourceText() => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", "Home.razor"));

    [Fact]
    public void Formats_every_amount_and_date_against_invariant_culture()
    {
        var source = SourceText();

        source.Should().Contain("CultureInfo.InvariantCulture",
            "money and dates must render the same way regardless of the host machine's OS locale (see CurrencyCodeTests.Orders_ordinally_regardless_of_culture)");
        source.Should().NotContain(".ToString(\"C\"",
            "a culture-formatted currency string depends on the server's locale for both the separator and the symbol - this page has a CurrencyCode, not a RegionInfo, to ask for one");
    }

    [Fact]
    public void Reads_spending_through_the_read_model_only()
    {
        SourceText().Should().Contain("ISpendingReadModel");
    }
}
