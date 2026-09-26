using Microsoft.AspNetCore.Hosting;
using Noof.Ledger.Host.Startup;

namespace Noof.Ledger.Host.Tests;

// The same "never write into the operator's real directory" rule TestHostLogging enforces for
// Logging:File:Directory (Phase 5), extended to the key ring now that DataProtection:KeyRingDirectory
// is configurable too - before this, a WebApplicationFactory<Program> with no override generated
// and DPAPI-encrypted a real key straight into %LOCALAPPDATA%\NoofLedger\dp-keys on every test run.
public static class TestHostDataProtection
{
    public static string UseTempKeyRingDirectory(this IWebHostBuilder builder)
    {
        var directory = Directory.CreateTempSubdirectory("noof-host-test-dpkeys-").FullName;
        builder.UseSetting(DataProtectionSetup.KeyRingDirectoryConfigKey, directory);
        return directory;
    }
}
