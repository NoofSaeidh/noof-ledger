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
public sealed record WalletOption(Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency);

// Today is the local day the message was SENT, never the day the job runs: a message that waited in the
// offline queue overnight must not move a day (D2). For a Correct job specifically, "the message" is the
// correction reply itself, not the original capture (docs/OPEN-QUESTIONS.md P2-2).
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
public sealed record CorrectionRequest(DateOnly CurrentOccurredOn, IReadOnlyList<RecordedLine> CurrentLines, string Instruction);

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
}

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
    string? BalanceCurrency = null);

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
    Money? StatedBalance = null);

// A line ready to be written: identity resolved, slug resolved to a real row.
public sealed record CategorizedLineItem(
    string Description,
    Money Amount,
    Guid CategoryId,
    Guid? MerchantId);

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
    Guid? WalletId = null);

// The stated amount of a balance-check transaction, and what the app had computed for that wallet
// and currency just before it - history for the echo (M6), never a figure anything reads back as
// the current balance.
public sealed record BalanceStatement(Money Stated, decimal ComputedBefore);

// CategoryName is Category.NameEn: the bot speaks English for now (D-D).
public sealed record RecordedLine(
    string Description,
    Money Amount,
    string? CategorySlug,
    string? CategoryName,
    string? MerchantName);

// Kind is the job that produced this outcome; TransactionKind is what the record is. Never confuse the two.
public sealed record CategorizationOutcome(
    IReadOnlyList<CategorizedLineItem> Items,
    DateOnly OccurredOn,
    JobKind Kind = JobKind.Categorize,
    string? Instruction = null,
    TransactionKind TransactionKind = TransactionKind.Expense,
    Guid? WalletId = null,
    Money? StatedBalance = null);

public sealed record CategoryEntry(Guid Id, string Slug, string NameEn, string NameRu, string? ParentSlug);

public sealed record MerchantAliasEntry(string Folded, Guid MerchantId, string DisplayName);
