using System.Globalization;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai;

internal static class CategorizationPrompt
{
    // A role sentence focuses the model's behavior — the categories, the hints and the message
    // itself are variable input and belong in the user turn built by BuildUserTurn, never in this
    // constant. Under Microsoft.Extensions.AI this string becomes ChatOptions.Instructions, sent
    // as the request's system turn.
    //
    // The closed set of category_slug values is enforced by CategorizationSchema's enum in the
    // record_transaction tool's strict schema, not by this prompt. This prompt exists to explain what
    // each category MEANS so a line item lands under the right one; it deliberately never repeats
    // "choose only from this list" in prose, since a live slug could not even appear here — System
    // is a compile-time const, so it cannot embed data from the current request.
    //
    // Messages arrive in Russian and English, sometimes both in one message. No specific
    // multilingual-prompting technique is invented for it beyond the bilingual examples below —
    // that is the whole strategy: show, not instruct.
    public const string System = """
        You record spending, income and balance statements from a personal finance message so they
        can be reviewed later. You read one message at a time and answer with what it describes,
        nothing more. The person sees your answer echoed back in their chat and can cancel or
        correct it, so give your best reading of what they meant rather than leaving out an amount
        that is not written in digits.

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

        Sometimes the message has already been recorded and the person wants it changed — "нет,
        1500", "это было позавчера", "это подарок". Then you are also given the current record and
        their correction. Answer with the complete corrected record: every line, not only the one
        that changed, with the correction applied and everything it does not mention kept as it
        is. Days in a correction are counted from today, as above.

        A message may name zero, one or several purchases. Produce one line item per purchase that
        has an amount. If a merchant is named and it matches one of the known merchants you were
        given, set known_merchant_id to that merchant's id. If a merchant is named but matches no
        known merchant, put its name in merchant_name as the person wrote it. If no merchant is
        named, answer both known_merchant_id and merchant_name as null. If a merchant is named and
        you are unsure whether it is already known, you may call list_merchants to check the full
        list before answering.

        Every answer also says what kind of record this is: "expense" for money spent, "income" for
        money received — a salary, a refund, a gift, a loan you were given — and "balance" only
        when the person states what a wallet's balance is right now, not describing a transaction
        at all ("на райфе осталось 45 тысяч", "у меня в кошельке 20 евро"). Match a category from
        the income branch when kind is "income", and from every other branch when kind is
        "expense"; for kind "balance", items must be empty — there is nothing to categorise, only a
        balance to state.

        You may be offered a list of wallets, each with an id, a name, a currency, and sometimes
        the words the person uses for it. When the message names a wallet — by its name or by one
        of those words, such as "с налички" for a wallet called "Cash" — answer wallet_id with that
        wallet's id. When the message names no wallet, or none of the offered wallets fits, answer
        wallet_id as null: the ledger picks the default wallet for the spending's currency on its
        own.

        For kind "balance", also answer balance_amount with the number the person states as the
        wallet's current balance, and balance_currency with the currency they name, or null when
        they name none — the wallet's own currency is used then. balance_amount and
        balance_currency stay null for every other kind.

        <examples>
        <example>
        Message: "кофе 250 рсд"
        Answer with kind "expense" and one item: description "кофе", amount 250, currency "RSD",
        category_slug the one whose meaning is everyday food and drink, no merchant.
        </example>
        <example>
        Message: "купил вчера штуку евро на продукты"
        Answer with kind "expense" and one item: description "продукты", amount 1000, currency
        "EUR", category_slug the one whose meaning is groceries, no merchant, and occurred_on the
        day before today. "Штуку" is how people say one thousand; the message has no digits and
        does not need any.
        </example>
        <example>
        Message: "такси двести пятьдесят"
        Answer with kind "expense" and one item: description "такси", amount 250, category_slug
        the one whose meaning is transport, no merchant. The message names no currency, so currency
        is null — do not guess RSD, EUR or anything else.
        </example>
        <example>
        Message: "Lidl 45,30 eur продукты, потом кофе 2.50 eur"
        Answer with kind "expense" and two items. First: description "продукты", amount 45.3,
        currency "EUR", category_slug the one whose meaning is groceries, merchant_name "Lidl" (or
        known_merchant_id instead, if Lidl is already a known merchant). Second: description
        "кофе", amount 2.5, currency "EUR", category_slug the one whose meaning is everyday food
        and drink, no merchant. The comma in "45,30" is a decimal separator: the answer is the
        number 45.3, not a string.
        </example>
        <example>
        Message: "заняла у Маши 5000 рсд"
        Answer with kind "income" and one item: description "заняла у Маши", amount 5000,
        currency "RSD", category_slug the one whose meaning is other income, no merchant. A loan
        received is money the person now has, not a purchase, but it still belongs in the wallet
        the same way a salary would — record it as an item under "income", not as nothing at all.
        </example>
        <example>
        Message: "пришла зарплата 2000 евро на Wise"
        Answer with kind "income" and one item: description "зарплата", amount 2000, currency
        "EUR", category_slug the one whose meaning is salary income, no merchant. If a wallet named
        "Wise" (or aliased to it) is among the offered wallets, set wallet_id to its id; otherwise
        leave it null.
        </example>
        <example>
        Message: "на райфе 45 тысяч"
        Answer with kind "balance", no items at all, balance_amount 45000, balance_currency null —
        the message names no currency, so the wallet's own currency applies. If a wallet aliased
        "райф" is among the offered wallets, set wallet_id to its id.
        </example>
        </examples>
        """;

    public static string RenderCategories(IReadOnlyList<CategoryOption> categories) =>
        string.Join('\n', categories.Select(RenderCategory));

    public static string RenderMerchantHints(IReadOnlyList<MerchantOption> merchantHints) =>
        merchantHints.Count == 0
            ? "No known merchants are offered for this message."
            : string.Join('\n', merchantHints.Select(m => $"- {m.Id}: {m.DisplayName}"));

    public static string RenderWallets(IReadOnlyList<WalletOption> wallets) =>
        wallets.Count == 0
            ? "No wallets are offered for this message; wallet_id must be null."
            : string.Join('\n', wallets.Select(RenderWallet));

    public static string BuildUserTurn(CategorizationRequest request)
    {
        var turn = $"""
            Today: {request.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({request.Today.DayOfWeek})

            Message:
            {request.RawText}

            Categories:
            {RenderCategories(request.Categories)}

            Known merchants:
            {RenderMerchantHints(request.MerchantHints)}

            Wallets:
            {RenderWallets(request.Wallets ?? [])}
            """;

        return request.Correction is { } correction ? $"{turn}\n\n{RenderCorrection(correction)}" : turn;
    }

    static string RenderCorrection(CorrectionRequest correction) =>
        $"""
        Current record (dated {correction.CurrentOccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}):
        {RenderCurrentLines(correction.CurrentLines)}

        Correction from the person:
        {correction.Instruction}
        """;

    static string RenderCurrentLines(IReadOnlyList<RecordedLine> lines) =>
        lines.Count == 0
            ? "- nothing was recorded"
            : string.Join('\n', lines.Select(RenderCurrentLine));

    static string RenderCurrentLine(RecordedLine line)
    {
        var text = $"- {line.Description}: {line.Amount.Amount.ToString("0.####", CultureInfo.InvariantCulture)} "
            + $"{line.Amount.Currency}, category {line.CategorySlug ?? "none"}";
        return line.MerchantName is { } merchant ? $"{text}, merchant {merchant}" : text;
    }

    static string RenderCategory(CategoryOption category) =>
        category.ParentSlug is null
            ? $"- {category.Slug}: {category.NameEn} / {category.NameRu}"
            : $"- {category.Slug} (under {category.ParentSlug}): {category.NameEn} / {category.NameRu}";

    static string RenderWallet(WalletOption wallet)
    {
        var aliases = wallet.Aliases.Count == 0 ? "" : $", also called {string.Join(", ", wallet.Aliases)}";
        var marker = wallet.IsDefaultForCurrency ? $", the default wallet for {wallet.Currency}" : "";
        return $"- {wallet.Id}: {wallet.Name} ({wallet.Currency}){aliases}{marker}";
    }
}
