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
    static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public void Keys_persist_under_the_dedicated_NoofLedger_folder()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        var repository = options.XmlRepository.Should().BeOfType<FileSystemXmlRepository>().Subject;
        repository.Directory.FullName.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "dp-keys"));
    }

    [Fact]
    public void Keys_are_protected_with_Dpapi()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        options.XmlEncryptor.Should().BeOfType<DpapiXmlEncryptor>();
    }
}
