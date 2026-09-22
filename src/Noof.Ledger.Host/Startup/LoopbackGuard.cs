using System.Net;

namespace Noof.Ledger.Host.Startup;

internal static class LoopbackGuard
{
    public static void AssertSafe(IReadOnlyList<string> boundAddresses)
    {
        var exposed = boundAddresses.Where(address => !IsLoopback(address)).ToArray();

        if (exposed.Length is 0)
            return;

        throw new InvalidOperationException(
            $"Refusing to start: {string.Join(", ", exposed)} is reachable beyond this machine. " +
            "Noof Ledger binds loopback only, by design — there is no configuration to widen this.");
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
