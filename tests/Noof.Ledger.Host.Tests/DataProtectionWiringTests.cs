using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Noof.Ledger.Host.Tests;

public class DataProtectionWiringTests
{
    // Never Factory()'s own default (real) key ring directory here - see TestHostDataProtection's
    // own comment on why every host built here must override it to a temp folder.
    static (WebApplicationFactory<Program> Factory, string KeyRingDirectory) FactoryWithTempKeyRing()
    {
        string? keyRingDirectory = null;
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            keyRingDirectory = builder.UseTempKeyRingDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });
        // WithWebHostBuilder only registers the configuration callback above - it does not run it
        // (and therefore never sets keyRingDirectory) until the host is actually built, which
        // touching .Services forces here so the caller can rely on keyRingDirectory immediately.
        _ = factory.Services;
        return (factory, keyRingDirectory!);
    }

    [Fact]
    public void Keys_persist_under_the_configured_directory()
    {
        var (factory, keyRingDirectory) = FactoryWithTempKeyRing();
        using var _ = factory;

        var options = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        var repository = options.XmlRepository.Should().BeOfType<FileSystemXmlRepository>().Subject;
        repository.Directory.FullName.Should().Be(keyRingDirectory);
    }

    [Fact]
    public void Keys_are_protected_with_Dpapi()
    {
        var (factory, _) = FactoryWithTempKeyRing();
        using var _1 = factory;

        var options = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        options.XmlEncryptor.Should().BeOfType<DpapiXmlEncryptor>();
    }
}
