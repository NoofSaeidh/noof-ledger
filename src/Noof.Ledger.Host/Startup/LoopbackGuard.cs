using System.Net;

namespace Noof.Ledger.Host.Startup;

internal static class LoopbackGuard
{
    public static void AssertSafe(IReadOnlyList<string> boundAddresses, string authMode)
    {
        // Enforce for every mode EXCEPT Cookie, rather than only for Off. An unrecognised mode
        // string registers the no-auth handler in Program.cs, so skipping the check on anything
        // we do not recognise would disable this interlock on a config typo — in exactly the
        // case where there is no authentication at all.
        if (string.Equals(authMode, "Cookie", StringComparison.OrdinalIgnoreCase))
            return;

        var exposed = boundAddresses.Where(address => !IsLoopback(address)).ToArray();

        if (exposed.Length is 0)
            return;

        throw new InvalidOperationException(
            $"Refusing to start: {string.Join(", ", exposed)} is reachable beyond this machine while " +
            $"Auth:Mode={authMode}. Set Auth:Mode=Cookie and create a user with `Noof.Ledger.Host.exe user set-password <name>`.");
    }

    // Anything that is not demonstrably loopback is treated as exposed, so unparseable input
    // fails closed. Kestrel's "+" and "*" wildcards reach that path because Uri.TryCreate
    // rejects them outright — verified on .NET 10, so there is no separate branch for them.
    static bool IsLoopback(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return false;

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
