using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal static class DiagnosticsHostRegistration
{
    public static IServiceCollection AddNoofDiagnosticsHost(this IServiceCollection services) =>
        services.AddSingleton<ILogFileTail, LogFileTail>();
}
