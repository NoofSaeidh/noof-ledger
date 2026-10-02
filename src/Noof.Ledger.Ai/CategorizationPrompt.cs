using System.Globalization;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

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
        You record spending, income, balance statements and transfers from a personal finance
        message so they can be reviewed later. You read one message at a time and answer with what
        it describes, nothing more. The person sees your answer echoed back in their chat and can
        cancel or correct it, so give your best reading of what they meant rather than leaving out
        an amount that is not written in digits.

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
        guessing here would only replace a correct default with a wrong guess. This is for an
        item's currency only: each side of a transfer always has a currency, the one the person
        says or else the named wallet's own, and a fee with no currency said is in its side's.

        The message comes with today's date and weekday in the person's time zone. When the
        message says which day the purchase happened — "вчера", "позавчера", "в пятницу", "15-го"
        — answer with occurred_on: that day as YYYY-MM-DD, counted from today. A weekday means the
        most recent such day before today. When the message names no day, answer occurred_on as
        null: it will be recorded as today.

        Sometimes the message has already been recorded and the person wants it changed — "нет,
        1500", "это было позавчера", "это подарок". Then you are also given the current record and
        their correction. Answer with the complete corrected record: every line, not only the one
        that changed, with the correction applied and everything it does not mention kept as it
        is. Days in a correction are counted from today, as above. The current record names its
        kind; for a transfer it shows each side, its fee and any rate the person stated, and for a
        purchase in another currency what the wallet was charged. A charge already on the record
        stays as it is: answer charged only when the correction states a new one. Each side of a
        transfer shows its amount as the person would have said it: a fee "not included in the
        figure" is answered with included false, and a fee "already taken out of the figure" with
        included true. A new amount the correction states for a side replaces that figure and keeps
        its fee's included as shown, unless the correction says how the fee relates to it.
        A side "worked out by the ledger" is answered
        with to_amount null unless the correction states what arrived. When the current
        record says nothing was recorded, the first reading could not be recorded — read the
        message again and take the correction as the missing piece, such as the amount received in
        an exchange.

        A message may name zero, one or several purchases. Produce one line item per purchase that
        has an amount. If a merchant is named and it matches one of the known merchants you were
        given, set known_merchant_id to that merchant's id. If a merchant is named but matches no
        known merchant, put its name in merchant_name as the person wrote it. If no merchant is
        named, answer both known_merchant_id and merchant_name as null. If a merchant is named and
        you are unsure whether it is already known, you may call list_merchants to check the full
        list before answering.

        Every answer also says what kind of record this is: "expense" for money spent, "income" for
        money received — a salary, a refund, a gift, a loan you were given — "balance" only when
        the person states what a wallet's balance is right now, not describing a transaction at all
        ("на райфе осталось 45 тысяч", "у меня в кошельке 20 евро"), and "transfer" when money
        moves between the person's own wallets. Match a category from the income branch when kind
        is "income", and from every other branch when kind is "expense"; for kind "balance",
        items must be empty — there is nothing to categorise, only a balance to state.

        A cash withdrawal, a top-up, a transfer between the person's own accounts and a currency
        exchange are all kind "transfer", never an expense plus an income. For kind "transfer",
        items stay empty and transfer says what moved: the wallet, amount and currency that left
        (from_wallet_id, from_amount, from_currency) and those that arrived (to_wallet_id,
        to_amount, to_currency). A side's wallet id is null when the person names no wallet for
        it: the ledger takes an unnamed from side out of the card wallet of its currency and puts
        an unnamed to side in the cash wallet of its currency, each else in the default.
        Copy every amount exactly as the person said it and never multiply, add or subtract:
        a stated rate goes into rate — "по 117" for euros changed into dinars is base_currency
        "EUR", quote_amount 117, quote_currency "RSD" — a fee goes into fee, and the ledger works out the
        rest. Leave to_amount null when the person did not say what arrived. transfer is null for
        every other kind.

        A fee is on leg "from" unless the person says the receiving side kept it. Set included
        to true only when the person says the amount they gave for that side already includes the
        fee ("списали 10150 включая комиссию 150"), or when the fee is on leg "to" and
        to_amount is said as what arrived; otherwise false. An amount said as what arrived —
        "получил", "пришло", "на руки" — is what was left after a fee on that side, so it already
        includes the fee unless the person says the fee was taken out of it afterwards. A fee said
        in only one side's currency is on that side
        ("комиссия 150 динар" on euros changed into dinars is leg "to").

        Answer charged only when the person says what was actually taken from the wallet, in the
        wallet's own currency, for a purchase in another currency ("30 долларов с каспи, списали
        15400"): amount and currency as said, fee_amount when they name the commission, and
        fee_included true only when they say the charged amount includes it. Otherwise charged is
        null.

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
        <example>
        Message: "снял 10000 с райфа, комиссия 150"
        Answer with kind "transfer", no items, and transfer: from_wallet_id the wallet aliased
        "райф" if it is among the offered wallets, from_amount 10000, from_currency "RSD",
        to_wallet_id null, to_amount null, to_currency "RSD", rate null, and fee with amount 150,
        currency "RSD", leg "from", included false. The 10000 does not include the fee; the
        ledger adds it. The person names no wallet for the cash, so to_wallet_id stays null.
        </example>
        <example>
        Message: "поменял 100 евро на динары по 117"
        Answer with kind "transfer", no items, and transfer: from_amount 100, from_currency "EUR",
        to_amount null, to_currency "RSD", rate with base_currency "EUR", quote_amount 117 and
        quote_currency "RSD", fee null, both wallet ids null. Do not work out 11700 yourself.
        </example>
        <example>
        Message: "поменял 100 евро, получил 11700 динар, комиссия 100 динар"
        Answer with kind "transfer", no items, and transfer: from_amount 100, from_currency "EUR",
        to_amount 11700, to_currency "RSD", rate null, and fee with amount 100, currency "RSD",
        leg "to", included true, both wallet ids null. "Получил" is what the person was left with
        after the office kept its fee, so 11700 already has the fee out of it.
        </example>
        <example>
        Message: "30 долларов с каспи на книгу, списали 15400"
        Answer with kind "expense" and one item: description "книга", amount 30, currency "USD",
        category_slug the one whose meaning is books or shopping; wallet_id the wallet aliased
        "каспи" if it is among the offered wallets; and charged with amount 15400, currency "KZT",
        fee_amount null, fee_included false.
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
        {RenderCurrentRecord(correction)}

        Correction from the person:
        {correction.Instruction}
        """;

    // A record no reading ever completed (a failed first reading) is still the default Expense with nothing in it;
    // naming that kind would tell the model something the person never said.
    static string RenderCurrentRecord(CorrectionRequest correction)
    {
        if (correction is { CurrentKind: TransactionKind.Expense, CurrentLines.Count: 0, CurrentTransfer: null })
            return "- nothing was recorded";

        List<string> parts = [$"Kind: {KindWord(correction.CurrentKind)}"];
        if (correction.CurrentTransfer is { } transfer)
            parts.AddRange(RenderTransfer(transfer));

        if (correction.CurrentStatement is { } statement)
            parts.Add($"- stated balance: {Amount(statement.Stated)}");

        if (correction.CurrentLines.Count > 0)
            parts.AddRange(correction.CurrentLines.Select(RenderCurrentLine));
        else if (correction is { CurrentTransfer: null, CurrentStatement: null })
            parts.Add("- no line items");

        parts.AddRange((correction.CurrentCharges ?? []).Select(RenderCharge));
        return string.Join('\n', parts);
    }

    static string KindWord(TransactionKind kind) => kind switch
    {
        TransactionKind.Income => ProposedKind.Income,
        TransactionKind.BalanceCheck => ProposedKind.Balance,
        TransactionKind.Transfer => ProposedKind.Transfer,
        _ => ProposedKind.Expense,
    };

    // Each side as the person would have said it (amendments 21, 24): the source as handed over less its fee, the fee
    // beside it "not included in the figure"; the destination as what arrived - the stored amount - its fee "already
    // taken out of the figure", as a person says "получил" (I-1). Answered back that way - included false on the source,
    // true on the destination, a worked-out side null - it settles to exactly the stored amounts. A stated rate keeps
    // its stated direction.
    static IEnumerable<string> RenderTransfer(TransferView transfer)
    {
        var source = transfer is { Fee: { } sourceFee, FeeLeg: TransferLeg.From } ? transfer.From - sourceFee : transfer.From;
        var destinationPrincipal = transfer is { Fee: { } destinationFee, FeeLeg: TransferLeg.To } ? transfer.To + destinationFee : transfer.To;
        var workedOut = WorkedOutByTheLedger(transfer.StatedRate, source, destinationPrincipal) ? ", worked out by the ledger" : "";

        yield return $"- from {transfer.FromWalletName}: {Amount(source)}{FeeNote(transfer, TransferLeg.From)}";
        yield return $"- to {transfer.ToWalletName}: {Amount(transfer.To)}{workedOut}{FeeNote(transfer, TransferLeg.To)}";

        if (transfer.StatedRate is { } rate)
        {
            yield return $"- rate as stated: 1 {rate.Base} = "
                + $"{rate.QuoteAmount.ToString("0.############", CultureInfo.InvariantCulture)} {rate.Quote}";
        }
    }

    // A destination the person need not have said: the source copied in the same currency, or the stated rate applied
    // to the source. Answered back as null it settles to the same figure; answered as a number it would pin a rounded
    // figure the person never gave and outlive a corrected rate.
    static bool WorkedOutByTheLedger(ExchangeRate? rate, Money source, Money destination) =>
        source.Currency == destination.Currency
            ? source == destination
            : rate is { QuoteAmount: > 0m } stated
              && (stated.Base == source.Currency || stated.Quote == source.Currency)
              && stated.Convert(source) == destination;

    static string FeeNote(TransferView transfer, TransferLeg leg) => transfer switch
    {
        { Fee: { } fee, FeeLeg: TransferLeg.From } when leg == TransferLeg.From =>
            $", plus a fee of {Amount(fee)} on this side (not included in the figure)",
        { Fee: { } fee, FeeLeg: TransferLeg.To } when leg == TransferLeg.To =>
            $", after a fee of {Amount(fee)} on this side (already taken out of the figure)",
        _ => "",
    };

    static string RenderCharge(ChargeView charge)
    {
        var fee = charge.Fee.Amount > 0m ? $" plus a fee of {Amount(charge.Fee)}" : "";
        var source = charge.Source == ChargeSource.Stated ? "as the person stated" : "at the wallet's own rate";
        return $"- the {charge.Currency} lines ({Amount(charge.ForeignSum, charge.Currency)}) were charged "
            + $"{Amount(charge.Charged)}{fee} to the wallet, {source}";
    }

    static string RenderCurrentLine(RecordedLine line)
    {
        var text = $"- {line.Description}: {Amount(line.Amount)}, category {line.CategorySlug ?? "none"}";
        return line.MerchantName is { } merchant ? $"{text}, merchant {merchant}" : text;
    }

    static string Amount(Money money) => Amount(money.Amount, money.Currency);

    static string Amount(decimal amount, CurrencyCode currency) =>
        $"{amount.ToString("0.####", CultureInfo.InvariantCulture)} {currency}";

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
