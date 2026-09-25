using AwesomeAssertions;
using Noof.Ledger.Host.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Noof.Ledger.Host.Tests;

public class RedactingSinkTests
{
    [Fact]
    public void The_inner_sink_receives_the_redacted_event_not_the_original()
    {
        var recording = new RecordingSink();
        var redactor = new SecretRedactor(new FixedSecrets("sk-super-secret-token"));
        var sink = new RedactingSink(recording, redactor);
        var evt = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse("test"),
            [new LogEventProperty("Token", new ScalarValue("sk-super-secret-token"))]);

        sink.Emit(evt);

        recording.Received.Should().ContainSingle();
        ((ScalarValue)recording.Received[0].Properties["Token"]).Value.Should().Be("***");
    }

    [Fact]
    public void Disposing_the_sink_disposes_an_inner_sink_that_is_disposable()
    {
        // Serilog's own Logger only flushes and closes a sink it holds directly if that sink is
        // IDisposable. RedactingSink sits between the outer pipeline and console+file+Postgres, so
        // without this it silently swallows disposal and the file sink's handle - and the Postgres
        // sink's batch - never close when the host shuts down. This is what LoggingBootstrapTests'
        // file-cleanup race actually caught.
        var recording = new RecordingSink();
        var sink = new RedactingSink(recording, new SecretRedactor(new FixedSecrets()));

        (sink as IDisposable)?.Dispose();

        recording.Disposed.Should().BeTrue("the inner sink must be disposed when RedactingSink is");
    }

    sealed class RecordingSink : ILogEventSink, IDisposable
    {
        public List<LogEvent> Received { get; } = [];
        public bool Disposed { get; private set; }
        public void Emit(LogEvent logEvent) => Received.Add(logEvent);
        public void Dispose() => Disposed = true;
    }

    sealed class FixedSecrets(params string[] values) : ISecretValueSource
    {
        public IReadOnlyCollection<string> CurrentValues => values;
    }
}
