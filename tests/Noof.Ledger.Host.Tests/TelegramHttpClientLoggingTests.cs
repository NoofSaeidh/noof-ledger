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
            builder.UseSetting("Auth:Mode", "Off");
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
