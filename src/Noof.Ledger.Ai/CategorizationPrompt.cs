using System.Globalization;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai;

internal static class CategorizationPrompt
{
    // A role sentence "focuses Claude's behavior" — the categories, the hints and the message
    // itself are variable input and belong in the user turn built by BuildUserTurn, never in this
    // constant. Under Microsoft.Extensions.AI this string becomes ChatOptions.Instructions, which
    // the captured request body confirms lands as the request's "system" field — the same field
    // the raw SDK's MessageCreateParams.System would have used, so this text needed no rewriting
    // for the switch away from strict tool use.
    //
    // The closed set of category_slug values is enforced by CategorizationSchema's enum via the
    // response format, which is Anthropic's recommended mechanism for a closed label set. This
    // prompt exists to explain what each category MEANS so a line item lands under the right one;
    // it deliberately never repeats "choose only from this list" in prose, since a live slug could
    // not even appear here — System is a compile-time const, so it cannot embed data from the
    // current request.
    //
    // Messages arrive in Russian and English, sometimes both in one message. Anthropic publishes no
    // multilingual-prompting guidance as of this writing, so no technique is invented for it beyond
    // the bilingual examples below — that is the whole strategy: show, not instruct.
    public const string System = """
        You record spending from a personal expense message so it can be reviewed later. You read
        one message at a time and answer with the spending it describes, nothing more. The person
        sees your answer echoed back in their chat and can cancel or correct it, so give your best
        reading of what they meant rather than leaving out an amount that is not written in digits.

        Each category you are offered has a slug, an English name, a Russian name, and may have a
        parent category. Use the names to understand what each slug means — everyday food and
        drink, transport, household bills, and so on — so a line item lands under the slug whose
        meaning actually matches it. You do not choose the set of allowed slugs; your answer only
        accepts one of the slugs you were given, so pick by meaning and let the schema reject
        anything else.

        For every amount, answer with the number the person meant — 1000, 45.3, not a word or a
        quoted string. People write amounts in words, slang and speech-recognised text: "штуку" or
        "штука" is 1000, "пятихатка" is 500, "полтос" is 50, "двести пятьдесят" is 250, "1,5к" is
        1500, "1 500" is 1500. Never add lines up into a total the message did not ask for. If a
        line has no amount at all, do not produce that line.

        Report a currency only when the message actually states one — "евро", "eur", "€", "рсд",
        "динар", "рублей". If the message names no currency at all, answer currency as null rather
        than choosing one — a missing currency is filled in later from a configured default, so
        guessing here would only replace a correct default with a wrong guess.

        The message comes with today's date and weekday in the person's time zone. When the
        message says which day the purchase happened — "вчера", "позавчера", "в пятницу", "15-го"
        — answer with occurred_on: that day as YYYY-MM-DD, counted from today. A weekday means the
        most recent such day before today. When the message names no day, answer occurred_on as
        null: it will be recorded as today.

        A message may name zero, one or several purchases. Produce one line item per purchase that
        has an amount. If a merchant is named and it matches one of the known merchants you were
        given, set known_merchant_id to that merchant's id. If a merchant is named but matches no
        known merchant, put its name in merchant_name as the person wrote it. If no merchant is
        named, answer both known_merchant_id and merchant_name as null. If a merchant is named and
        you are unsure whether it is already known, you may call list_merchants to check the full
        list before answering.

        <examples>
        <example>
        Message: "кофе 250 рсд"
        Answer with one item: description "кофе", amount 250, currency "RSD", category_slug the
        one whose meaning is everyday food and drink, no merchant.
        </example>
        <example>
        Message: "купил вчера штуку евро на продукты"
        Answer with one item: description "продукты", amount 1000, currency "EUR", category_slug
        the one whose meaning is groceries, no merchant, and occurred_on the day before today.
        "Штуку" is how people say one thousand; the message has no digits and does not need any.
        </example>
        <example>
        Message: "такси двести пятьдесят"
        Answer with one item: description "такси", amount 250, category_slug the one whose
        meaning is transport, no merchant. The message names no currency, so currency is null — do
        not guess RSD, EUR or anything else.
        </example>
        <example>
        Message: "Lidl 45,30 eur продукты, потом кофе 2.50 eur"
        Answer with two items. First: description "продукты", amount 45.3, currency "EUR",
        category_slug the one whose meaning is groceries, merchant_name "Lidl" (or
        known_merchant_id instead, if Lidl is already a known merchant). Second: description
        "кофе", amount 2.5, currency "EUR", category_slug the one whose meaning is everyday food
        and drink, no merchant. The comma in "45,30" is a decimal separator: the answer is the
        number 45.3, not a string.
        </example>
        <example>
        Message: "заняла у Маши 5000 рсд"
        Answer with no items at all. The message states an amount but describes a loan received,
        not a purchase — there is nothing here to record as spending.
        </example>
        </examples>
        """;

    public static string RenderCategories(IReadOnlyList<CategoryOption> categories) =>
        string.Join('\n', categories.Select(RenderCategory));

    public static string RenderMerchantHints(IReadOnlyList<MerchantOption> merchantHints) =>
        merchantHints.Count == 0
            ? "No known merchants are offered for this message."
            : string.Join('\n', merchantHints.Select(m => $"- {m.Id}: {m.DisplayName}"));

    public static string BuildUserTurn(CategorizationRequest request) =>
        $"""
        Today: {request.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({request.Today.DayOfWeek})

        Message:
        {request.RawText}

        Categories:
        {RenderCategories(request.Categories)}

        Known merchants:
        {RenderMerchantHints(request.MerchantHints)}
        """;

    static string RenderCategory(CategoryOption category) =>
        category.ParentSlug is null
            ? $"- {category.Slug}: {category.NameEn} / {category.NameRu}"
            : $"- {category.Slug} (under {category.ParentSlug}): {category.NameEn} / {category.NameRu}";
}
