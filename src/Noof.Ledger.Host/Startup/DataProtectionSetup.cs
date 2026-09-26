using Microsoft.AspNetCore.DataProtection;

namespace Noof.Ledger.Host.Startup;

internal static class DataProtectionSetup
{
    public const string KeyRingDirectoryConfigKey = "DataProtection:KeyRingDirectory";

    const string DefaultKeyRingDirectory = @"%LOCALAPPDATA%\NoofLedger\dp-keys";

    public static void Configure(IServiceCollection services, DirectoryInfo keyRingDirectory) =>
        services.AddDataProtection()
            .SetApplicationName("Noof.Ledger")
            .PersistKeysToFileSystem(keyRingDirectory)
            .ProtectKeysWithDpapi();

    public static DirectoryInfo ResolveKeyRingDirectory(IConfiguration configuration) =>
        new(Environment.ExpandEnvironmentVariables(configuration[KeyRingDirectoryConfigKey] ?? DefaultKeyRingDirectory));
}
