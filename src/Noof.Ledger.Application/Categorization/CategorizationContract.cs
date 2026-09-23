using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

// What the model is offered. Slug is the stable key the model answers with; NameEn/NameRu are
// renameable display text, which is exactly why they are not the key (decision P1-1).
public sealed record CategoryOption(string Slug, string NameEn, string NameRu, string? ParentSlug);

// A merchant the database already knows. Id is what the model returns when it accepts one.
public sealed record MerchantOption(Guid Id, string DisplayName);

public sealed record CategorizationRequest(
    string RawText,
    IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<MerchantOption> MerchantHints,
    IReadOnlyList<MerchantOption> AllMerchants);

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

public sealed record CategorizationProposal(IReadOnlyList<ProposedLineItem> Items);

public sealed record ResolvedLineItem(
    string Description,
    Money Amount,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantName);

public sealed record MappedProposal(IReadOnlyList<ResolvedLineItem> Items);

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
    IReadOnlyList<RecordedLine> Lines);

// CategoryName is the Russian name: the bot speaks Russian.
public sealed record RecordedLine(
    string Description,
    Money Amount,
    string? CategorySlug,
    string? CategoryName,
    string? MerchantName);

public sealed record CategorizationOutcome(IReadOnlyList<CategorizedLineItem> Items, DateOnly OccurredOn);

public sealed record CategoryEntry(Guid Id, string Slug, string NameEn, string NameRu, string? ParentSlug);

public sealed record MerchantAliasEntry(string Folded, Guid MerchantId, string DisplayName);
