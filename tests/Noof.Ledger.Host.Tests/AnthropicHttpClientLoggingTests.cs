using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Host.Tests;

public class AnthropicHttpClientLoggingTests
{
    // IHttpClientFactory's own logging handlers redact header VALUES by default
    // (HttpClientFactoryOptions.ShouldRedactHeaderValue), but that default is just a delegate on
    // a per-client options object - reachable, and overridable, by anything that later calls
    // services.Configure<HttpClientFactoryOptions>("anthropic", ...), the same way an operator
    // might flip a log-level filter back on. RemoveAllLoggers takes the handlers out of the
    // pipeline entirely, so there is nothing left for such a change to re-expose - same proof
    // shape as TelegramHttpClientLoggingTests' "configuration reenables it" case.
    [Fact]
    public async Task No_captured_log_line_contains_the_anthropic_api_key_even_if_header_redaction_is_turned_off()
    {
        const string apiKey = "sk-ant-TopSecretApiKeyValue";
        var lines = new ConcurrentQueue<string>();

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", "Off");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.UseSetting("Logging:LogLevel:System.Net.Http.HttpClient.anthropic", "Trace");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(lines)));
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("anthropic").ConfigurePrimaryHttpMessageHandler(() => new StubHandler());
                services.Configure<HttpClientFactoryOptions>("anthropic", options => options.ShouldRedactHeaderValue = _ => false);
            });
        });

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("anthropic");
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/v1/models");
        request.Headers.Add("x-api-key", apiKey);
        await client.SendAsync(request, TestContext.Current.CancellationToken);

        lines.Should().NotContain(line => line.Contains(apiKey));
    }

    // Same reasoning as TelegramHttpClientLoggingTests' equivalent case: AddFilter is a
    // suppression, not a removal, and a more specific configured category beats a filter on a
    // shorter prefix. The key must stay out of the log even when both the log level AND the
    // redaction option are explicitly turned back on for this exact client name.
    [Fact]
    public async Task No_captured_log_line_contains_the_key_even_when_configuration_reenables_the_nested_logging_category()
    {
        const string apiKey = "sk-ant-ProbeTopSecretApiKey";
        var lines = new ConcurrentQueue<string>();

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", "Off");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.UseSetting("Logging:LogLevel:System.Net.Http.HttpClient.anthropic.LogicalHandler", "Trace");
            builder.UseSetting("Logging:LogLevel:System.Net.Http.HttpClient.anthropic.ClientHandler", "Trace");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(lines)));
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("anthropic").ConfigurePrimaryHttpMessageHandler(() => new StubHandler());
                services.Configure<HttpClientFactoryOptions>("anthropic", options => options.ShouldRedactHeaderValue = _ => false);
            });
        });

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("anthropic");
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/v1/models");
        request.Headers.Add("x-api-key", apiKey);
        await client.SendAsync(request, TestContext.Current.CancellationToken);

        lines.Should().NotContain(line => line.Contains(apiKey),
            "a more specific configured category and redaction override must not be able to re-enable the header logger");
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
