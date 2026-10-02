using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

// What the model is offered. Slug is the stable key the model answers with; NameEn/NameRu are
// renameable display text, which is exactly why they are not the key (decision P1-1).
public sealed record CategoryOption(string Slug, string NameEn, string NameRu, string? ParentSlug);

// A merchant the database already knows. Id is what the model returns when it accepts one.
public sealed record MerchantOption(Guid Id, string DisplayName);

// A wallet the model may pick for a transaction (M3). Aliases are the words the operator uses for
// it in speech ("с налички" for a wallet named "Cash"); IsDefaultForCurrency marks the wallet the
// mapper falls back to when the model names none.
// DefaultForPayment says which payment method this wallet is the default for in its currency, if any: a transfer
// leg the model left without a wallet takes, by side, the card default of its currency for the source and the cash
// default for the destination, each else that currency's default (spec A-27). A slip keeps cash on both legs.
public sealed record WalletOption(
    Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency,
    WalletPaymentDefault? DefaultForPayment = null);

// Today is the local day the message was SENT, never the day the job runs: a message that waited in the
// offline queue overnight must not move a day (D2). For a Correct job specifically, "the message" is the
// correction reply itself, not the original capture (docs/decisions/p2-2-correction-today-anchor.md).
// Wallets is null, not an empty list, when the caller offers none at all - CategorizationSchema and
// CategorizationPrompt both treat null the same as empty (M9), but the distinction stays in the type
// so a future caller can tell "no wallets exist yet" from "I forgot to pass them".
public sealed record CategorizationRequest(
    string RawText,
    DateOnly Today,
    IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<MerchantOption> MerchantHints,
    IReadOnlyList<MerchantOption> AllMerchants,
    CorrectionRequest? Correction = null,
    IReadOnlyList<WalletOption>? Wallets = null);

// The record as it stands and what the person asked to change. The model answers with the complete corrected
// record, which replaces the model-authored lines exactly as a first reading does (D6).
// CurrentLines carries principal lines only: a fee line is written by C# and is never offered back to the model
// as an item. A transfer, the record's charges and a balance statement travel as facts of their own.
public sealed record CorrectionRequest(
    DateOnly CurrentOccurredOn,
    IReadOnlyList<RecordedLine> CurrentLines,
    string Instruction,
    TransactionKind CurrentKind = TransactionKind.Expense,
    TransferView? CurrentTransfer = null,
    IReadOnlyList<ChargeView>? CurrentCharges = null,
    BalanceStatement? CurrentStatement = null);

// One line in the model's own reading of the message. Amount is the number the person meant (1000
// for "штуку"), answered as a JSON number and read straight into decimal by System.Text.Json — no
// parsing step, nothing compares it with the message (decision D1). CurrencyCode is null when the
// message states none.
public sealed record ProposedLineItem(
    string Description,
    decimal Amount,
    string? CurrencyCode,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantName);

// The closed set of strings the model answers "kind" with (M9). Kept as string constants, not an
// enum, because this record crosses the model boundary as JSON before anything maps it - the same
// reason ProposedLineItem.CurrencyCode is a string, not a CurrencyCode, until ProposalMapper resolves it.
public static class ProposedKind
{
    public const string Expense = "expense";
    public const string Income = "income";
    public const string Balance = "balance";
    public const string Transfer = "transfer";
}

public static class ProposedLeg
{
    public const string From = "from";
    public const string To = "to";
}

// A transfer exactly as the operator said it (spec §2). The model copies amounts and never computes: C# adds or
// takes out the fee and converts by the rate. Rate reads "1 BaseCurrency = QuoteAmount QuoteCurrency"; Fee.Leg is
// a ProposedLeg value, and Included says whether the amount given for that leg already contains the fee.
public sealed record ProposedRate(string BaseCurrency, decimal QuoteAmount, string QuoteCurrency);

public sealed record ProposedFee(decimal Amount, string Currency, string Leg, bool Included);

public sealed record ProposedTransfer(
    Guid? FromWalletId, decimal FromAmount, string FromCurrency,
    Guid? ToWalletId, decimal? ToAmount, string ToCurrency,
    ProposedRate? Rate, ProposedFee? Fee);

// Only when the operator said what was actually charged, in whatever currency they said it.
public sealed record ProposedCharge(decimal Amount, string Currency, decimal? FeeAmount, bool FeeIncluded);

// Kind defaults to Expense so every existing positional construction of this record (a plain
// spending answer) keeps meaning exactly what it always meant. WalletId/BalanceAmount/BalanceCurrency
// travel flat, mirroring record_transaction's own wire shape (the "expensive to reverse" note in
// plan-00-header.md) rather than as a nested object the strict schema cannot express as cleanly.
public sealed record CategorizationProposal(
    IReadOnlyList<ProposedLineItem> Items,
    string? OccurredOn = null,
    string Kind = ProposedKind.Expense,
    Guid? WalletId = null,
    decimal? BalanceAmount = null,
    string? BalanceCurrency = null,
    ProposedTransfer? Transfer = null,
    ProposedCharge? Charged = null);

public sealed record ResolvedLineItem(
    string Description,
    Money Amount,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantName);

// WalletId is always a wallet the request offered: the one the model named, or the default wallet of the
// spending's currency, or the default wallet of the configured default currency (M3).
public sealed record MappedProposal(
    IReadOnlyList<ResolvedLineItem> Items,
    DateOnly? OccurredOn,
    TransactionKind Kind = TransactionKind.Expense,
    Guid WalletId = default,
    Money? StatedBalance = null,
    TransferFacts? Transfer = null,
    StatedCharge? Charged = null);

// What ApplyAsync writes for a Transfer: the stored amounts - everything each wallet moved, the fee inside its
// leg (T-12) - and the fee in its leg's currency.
public sealed record TransferFacts(
    Guid FromWalletId, Money From, Guid ToWalletId, Money To,
    Money? Fee, TransferLeg? FeeLeg, ExchangeRate? StatedRate, Guid? VenueMerchantId = null);

// A charge the operator said, in whatever currency they said it, passed through unjudged. Only one in the
// wallet's currency is honoured, but one in any other currency still counts as said, so an older stated charge
// never survives it.
public sealed record StatedCharge(Money Charged, decimal? FeeAmount, bool FeeIncluded);

// A line ready to be written: identity resolved, slug resolved to a real row. Ordinal and
// ReceiptLineId are set only when this line comes from a receipt's own lines (Phase 6, R-2) -
// null for every text/voice capture, which keeps the auto-numbering EfCategorizationStore already
// did for them unchanged.
public sealed record CategorizedLineItem(
    string Description,
    Money Amount,
    Guid CategoryId,
    Guid? MerchantId,
    int? Ordinal = null,
    Guid? ReceiptLineId = null);

// Everything the worker and the echo need about one transaction: the message, where its echo lives, and the
// record exactly as it is stored now. SentOn is the local day the message was sent - "today" for the model (D2).
public sealed record CategorizationSubject(
    Guid TransactionId,
    string RawText,
    long TelegramChatId,
    int? BotMessageId,
    string WalletName,
    TransactionStatus Status,
    DateOnly SentOn,
    DateOnly OccurredOn,
    IReadOnlyList<RecordedLine> Lines,
    CaptureKind CaptureKind = CaptureKind.Text,
    TransactionKind Kind = TransactionKind.Expense,
    CurrencyCode? WalletCurrency = null,
    IReadOnlyList<Money>? WalletBalances = null,
    BalanceStatement? Statement = null,
    Guid? WalletId = null,
    TransferView? Transfer = null,
    IReadOnlyList<ChargeView>? Charges = null,
    RecordFailureReason FailureReason = RecordFailureReason.None,
    SlipFacts? Slip = null);

// The stated amount of a balance-check transaction, and what the app had computed for that wallet
// and currency just before it - history for the echo (M6), never a figure anything reads back as
// the current balance.
public sealed record BalanceStatement(Money Stated, decimal ComputedBefore);

// A Transfer record as stored: the stored amounts (T-12), the fee and its leg, the rate the operator said, and
// each leg's wallet balances in the order IBalanceReadModel.BalanceOfAsync gives them. FromBalanceWithoutThis is the
// source wallet's balance in From's currency at the end of the ledger with this record left out (the wallet_balances
// rule), so a backdated transfer that a later checkpoint absorbs never claims to have taken the wallet below zero.
public sealed record TransferView(
    Guid FromWalletId, string FromWalletName, Money From,
    Guid ToWalletId, string ToWalletName, Money To,
    Money? Fee, TransferLeg? FeeLeg, ExchangeRate? StatedRate, string? VenueName,
    IReadOnlyList<Money> FromBalances, IReadOnlyList<Money> ToBalances,
    decimal? FromBalanceWithoutThis = null);

// One foreign currency of a spending and what it cost the wallet; Terms is the snapshot it was computed with.
public sealed record ChargeView(
    CurrencyCode Currency, decimal ForeignSum, Money Charged, Money Fee, decimal RateUsed, FeeTerms Terms, ChargeSource Source);

// A record read from an exchange-office slip: the office, the slip's number, and what vision read - evidence,
// never the record's money.
public sealed record SlipFacts(string? VenueName, string? SlipNumber, ExtractedExchange Evidence);

// CategoryName is Category.NameEn: the bot speaks English for now (D-D).
public sealed record RecordedLine(
    string Description,
    Money Amount,
    string? CategorySlug,
    string? CategoryName,
    string? MerchantName,
    EntryRole Role = EntryRole.Principal);

// Kind is the job that produced this outcome; TransactionKind is what the record is. Never confuse the two.
public sealed record CategorizationOutcome(
    IReadOnlyList<CategorizedLineItem> Items,
    DateOnly OccurredOn,
    JobKind Kind = JobKind.Categorize,
    string? Instruction = null,
    TransactionKind TransactionKind = TransactionKind.Expense,
    Guid? WalletId = null,
    Money? StatedBalance = null,
    TransferFacts? Transfer = null,
    StatedCharge? Charged = null);

public sealed record CategoryEntry(Guid Id, string Slug, string NameEn, string NameRu, string? ParentSlug);

public sealed record MerchantAliasEntry(string Folded, Guid MerchantId, string DisplayName);
