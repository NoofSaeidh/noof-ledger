using Noof.Ledger.Application.Diagnostics;
using Npgsql;
using Serilog.Debugging;

namespace Noof.Ledger.Host.Logging;

// Serilog.Debugging.SelfLog.Enable replaces the process's one self-log delegate outright - it does
// not compose across hosts, and a Postgres sink failure is asynchronous, so nothing tells a later
// host apart from an earlier one just by which one happens to be "current" when the failure
// surfaces. SelfLogSinkFailureTests reproduced this as a flake
// (docs/superpowers/sdd/2026-09-25-phase6-receipts/fix-fd-report.md); writing every raw SelfLog
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
    static string? currentIdentity;
    static bool hookInstalled;

    public static IDisposable Claim(ILogSinkStatus sinkStatus, TimeProvider timeProvider, string connectionString)
    {
        var owner = new object();

        lock (Gate)
        {
            currentOwner = owner;
            currentStatus = sinkStatus;
            currentTimeProvider = timeProvider;
            currentIdentity = HostPortOf(connectionString);
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
            string? identity;
            lock (Gate)
            {
                status = currentStatus;
                time = currentTimeProvider;
                identity = currentIdentity;
            }

            if (identity is not null && NamesADifferentConnection(message, identity))
                return;

            status?.RecordFailure((time ?? TimeProvider.System).GetUtcNow());
        });

        hookInstalled = true;
    }

    static bool NamesADifferentConnection(string message, string currentIdentity) =>
        message.Contains("Failed to connect to ", StringComparison.Ordinal)
        && !message.Contains(currentIdentity, StringComparison.Ordinal);

    static string HostPortOf(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return $"{builder.Host}:{builder.Port}";
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
                currentIdentity = null;
            }
        }
    }
}
