using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai;

[SuppressMessage("Maintainability", "CA1515",
    Justification = "The one public type in this assembly, and the only way the Host can register "
        + "ICategorizer without naming an implementation or seeing the Anthropic SDK.")]
public static class AiRegistration
{
    const string HttpClientName = "anthropic";

    public static IServiceCollection AddNoofAi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new AnthropicOptions();
        configuration.GetSection("Ai").Bind(options);
        services.AddSingleton(options);

        // IHttpClientFactory's default logging handlers redact header values through
        // HttpClientFactoryOptions.ShouldRedactHeaderValue, but that is a per-client options
        // delegate reachable by anything that later calls services.Configure<HttpClientFactoryOptions>
        // ("anthropic", ...). x-api-key carries the Anthropic key on every request; removing the
        // logging handlers means there is nothing left for such a change to re-expose.
        services.AddHttpClient(HttpClientName).RemoveAllLoggers();

        // AddHttpClient registers IHttpClientFactory, never an HttpClient - AnthropicClientFactory
        // takes a real client, so it has to be built through a lambda.
        services.AddScoped<IAnthropicClientFactory>(sp => new AnthropicClientFactory(
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            sp.GetRequiredService<AnthropicOptions>()));

        services.AddScoped<ICategorizer, AnthropicCategorizer>();
        services.AddScoped<ISecretProbe, AnthropicKeyProbe>();

        return services;
    }
}
