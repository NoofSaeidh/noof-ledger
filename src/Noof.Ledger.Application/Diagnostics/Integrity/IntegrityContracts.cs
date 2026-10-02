using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Diagnostics.Integrity;

// The declaration order is the check order every list of findings, page and explanation follows.
public enum IntegrityCheck { PostingsDisagree = 0, FactsMismatchKind = 1, StuckInPipeline = 2, NotApplied = 3 }

public enum IntegrityGroup { Bug = 0, WaitingOnYou = 1 }

public abstract record IntegrityFact(string Name);

public sealed record MoneyFact(string Name, decimal Amount, CurrencyCode Currency) : IntegrityFact(Name);

public sealed record DateFact(string Name, DateOnly Day) : IntegrityFact(Name);

// The instant a wait started, not its length, so a snapshot read days later still ages from the right moment.
public sealed record SinceFact(string Name, DateTimeOffset Since) : IntegrityFact(Name);

public sealed record TextFact(string Name, string Text) : IntegrityFact(Name);

public sealed record CountFact(string Name, int Count) : IntegrityFact(Name);

// WalletId is null while the record is Captured; JobId is set only for a failed job that never applied.
public sealed record IntegrityFinding(
    IntegrityCheck Check, IntegrityGroup Group, Guid? TransactionId, Guid? WalletId, Guid? JobId,
    IReadOnlyList<IntegrityFact> Facts);

public interface IIntegrityChecks
{
    // Check order; within a check, by the record's created_at, then its id.
    Task<IReadOnlyList<IntegrityFinding>> FindAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<IntegrityFinding>> FindForTransactionAsync(Guid transactionId, CancellationToken cancellationToken);
}

public static class IntegrityHealth
{
    public const string CheckName = "Integrity";

    public const string PagePath = "/diagnostics/integrity";
}
