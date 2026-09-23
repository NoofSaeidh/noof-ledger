using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai;

[SuppressMessage("Maintainability", "CA1515",
    Justification = "The one public type in this assembly, and the only way the Host can register "
        + "ICategorizer and IModelProvider without naming an implementation or seeing a provider SDK.")]
public static class AiRegistration
{
    public static IServiceCollection AddNoofAi(this IServiceCollection services, IConfiguration configuration)
    {
        // The one line that chooses the provider. Everything it registers - IChatClientFactory,
        // IModelProvider, ISecretProbe - is provider-neutral; the implementation behind them is not.
        services.AddAnthropicChatClientFactory(configuration);

        services.AddScoped<ICategorizer, ChatCategorizer>();

        return services;
    }
}
