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

    [Fact]
    public void A_completed_transaction_with_no_line_items_reads_as_nothing_to_record_not_a_blank_list()
    {
        var source = SourceText();

        source.Should().Contain("Items.Count == 0",
            "a Completed transaction with zero line items means the categoriser read the message and " +
            "found nothing to record (a loan received, not spending) - it must say so, not silently " +
            "render an empty list that looks like a broken row");
    }

    [Fact]
    public void Disposes_a_component_owned_cancellation_source_so_navigating_away_cancels_the_in_flight_query()
    {
        var source = SourceText();

        source.Should().Contain("@implements IDisposable",
            "Blazor gives a component no CancellationToken of its own - navigating away must cancel the " +
            "read-model query via a token this component owns and disposes");
        source.Should().Contain("CancellationTokenSource",
            "the component must own a CancellationTokenSource to cancel on dispose");
        source.Should().NotContain("CancellationToken.None",
            "CancellationToken.None never cancels - navigating away would let the query for a disposed component run to completion");
    }

    [Fact]
    public void A_cancelled_query_is_not_mistaken_for_a_dead_database()
    {
        var source = SourceText();

        source.Should().Contain("catch (OperationCanceledException",
            "an OperationCanceledException from the component's own cancellation token must not fall " +
            "through to the database-unavailable branch, which is reserved for a genuine DbException");
    }
}
