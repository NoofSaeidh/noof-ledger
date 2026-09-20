using System.Net;

namespace Noof.Ledger.Host.Startup;

public static class LoopbackGuard
{
    public static void AssertSafe(IReadOnlyList<string> boundAddresses, string authMode)
    {
        if (!string.Equals(authMode, "Off", StringComparison.OrdinalIgnoreCase))
            return;

        var exposed = boundAddresses.Where(address => !IsLoopback(address)).ToArray();

        if (exposed.Length is 0)
            return;

        throw new InvalidOperationException(
            $"Refusing to start: {string.Join(", ", exposed)} is reachable beyond this machine while " +
            $"Auth:Mode=Off. Set Auth:Mode=Cookie and create a user with `Noof.Ledger.Host.exe user set-password <name>`.");
    }

    static bool IsLoopback(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;

        if (host is "+" or "*")
            return false;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
