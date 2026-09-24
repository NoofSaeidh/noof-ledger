using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Ai.Groq;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Transcription;

namespace Noof.Ledger.Ai;

[SuppressMessage("Maintainability", "CA1515",
    Justification = "The one public type in this assembly, and the only way the Host can register "
        + "ICategorizer, IModelProvider, ITranscriber and ISpeechProvider without naming an implementation or "
        + "seeing a provider.")]
public static class AiRegistration
{
    public static IServiceCollection AddNoofAi(this IServiceCollection services, IConfiguration configuration)
    {
        // The two lines that choose the providers - one for the model, one for speech-to-text.
        // Everything they register - IChatClientFactory, ISpeechToTextClientFactory, IModelProvider,
        // ISpeechProvider, ISecretProbe - is provider-neutral; the implementation behind them is not.
        services.AddAnthropicChatClientFactory(configuration);
        services.AddGroqSpeechToText(configuration);

        services.AddScoped<ICategorizer, ChatCategorizer>();
        services.AddScoped<ITranscriber, SpeechTranscriber>();

        return services;
    }
}
