using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Transcription;

namespace Noof.Ledger.Ai.Groq;

internal static class GroqRegistration
{
    const string HttpClientName = "groq";

    public static IServiceCollection AddGroqSpeechToText(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new GroqOptions();
        configuration.GetSection("Ai:Groq").Bind(options);
        services.AddSingleton(options);

        // The key rides in the Authorization header. With the logging handlers removed there is nothing a log-level
        // setting could turn back on.
        services.AddHttpClient(HttpClientName, client => client.Timeout = options.Timeout).RemoveAllLoggers();

        services.AddScoped(sp => new GroqSpeechToTextClientFactory(
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            sp.GetRequiredService<GroqOptions>(),
            sp.GetRequiredService<IOperationTimer>(),
            sp.GetRequiredService<ILogger<GroqSpeechToTextClientFactory>>()));

        services.AddScoped<ISpeechToTextClientFactory>(sp => sp.GetRequiredService<GroqSpeechToTextClientFactory>());
        services.AddScoped<ISpeechProvider>(sp => sp.GetRequiredService<GroqSpeechToTextClientFactory>());
        services.AddScoped<ISecretProbe>(sp => sp.GetRequiredService<GroqSpeechToTextClientFactory>());

        return services;
    }
}
