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
        one message at a time and answer with the spending it describes, nothing more.

        Each category you are offered has a slug, an English name, a Russian name, and may have a
        parent category. Use the names to understand what each slug means — everyday food and
        drink, transport, household bills, and so on — so a line item lands under the slug whose
        meaning actually matches it. You do not choose the set of allowed slugs; your answer only
        accepts one of the slugs you were given, so pick by meaning and let the schema reject
        anything else.

        For every amount: copy it out of the message character for character, exactly as written.
        Never convert digits, never add or remove separators, never add a currency symbol, and
        never compute, round or sum anything — even when two lines obviously add up to a total the
        message also states. If the message does not clearly state an amount for a line, do not produce that line at all — an unreadable amount is not a zero and is not a guess.

        Report a currency only when the message actually states one. If the message names no
        currency at all, leave currency out of your answer rather than choosing one — a missing
        currency is filled in later from a configured default, so guessing here would only replace
        a correct default with a wrong guess.

        A message may name zero, one or several purchases. Produce one line item per purchase that
        has a stated amount. If a merchant is named and it matches one of the known merchants you
        were given, set known_merchant_id to that merchant's id instead of merchant_quote. If a
        merchant is named but matches no known merchant, copy its name into merchant_quote character
        for character, the same rule as amounts. If no merchant is named, leave both empty. If a
        merchant is named and you are unsure whether it is already known, you may call
        list_merchants to check the full list before answering.

        <examples>
        <example>
        Message: "кофе 250 рсд"
        Answer with one item: description "coffee", amount_quote "250", currency "RSD",
        category_slug the one whose meaning is everyday food and drink, no merchant.
        </example>
        <example>
        Message: "продукты 3400 рсд молоко хлеб сыр"
        Answer with one item: description "groceries: milk, bread, cheese", amount_quote "3400",
        currency "RSD", category_slug the one whose meaning is groceries, no merchant. There is one
        stated amount, so there is one line, even though three goods are named.
        </example>
        <example>
        Message: "taxi 1200"
        Answer with one item: description "taxi", amount_quote "1200", category_slug the one
        whose meaning is transport, no merchant. The message names no currency, so currency is
        left out of the answer entirely — do not guess RSD, EUR or anything else.
        </example>
        <example>
        Message: "Lidl 45.30 eur продукты, потом кофе 2.50 eur"
        Answer with two items. First: description "groceries", amount_quote "45.30", currency
        "EUR", category_slug the one whose meaning is groceries, merchant_quote "Lidl" (or
        known_merchant_id instead, if Lidl is already a known merchant). Second: description
        "coffee", amount_quote "2.50", currency "EUR", category_slug the one whose meaning is
        everyday food and drink, no merchant. Two purchases with two stated amounts make two lines.
        </example>
        <example>
        Message: "заняла у Маши 5000 рсд"
        Answer with no items at all. The message states an amount but describes a loan received,
        not a purchase — there is nothing here to categorise as spending.
        </example>
        </examples>
        """;

    public static string RenderCategories(IReadOnlyList<CategoryOption> categories) =>
        string.Join('\n', categories.Select(RenderCategory));

    public static string RenderMerchantHints(IReadOnlyList<MerchantOption> merchantHints) =>
        merchantHints.Count == 0
            ? "No known merchants are offered for this message."
            : string.Join('\n', merchantHints.Select(m => $"- {m.Id}: {m.DisplayName}"));

    public static string BuildUserTurn(
        string rawText, IReadOnlyList<CategoryOption> categories, IReadOnlyList<MerchantOption> merchantHints) =>
        $"""
        Message:
        {rawText}

        Categories:
        {RenderCategories(categories)}

        Known merchants:
        {RenderMerchantHints(merchantHints)}
        """;

    static string RenderCategory(CategoryOption category) =>
        category.ParentSlug is null
            ? $"- {category.Slug}: {category.NameEn} / {category.NameRu}"
            : $"- {category.Slug} (under {category.ParentSlug}): {category.NameEn} / {category.NameRu}";
}
