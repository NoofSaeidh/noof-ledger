using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Noof.Ledger.Host.Startup;

public static class DataProtectionSetup
{
    public static void Configure(IServiceCollection services, DirectoryInfo keyRingDirectory) =>
        services.AddDataProtection()
            .SetApplicationName("Noof.Ledger")
            .PersistKeysToFileSystem(keyRingDirectory)
            .ProtectKeysWithDpapi();
}
