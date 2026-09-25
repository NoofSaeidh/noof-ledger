using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class LogSinkStatus : ILogSinkStatus
{
    DateTimeOffset? lastFailureAt;

    public DateTimeOffset? LastFailureAt => lastFailureAt;

    public void RecordFailure(DateTimeOffset at) => lastFailureAt = at;
}
