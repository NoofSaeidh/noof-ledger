using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Application.Diagnostics;

public interface IOperationTimer
{
    OperationTiming Start(ILogger logger, string operation, TimeSpan expectedWait = default);

    void Record(ILogger logger, string operation, TimeSpan elapsed, bool onlyIfSlow = false, TimeSpan expectedWait = default);
}
