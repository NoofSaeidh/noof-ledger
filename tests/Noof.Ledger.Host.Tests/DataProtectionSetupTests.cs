using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Host.Startup;

namespace Noof.Ledger.Host.Tests;

public class DataProtectionSetupTests
{
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
