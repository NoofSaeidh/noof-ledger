using Microsoft.AspNetCore.DataProtection;

namespace Noof.Ledger.Host.Startup;

internal static class DataProtectionSetup
{
    public static void Configure(IServiceCollection services, DirectoryInfo keyRingDirectory) =>
        services.AddDataProtection()
            .SetApplicationName("Noof.Ledger")
            .PersistKeysToFileSystem(keyRingDirectory)
            .ProtectKeysWithDpapi();
}
