using System.Globalization;
using MudBlazor;

namespace Noof.Ledger.Web.Components;

// MudBlazor's default number converter parses with NumberStyles.Any, so under the invariant culture a decimal comma is
// read as a thousands separator ("117,35" -> 11735), and text it cannot parse becomes default - null - in the bound
// value. This one takes a point or a comma as the decimal separator, no thousands separator, and remembers a refusal
// so the page can refuse to save. One instance per field: the state is the field's.
internal sealed class DecimalFieldConverter : IReversibleConverter<decimal?, string?>, ICultureAwareConverter
{
    const NumberStyles Accepted = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite
        | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    public Func<CultureInfo> Culture { get; set; } = () => CultureInfo.InvariantCulture;

    public Func<string?> Format { get; set; } = () => null;

    public bool Failed { get; private set; }

    public string? Convert(decimal? input) => input?.ToString(Format(), Culture());

    public decimal? ConvertBack(string? input)
    {
        Failed = false;

        if (string.IsNullOrWhiteSpace(input))
            return null;

        if (decimal.TryParse(input.Replace(',', '.'), Accepted, CultureInfo.InvariantCulture, out var value))
            return value;

        Failed = true;
        throw new FormatException("Not a number: use one decimal separator (a point or a comma) and no thousands separator.");
    }
}
