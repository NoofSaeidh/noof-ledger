using System.Globalization;

namespace Noof.Ledger.TestKit;

// .NET Core flows CultureInfo.CurrentCulture through ExecutionContext, so setting it here is
// visible to every awaited call inside the scope, not just synchronous code on this thread.
public sealed class CultureScope : IDisposable
{
    readonly CultureInfo previousCulture = CultureInfo.CurrentCulture;
    readonly CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

    public CultureScope(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = previousCulture;
        CultureInfo.CurrentUICulture = previousUiCulture;
    }
}
