using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Transcription;
using Noof.Ledger.Telegram.Diagnostics;

namespace Noof.Ledger.Telegram;

[SuppressMessage("Maintainability", "CA1515",
    Justification = "The one public type in this assembly, and the only way the Host can start the "
        + "poller without naming TelegramPollingService or touching the bot token.")]
public static class TelegramRegistration
{
    const string HttpClientName = "telegram";

    public static IServiceCollection AddNoofTelegram(this IServiceCollection services)
    {
        // A log-level filter is a suppression a more specific configured category can override at
        // runtime - Logging:LogLevel:System.Net.Http.HttpClient.telegram.LogicalHandler beats a
        // filter on the shorter prefix and puts the full request URI, bot token included, at
        // Information. Removing the logging handlers means there is nothing left to re-enable.
        services.AddHttpClient(HttpClientName).RemoveAllLoggers();

        services.AddSingleton<TelegramClientHandle>();
        services.AddSingleton<ITelegramBotClientFactory, TelegramBotClientFactory>();
        services.AddSingleton<IChatNotifier, TelegramChatNotifier>();
        services.AddSingleton<IVoiceFileSource, TelegramVoiceFileSource>();
        services.AddScoped<TelegramOwnerGate>();
        services.AddScoped<TelegramUpdateOffsetStore>();
        services.AddScoped<RecordActionHandler>();
        services.AddScoped<CorrectionHandler>();
        services.AddScoped<ITelegramUpdateRouter, TelegramUpdateRouter>();
        services.AddHostedService<TelegramPollingService>();

        services.AddScoped<ISystemHealthCheck, TelegramHealthCheck>();

        return services;
    }
}
