using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.BugReports;

// The findings and log_lines columns. Amounts are decimal strings and instants round-trip "O" in UTC, so nothing passes
// through a JSON number or a local time. Free text loses its fiscal link before it is written and is cut only after
// that, so a cut never leaves half a link behind. A column that cannot be read back reads as not collected (null),
// never as an empty list.
internal static class BugReportJson
{
    public const int MaxFieldLength = 2000;

    public const string TelegramIdMark = "[telegram id]";

    static readonly string[] TelegramIdSuffixes = ["ChatId", "MessageId", "FileId"];

    // PropertiesJson is shown to the operator (escaped by Razor, fenced in the Markdown); \u-escaping Cyrillic or "+"
    // would only make it unreadable there.
    static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<IntegrityFinding> WithoutLinks(
        IReadOnlyList<IntegrityFinding> findings, IFiscalVerificationUrl verificationUrl) =>
    [
        .. findings.Select(finding => finding with
        {
            Facts =
            [
                .. finding.Facts.Select(fact =>
                    fact is TextFact text ? text with { Text = verificationUrl.StripUrl(text.Text) ?? "" } : fact),
            ],
        }),
    ];

    public static string WriteFindings(IReadOnlyList<IntegrityFinding> findings, IFiscalVerificationUrl verificationUrl) =>
        JsonSerializer.Serialize<List<FindingJson>>([.. WithoutLinks(findings, verificationUrl).Select(ToJson)]);

    public static IReadOnlyList<IntegrityFinding>? ReadFindings(string? json)
    {
        if (json is null)
            return null;

        try
        {
            var findings = JsonSerializer.Deserialize<List<FindingJson?>>(json)
                ?? throw new FormatException("The findings column holds a JSON null.");
            return [.. findings.Select(finding => ToFinding(Present(finding)))];
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public static string WriteLogLines(IReadOnlyList<LogRow> rows, IFiscalVerificationUrl verificationUrl) =>
        JsonSerializer.Serialize<List<LogLineJson>>([.. rows.Select(row => LogLine(row, verificationUrl))]);

    // Spec P-22: existing events log Telegram ids ("{Stage} to bot message {BotMessageId}"), and SecretRedactor leaves
    // numbers alone. A report holds none: the id properties become the mark, and so do their values in the text - before
    // the fiscal strip and the cut.
    static LogLineJson LogLine(LogRow row, IFiscalVerificationUrl verificationUrl)
    {
        var properties = Properties(row.PropertiesJson);
        IReadOnlyList<string> telegramIds = properties is null
            ? []
            : [.. properties.Where(property => IsTelegramId(property.Key)).Select(property => property.Value)];

        return new LogLineJson(
            row.LoggedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            row.Level.ToString(),
            row.Source,
            Cut(verificationUrl.StripUrl(WithoutTelegramIds(row.Message, telegramIds)) ?? ""),
            row.Exception is { } raw && verificationUrl.StripUrl(WithoutTelegramIds(raw, telegramIds)) is { } exception
                ? Cut(exception)
                : null,
            properties?.ToDictionary(
                property => property.Key,
                property => IsTelegramId(property.Key) ? TelegramIdMark : Cut(verificationUrl.StripUrl(property.Value) ?? ""),
                StringComparer.Ordinal));
    }

    static bool IsTelegramId(string propertyName) =>
        TelegramIdSuffixes.Any(suffix => propertyName.EndsWith(suffix, StringComparison.Ordinal));

    // Whole tokens only: 45678 keeps its digits when the id is 4567.
    static string WithoutTelegramIds(string text, IReadOnlyList<string> telegramIds) =>
        telegramIds.Where(id => id.Length > 0).Aggregate(text, (current, id) => Regex.Replace(
            current, $@"(?<![\p{{L}}\p{{Nd}}_-]){Regex.Escape(id)}(?![\p{{L}}\p{{Nd}}_-])", TelegramIdMark));

    public static IReadOnlyList<BugReportLogLine>? ReadLogLines(string? json)
    {
        if (json is null)
            return null;

        try
        {
            var lines = JsonSerializer.Deserialize<List<LogLineJson?>>(json)
                ?? throw new FormatException("The log_lines column holds a JSON null.");
            return
            [
                .. lines.Select(Present).Select(line => new BugReportLogLine(
                    DateTimeOffset.ParseExact(Required(line.LoggedAt), "O", CultureInfo.InvariantCulture),
                    Name<LogSeverity>(line.Level),
                    line.Source,
                    Required(line.Message),
                    line.Exception,
                    line.Properties is null ? null : JsonSerializer.Serialize(line.Properties, Readable))),
            ];
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    static FindingJson ToJson(IntegrityFinding finding) => new(
        finding.Check.ToString(), finding.Group.ToString(), finding.TransactionId, finding.WalletId, finding.JobId,
        [.. finding.Facts.Select(ToJson)]);

    static FactJson ToJson(IntegrityFact fact) => fact switch
    {
        TextFact text => new("text", text.Name, Text: text.Text),
        MoneyFact money => new("money", money.Name, Amount: money.Amount.ToString(CultureInfo.InvariantCulture),
            Currency: money.Currency.Value),
        DateFact date => new("date", date.Name, Day: date.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        SinceFact since => new("since", since.Name,
            Since: since.Since.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        CountFact count => new("count", count.Name, Count: count.Count),
        _ => throw new ArgumentOutOfRangeException(nameof(fact), fact.GetType().Name, "An integrity fact with no JSON form."),
    };

    static IntegrityFinding ToFinding(FindingJson finding) => new(
        KnownName<IntegrityCheck>(finding.Check),
        KnownName<IntegrityGroup>(finding.Group),
        finding.TransactionId,
        finding.WalletId,
        finding.JobId,
        [.. (finding.Facts ?? throw new FormatException("A finding without its facts.")).Select(fact => ToFact(Present(fact)))]);

    static IntegrityFact ToFact(FactJson fact) => fact.Kind switch
    {
        "text" => new TextFact(Required(fact.Name), Required(fact.Text)),
        "money" => new MoneyFact(
            Required(fact.Name),
            decimal.Parse(Required(fact.Amount), NumberStyles.Number, CultureInfo.InvariantCulture),
            new CurrencyCode(Required(fact.Currency))),
        "date" => new DateFact(Required(fact.Name), DateOnly.ParseExact(Required(fact.Day), "yyyy-MM-dd", CultureInfo.InvariantCulture)),
        "since" => new SinceFact(Required(fact.Name), DateTimeOffset.ParseExact(Required(fact.Since), "O", CultureInfo.InvariantCulture)),
        "count" => new CountFact(Required(fact.Name), fact.Count ?? throw new FormatException("A count fact without its count.")),
        _ => throw new FormatException($"'{fact.Kind}' is not a fact kind."),
    };

    // A value, a JSON string, is kept as the string; anything else as its JSON text. Raw: LogLine strips and cuts.
    static Dictionary<string, string>? Properties(string? json)
    {
        if (json is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                return null;

            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind is JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.GetRawText();
                properties[property.Name] = value ?? "";
            }

            return properties;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string Cut(string text) =>
        text.Length <= MaxFieldLength ? text : string.Concat(text.AsSpan(0, MaxFieldLength - 1), "…");

    static string Required(string? value) => value ?? throw new FormatException("A required field is missing.");

    static T Present<T>(T? element) where T : class => element ?? throw new FormatException("A null element in a list.");

    // Enum.Parse alone would also take "1"; a column holds names only, so spec R-1's renumbering left it readable.
    static T Name<T>(string? value) where T : struct, Enum =>
        value is not null && Enum.GetNames<T>().Contains(value, StringComparer.Ordinal)
            ? Enum.Parse<T>(value)
            : throw new FormatException($"'{value}' is not a {typeof(T).Name} name.");

    // Spec R-1: Unknown is no check and no group; no check produces it, so a column naming it was never written by one.
    static T KnownName<T>(string? value) where T : struct, Enum =>
        Name<T>(value) is var known && !EqualityComparer<T>.Default.Equals(known, default)
            ? known
            : throw new FormatException($"'{value}' is not a known {typeof(T).Name}.");

    sealed record FindingJson(
        [property: JsonPropertyName("check")] string? Check,
        [property: JsonPropertyName("group")] string? Group,
        [property: JsonPropertyName("transaction_id")] Guid? TransactionId,
        [property: JsonPropertyName("wallet_id")] Guid? WalletId,
        [property: JsonPropertyName("job_id")] Guid? JobId,
        [property: JsonPropertyName("facts")] IReadOnlyList<FactJson?>? Facts);

    sealed record FactJson(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
        [property: JsonPropertyName("amount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Amount = null,
        [property: JsonPropertyName("currency"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency = null,
        [property: JsonPropertyName("day"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Day = null,
        [property: JsonPropertyName("since"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Since = null,
        [property: JsonPropertyName("count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null);

    sealed record LogLineJson(
        [property: JsonPropertyName("logged_at")] string? LoggedAt,
        [property: JsonPropertyName("level")] string? Level,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("exception")] string? Exception,
        [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, string>? Properties);
}
