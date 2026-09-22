using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Host.Tests;

public class TelegramHttpClientLoggingTests
{
    [Fact]
    public async Task No_captured_log_line_contains_the_telegram_token()
    {
        const string token = "123456:AAETopSecretBotTokenValue";
        var lines = new ConcurrentQueue<string>();

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(lines)));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => new StubHandler()));
        });

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("telegram");
        await client.GetAsync($"http://example.invalid/bot{token}/getMe", TestContext.Current.CancellationToken);

        lines.Should().NotContain(line => line.Contains(token));
    }

    // AddFilter("System.Net.Http.HttpClient.telegram", LogLevel.None) is a suppression, not a
    // removal: a more specific configured category wins over a filter on a shorter prefix. An
    // operator troubleshooting "why isn't my bot receiving messages" reaching for
    // Logging:LogLevel:System.Net.Http.HttpClient.telegram.LogicalHandler is exactly the kind of
    // configuration a filter-only fix cannot survive - the token must stay out of the log even
    // when that category is explicitly turned back on.
    [Fact]
    public async Task No_captured_log_line_contains_the_token_even_when_configuration_reenables_the_nested_logging_category()
    {
        const string token = "123456:AAProbeTopSecretBotToken";
        var lines = new ConcurrentQueue<string>();

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.UseSetting("Logging:LogLevel:System.Net.Http.HttpClient.telegram.LogicalHandler", "Information");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(lines)));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => new StubHandler()));
        });

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("telegram");
        await client.GetAsync($"http://example.invalid/bot{token}/getMe", TestContext.Current.CancellationToken);

        lines.Should().NotContain(line => line.Contains(token),
            "a more specific configured category must not be able to re-enable the request-URI logger");
    }

    sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    sealed class CapturingLoggerProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(lines);
        public void Dispose() { }

        sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception));
        }
    }
}
