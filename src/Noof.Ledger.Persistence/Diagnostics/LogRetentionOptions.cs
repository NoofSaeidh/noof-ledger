using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class LogRetentionOptions
{
    public RetentionDays Days { get; init; } = new();

    internal sealed class RetentionDays
    {
        public int Verbose { get; init; } = 1;
        public int Debug { get; init; } = 1;
        public int Information { get; init; } = 90;
        public int Warning { get; init; } = 730;
        public int Error { get; init; } = 730;
        public int Fatal { get; init; } = 730;

        public int For(LogSeverity level) => level switch
        {
            LogSeverity.Verbose => Verbose,
            LogSeverity.Debug => Debug,
            LogSeverity.Information => Information,
            LogSeverity.Warning => Warning,
            LogSeverity.Error => Error,
            LogSeverity.Fatal => Fatal,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
        };
    }
}
