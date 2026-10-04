using Noof.Ledger.Application.Diagnostics.Integrity;

namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

internal interface IIntegrityCheck
{
    IntegrityCheck Check { get; }

    IntegrityGroup Group { get; }

    Task<IReadOnlyList<IntegrityFinding>> FindAsync(IntegrityScope scope, CancellationToken cancellationToken);
}
