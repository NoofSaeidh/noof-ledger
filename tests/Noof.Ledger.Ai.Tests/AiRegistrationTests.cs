using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;
using NSubstitute;

namespace Noof.Ledger.Ai.Tests;

public class AiRegistrationTests
{
    static ServiceProvider Provider(IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Substitute.For<ISecretStore>());
        services.AddNoofAi(configuration ?? new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddNoofAi_registers_the_provider_neutral_categorizer()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICategorizer>().Should().BeOfType<ChatCategorizer>();
    }

    [Fact]
    public void One_provider_instance_per_scope_answers_as_client_factory_model_provider_and_key_probe()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<IChatClientFactory>();

        factory.Should().BeOfType<AnthropicChatClientFactory>();
        scope.ServiceProvider.GetRequiredService<IModelProvider>().Should().BeSameAs(factory);
        scope.ServiceProvider.GetServices<ISecretProbe>().Should().ContainSingle().Which.Should().BeSameAs(factory);
    }

    [Fact]
    public void AddNoofAi_binds_the_Ai_configuration_section()
    {
        using var provider = Provider(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:MaxTokens"] = "4096" })
            .Build());

        provider.GetRequiredService<AnthropicOptions>().MaxTokens.Should().Be(4096);
    }
}
