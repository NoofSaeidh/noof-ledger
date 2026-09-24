namespace Noof.Ledger.Application.Diagnostics;

public interface ILogSinkStatus
{
    DateTimeOffset? LastFailureAt { get; }
    void RecordFailure(DateTimeOffset at);
}
