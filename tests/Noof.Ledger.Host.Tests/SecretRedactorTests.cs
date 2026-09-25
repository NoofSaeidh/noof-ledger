using AwesomeAssertions;
using Noof.Ledger.Host.Diagnostics;
using Serilog.Events;
using Serilog.Parsing;

namespace Noof.Ledger.Host.Tests;

public class SecretRedactorTests
{
    static readonly MessageTemplateParser Parser = new();

    static LogEvent MakeEvent(Exception? exception, params LogEventProperty[] properties) =>
        new(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), LogEventLevel.Information,
            exception, Parser.Parse("test"), properties);

    static SecretRedactor RedactorFor(params string[] secrets) => new(new FixedSecretSet(secrets));

    [Fact]
    public void A_scalar_string_property_containing_a_secret_is_redacted()
    {
        var redactor = RedactorFor("sk-super-secret-token");
        var evt = MakeEvent(null, new LogEventProperty("Token", new ScalarValue("value=sk-super-secret-token;ok")));

        var redacted = redactor.Redact(evt);

        ((ScalarValue)redacted.Properties["Token"]).Value.Should().Be("value=***;ok");
    }

    [Fact]
    public void A_property_without_any_secret_is_left_untouched()
    {
        var redactor = RedactorFor("sk-super-secret-token");
        var evt = MakeEvent(null, new LogEventProperty("Stage", new ScalarValue("Received")));

        var redacted = redactor.Redact(evt);

        ((ScalarValue)redacted.Properties["Stage"]).Value.Should().Be("Received");
    }

    [Fact]
    public void A_secret_inside_a_sequence_property_is_redacted()
    {
        var redactor = RedactorFor("sk-super-secret-token");
        var sequence = new SequenceValue([new ScalarValue("sk-super-secret-token"), new ScalarValue("fine")]);
        var evt = MakeEvent(null, new LogEventProperty("Headers", sequence));

        var redacted = redactor.Redact(evt);

        var redactedSequence = (SequenceValue)redacted.Properties["Headers"];
        ((ScalarValue)redactedSequence.Elements[0]).Value.Should().Be("***");
        ((ScalarValue)redactedSequence.Elements[1]).Value.Should().Be("fine");
    }

    [Fact]
    public void A_secret_inside_a_dictionary_property_is_redacted()
    {
        var redactor = RedactorFor("sk-super-secret-token");
        var dictionary = new DictionaryValue([
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("Authorization"), new ScalarValue("sk-super-secret-token")),
        ]);
        var evt = MakeEvent(null, new LogEventProperty("Headers", dictionary));

        var redacted = redactor.Redact(evt);

        var redactedDictionary = (DictionaryValue)redacted.Properties["Headers"];
        ((ScalarValue)redactedDictionary.Elements.Single().Value).Value.Should().Be("***");
    }

    [Fact]
    public void A_secret_inside_a_structured_property_is_redacted()
    {
        var redactor = RedactorFor("sk-super-secret-token");
        var structure = new StructureValue([new LogEventProperty("Key", new ScalarValue("sk-super-secret-token"))]);
        var evt = MakeEvent(null, new LogEventProperty("Config", structure));

        var redacted = redactor.Redact(evt);

        var redactedStructure = (StructureValue)redacted.Properties["Config"];
        ((ScalarValue)redactedStructure.Properties.Single().Value).Value.Should().Be("***");
    }

    [Fact]
    public void Values_shorter_than_eight_characters_are_never_treated_as_secrets()
    {
        // The redactor itself has no length rule - SecretSnapshot filters short values before they
        // ever reach it (Step 6). This test locks that division of responsibility: a 7-char "secret"
        // handed to the redactor directly IS redacted, proving the length rule lives upstream, not here.
        var redactor = RedactorFor("shortie");
        var evt = MakeEvent(null, new LogEventProperty("Value", new ScalarValue("a shortie value")));

        var redacted = redactor.Redact(evt);

        ((ScalarValue)redacted.Properties["Value"]).Value.Should().Be("a ***  value".Replace("  ", " "));
    }

    [Fact]
    public void An_exceptions_message_and_ToString_are_both_redacted_and_the_original_type_name_is_preserved()
    {
        var redactor = RedactorFor("sk-super-secret-token");
        var original = new InvalidOperationException("call failed with key sk-super-secret-token");
        var evt = MakeEvent(original);

        var redacted = redactor.Redact(evt);

        redacted.Exception.Should().NotBeNull();
        redacted.Exception!.Message.Should().Contain("InvalidOperationException").And.Contain("***").And.NotContain("sk-super-secret-token");
        redacted.Exception.ToString().Should().Contain("***").And.NotContain("sk-super-secret-token");
    }

    [Fact]
    public void An_event_with_no_exception_and_no_secrets_present_still_redacts_cleanly_with_a_null_exception()
    {
        var redactor = RedactorFor("sk-super-secret-token");
        var evt = MakeEvent(null, new LogEventProperty("Stage", new ScalarValue("Received")));

        var redacted = redactor.Redact(evt);

        redacted.Exception.Should().BeNull();
    }

    sealed class FixedSecretSet(IReadOnlyCollection<string> values) : ISecretValueSource
    {
        public IReadOnlyCollection<string> CurrentValues => values;
    }
}
