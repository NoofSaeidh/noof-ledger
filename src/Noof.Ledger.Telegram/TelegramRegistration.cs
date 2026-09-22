using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Chat;

namespace Noof.Ledger.Telegram;

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
        services.AddScoped<TelegramOwnerGate>();
        services.AddScoped<TelegramUpdateOffsetStore>();
        services.AddScoped<ITelegramUpdateRouter, TelegramUpdateRouter>();
        services.AddHostedService<TelegramPollingService>();

        return services;
    }
}
