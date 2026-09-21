using System.Globalization;
using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class MerchantNameTests
{
    [Fact]
    public void Trims_leading_and_trailing_whitespace()
    {
        MerchantName.Fold("  Coffee Bar  ").Should().Be("COFFEE BAR");
    }

    [Fact]
    public void Collapses_internal_whitespace_runs_to_a_single_space()
    {
        MerchantName.Fold("Coffee\t\n  Bar").Should().Be("COFFEE BAR");
    }

    [Fact]
    public void Uppercases_using_the_invariant_culture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

        try
        {
            // Turkish casing turns 'i' into 'İ' (dotted capital I), not 'I'. A current-culture
            // ToUpper() here would silently fracture merchant identity on a Turkish/Azeri locale,
            // since MerchantAlias.Folded is the write-once key that names actually get matched by.
            MerchantName.Fold("istanbul market").Should().Be("ISTANBUL MARKET");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Preserves_accented_characters()
    {
        MerchantName.Fold("Café Central").Should().Be("CAFÉ CENTRAL");
    }

    [Fact]
    public void Preserves_punctuation()
    {
        MerchantName.Fold("7-Eleven #42").Should().Be("7-ELEVEN #42");
    }
}
