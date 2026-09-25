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

    sealed class RecordingSink : ILogEventSink
    {
        public List<LogEvent> Received { get; } = [];
        public void Emit(LogEvent logEvent) => Received.Add(logEvent);
    }

    sealed class FixedSecrets(params string[] values) : ISecretValueSource
    {
        public IReadOnlyCollection<string> CurrentValues => values;
    }
}
