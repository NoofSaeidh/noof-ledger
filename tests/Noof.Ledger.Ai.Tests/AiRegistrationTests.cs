using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;
using NSubstitute;

namespace Noof.Ledger.Ai.Tests;

public class AiRegistrationTests
{
    [Fact]
    public void AddNoofAi_registers_the_categorizer_and_the_key_probe()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Substitute.For<ISecretStore>());
        services.AddNoofAi(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICategorizer>().Should().BeOfType<AnthropicCategorizer>();
        scope.ServiceProvider.GetServices<ISecretProbe>().Should().ContainSingle()
            .Which.Should().BeOfType<AnthropicKeyProbe>();
    }

    [Fact]
    public void AddNoofAi_binds_the_Ai_configuration_section()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Substitute.For<ISecretStore>());
        services.AddNoofAi(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:MaxTokens"] = "4096" })
            .Build());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AnthropicOptions>().MaxTokens.Should().Be(4096);
    }
}
