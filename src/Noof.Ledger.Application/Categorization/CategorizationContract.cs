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

// One line the model proposes. Nothing here is trusted: AmountQuote is a claim about the raw
// text, CategorySlug is a claim about the offered list, and MerchantQuote is a claim about a
// name that appears in the message. ProposalVerification turns claims into values.
public sealed record ProposedLineItem(
    string Description,
    string AmountQuote,
    string CurrencyCode,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantQuote);

public sealed record CategorizationProposal(IReadOnlyList<ProposedLineItem> Items);

// A line that survived verification. Amount is a Money built by C# from text C# re-read.
public sealed record ResolvedLineItem(
    string Description,
    Money Amount,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantText);

// A line ready to be written: identity resolved, slug resolved to a real row.
public sealed record CategorizedLineItem(
    string Description,
    Money Amount,
    Guid CategoryId,
    Guid? MerchantId);

// Everything the worker needs about the transaction it claimed, in one round trip.
public sealed record CategorizationSubject(
    Guid TransactionId,
    string RawText,
    long TelegramChatId,
    int? BotMessageId,
    string WalletName);

public sealed record CategoryEntry(Guid Id, string Slug, string NameEn, string NameRu, string? ParentSlug);

public sealed record MerchantAliasEntry(string Folded, Guid MerchantId, string DisplayName);
