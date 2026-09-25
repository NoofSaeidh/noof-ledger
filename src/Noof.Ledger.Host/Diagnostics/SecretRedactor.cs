using Serilog.Events;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class SecretRedactor(ISecretValueSource secrets)
{
    public LogEvent Redact(LogEvent logEvent)
    {
        var known = secrets.CurrentValues;

        var properties = logEvent.Properties
            .Select(pair => new LogEventProperty(pair.Key, RedactValue(pair.Value, known)));

        var exception = logEvent.Exception is { } original
            ? new RedactedException(original, RedactText(original.Message, known), RedactText(original.ToString(), known))
            : null;

        return new LogEvent(logEvent.Timestamp, logEvent.Level, exception, logEvent.MessageTemplate, properties);
    }

    static LogEventPropertyValue RedactValue(LogEventPropertyValue value, IReadOnlyCollection<string> secrets) => value switch
    {
        ScalarValue { Value: string text } => new ScalarValue(RedactText(text, secrets)),
        ScalarValue scalar => scalar,
        SequenceValue sequence => new SequenceValue(sequence.Elements.Select(element => RedactValue(element, secrets))),
        DictionaryValue dictionary => new DictionaryValue(dictionary.Elements
            .Select(pair => new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.Key, RedactValue(pair.Value, secrets)))),
        StructureValue structure => new StructureValue(
            structure.Properties.Select(property => new LogEventProperty(property.Name, RedactValue(property.Value, secrets))),
            structure.TypeTag),
        _ => value,
    };

    static string RedactText(string text, IReadOnlyCollection<string> secrets) =>
        secrets.Aggregate(text, (current, secret) => current.Replace(secret, "***", StringComparison.Ordinal));
}
