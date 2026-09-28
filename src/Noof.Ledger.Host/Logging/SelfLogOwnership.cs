using System.Net;
using System.Net.Sockets;
using Noof.Ledger.Application.Diagnostics;
using Npgsql;
using Serilog.Debugging;

namespace Noof.Ledger.Host.Logging;

// Serilog.Debugging.SelfLog.Enable replaces the process's one self-log delegate outright - it does
// not compose across hosts, and a Postgres sink failure is asynchronous, so nothing tells a later
// host apart from an earlier one just by which one happens to be "current" when the failure
// surfaces. SelfLogSinkFailureTests reproduced this as a flake; writing every raw SelfLog
// message to a file while reproducing it showed the actual source: an *orphaned* host - almost
// certainly the throwaway host WebApplicationFactory's HostFactoryResolver builds and discards to
// probe a minimal-hosting Program.cs before building the real one - is never disposed, so its own
// Postgres sink keeps retrying against its own unreachable connection for the rest of the process,
// each retry's "Failed to connect to <host>:<port>" landing on whichever claim happened to be
// current at that exact moment.
//
// A pure "current owner, cleared by its own Dispose" registration cannot close this on its own: the
// orphaned host is never disposed, so there is nothing to release it. What *is* available is
// content - a connection-refused message names the host:port it tried, and that never matches a
// different claim's own connection unless the two happen to share one. Every failure still defaults
// to being attributed to whoever is current, exactly as before, for a message that names no
// connection at all (a locked log file, a missing table) - only one that names a specific,
// *different* host:port than the current claim's own is dropped, however stale the claim that
// produced it. This is why SelfLogSinkFailureTests below uses a connection string no other test in
// the suite shares: the comparison needs two different identities to have anything to bite on.
internal static class SelfLogOwnership
{
    static readonly Lock Gate = new();
    static object? currentOwner;
    static ILogSinkStatus? currentStatus;
    static TimeProvider? currentTimeProvider;
    static IReadOnlyCollection<string> currentIdentities = [];
    static bool hookInstalled;

    public static IDisposable Claim(ILogSinkStatus sinkStatus, TimeProvider timeProvider, string connectionString)
    {
        var owner = new object();

        // Resolved once, here, rather than on every SelfLog callback: DNS resolution belongs to the
        // claim, not to each message it might later need to judge.
        var identities = IdentitiesOf(connectionString);

        lock (Gate)
        {
            currentOwner = owner;
            currentStatus = sinkStatus;
            currentTimeProvider = timeProvider;
            currentIdentities = identities;
            EnsureHookInstalledLocked();
        }

        return new Registration(owner);
    }

    static void EnsureHookInstalledLocked()
    {
        if (hookInstalled)
            return;

        SelfLog.Enable(message =>
        {
            ILogSinkStatus? status;
            TimeProvider? time;
            IReadOnlyCollection<string> identities;
            lock (Gate)
            {
                status = currentStatus;
                time = currentTimeProvider;
                identities = currentIdentities;
            }

            if (identities.Count > 0 && NamesADifferentConnection(message, identities))
                return;

            status?.RecordFailure((time ?? TimeProvider.System).GetUtcNow());
        });

        hookInstalled = true;
    }

    static bool NamesADifferentConnection(string message, IReadOnlyCollection<string> identities) =>
        message.Contains("Failed to connect to ", StringComparison.Ordinal)
        && !identities.Any(identity => message.Contains(identity, StringComparison.Ordinal));

    // Npgsql's own connection-failure message names the RESOLVED endpoint it dialled, formatted exactly
    // as IPEndPoint.ToString() renders it ("127.0.0.1:5432", "[::1]:5432") - never the configured host
    // name (verified against Npgsql 10.0.3: "Host=localhost" fails as "Failed to connect to
    // 127.0.0.1:<port>"). An IP literal (v4 or v6) is used as-is; a host name is resolved through DNS so
    // every address it could dial is in the identity set. A Unix-socket path (starts with "/") or a
    // failed resolution names no TCP endpoint at all, so it carries no identity - the claim then falls
    // back to defaulting every failure to itself, exactly as when the message names no connection.
    static IReadOnlyCollection<string> IdentitiesOf(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var host = builder.Host;

        if (string.IsNullOrEmpty(host) || host.StartsWith('/'))
            return [];

        if (IPAddress.TryParse(host, out var literal))
            return [new IPEndPoint(literal, builder.Port).ToString()];

        try
        {
            return [.. Dns.GetHostAddresses(host).Select(address => new IPEndPoint(address, builder.Port).ToString())];
        }
        catch (SocketException)
        {
            return [];
        }
    }

    sealed class Registration(object owner) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
            {
                if (!ReferenceEquals(currentOwner, owner))
                    return;

                currentOwner = null;
                currentStatus = null;
                currentTimeProvider = null;
                currentIdentities = [];
            }
        }
    }
}
