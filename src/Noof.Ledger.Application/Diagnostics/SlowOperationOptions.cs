namespace Noof.Ledger.Application.Diagnostics;

public sealed class SlowOperationOptions
{
    public const string ConfigurationSection = "Logging:SlowOperationMs";

    public Dictionary<string, int> ThresholdMs { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = 1000,
        ["model"] = 30000,
        ["speech"] = 15000,
        ["telegram"] = 3000,
        ["db"] = 1000,
        ["job"] = 60000,
        ["job.queueWait"] = 30000,
        ["backup"] = 600000,
        ["logs"] = 60000,
        ["health"] = 2000,
    };
}
