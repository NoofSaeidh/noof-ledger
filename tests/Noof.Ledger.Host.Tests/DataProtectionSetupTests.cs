using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Host.Startup;

namespace Noof.Ledger.Host.Tests;

public class DataProtectionSetupTests
{
    [Fact]
    public void With_no_configured_value_the_key_ring_directory_defaults_to_the_NoofLedger_folder()
    {
        var configuration = new ConfigurationBuilder().Build();

        var directory = DataProtectionSetup.ResolveKeyRingDirectory(configuration);

        directory.FullName.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "dp-keys"));
    }

    [Fact]
    public void A_configured_key_ring_directory_expands_environment_variables()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DataProtectionSetup.KeyRingDirectoryConfigKey] = @"%LOCALAPPDATA%\NoofDataProtectionResolverTest",
            })
            .Build();

        var directory = DataProtectionSetup.ResolveKeyRingDirectory(configuration);

        directory.FullName.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofDataProtectionResolverTest"));
    }

    [Fact]
    public void The_key_ring_never_stores_key_material_in_plaintext()
    {
        var keyRingDirectory = Directory.CreateTempSubdirectory("noof-dp-sentinel-");

        try
        {
            var services = new ServiceCollection();
            DataProtectionSetup.Configure(services, keyRingDirectory);

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(nameof(DataProtectionSetupTests))
                .Protect("force-a-key-to-be-generated");

            var keyRingFiles = Directory.GetFiles(keyRingDirectory.FullName, "key-*.xml");

            keyRingFiles.Should().NotBeEmpty("protecting a payload must generate a key file");

            foreach (var file in keyRingFiles)
            {
                File.ReadAllText(file).Should().NotContain("is in an unencrypted form",
                    $"{Path.GetFileName(file)} must not store its master key in plaintext — this is a public repo");
            }
        }
        finally
        {
            keyRingDirectory.Delete(recursive: true);
        }
    }
}
