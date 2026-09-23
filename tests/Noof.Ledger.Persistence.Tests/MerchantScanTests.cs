using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Persistence.Tests;

public class MerchantScanTests
{
    static readonly IMerchantScan Scan = new MerchantScan();

    [Fact]
    public void Matches_an_alias_that_occurs_on_whole_word_boundaries()
    {
        var aliases = new[] { new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi") };

        var matches = Scan.Matches("kupio u maxi danas", aliases, 10);

        matches.Should().ContainSingle().Which.Folded.Should().Be("MAXI");
    }

    [Fact]
    public void Does_not_match_an_alias_that_is_only_a_substring_of_a_longer_word()
    {
        var aliases = new[] { new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi") };

        var matches = Scan.Matches("kupio maximalno danas", aliases, 10);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void An_alias_does_not_match_when_its_words_are_not_adjacent_in_the_raw_text()
    {
        var aliases = new[] { new MerchantAliasEntry("MAXI PLUS", Guid.NewGuid(), "Maxi Plus") };

        var matches = Scan.Matches("kupio u maxi ali ne plus danas", aliases, 10);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Orders_the_longer_alias_first_when_both_match()
    {
        var maxi = new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi");
        var maxiPlus = new MerchantAliasEntry("MAXI PLUS", Guid.NewGuid(), "Maxi Plus");

        var matches = Scan.Matches("kupio u maxi plus danas", [maxi, maxiPlus], 10);

        matches.Select(m => m.Folded).Should().Equal("MAXI PLUS", "MAXI");
    }

    [Fact]
    public void Ties_on_length_are_broken_ordinally_regardless_of_input_order()
    {
        var idea = new MerchantAliasEntry("IDEA", Guid.NewGuid(), "Idea");
        var maxi = new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi");

        var matches = Scan.Matches("kupio u maxi i idea danas", [maxi, idea], 10);

        matches.Select(m => m.Folded).Should().Equal("IDEA", "MAXI");
    }

    [Fact]
    public void Returns_at_most_limit_matches()
    {
        var idea = new MerchantAliasEntry("IDEA", Guid.NewGuid(), "Idea");
        var maxi = new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi");

        var matches = Scan.Matches("kupio u maxi i idea danas", [maxi, idea], 1);

        matches.Should().ContainSingle().Which.Folded.Should().Be("IDEA");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_limit_returns_no_matches(int limit)
    {
        var aliases = new[] { new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi") };

        Scan.Matches("kupio u maxi danas", aliases, limit).Should().BeEmpty();
    }

    [Fact]
    public void Matching_is_ordinal_and_unaffected_by_the_current_culture()
    {
        // Mirrors MerchantNameTests.Uppercases_using_the_invariant_culture's trap at the scan
        // level: a culture-sensitive comparison here would be the Serbian LJ-collation defect
        // this codebase treats as settled everywhere else, not a hypothetical one.
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

        try
        {
            var aliases = new[] { new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi") };

            var matches = Scan.Matches("kupio u maxi danas", aliases, 10);

            matches.Should().ContainSingle().Which.Folded.Should().Be("MAXI");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
