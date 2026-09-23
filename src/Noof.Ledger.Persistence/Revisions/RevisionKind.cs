namespace Noof.Ledger.Persistence.Revisions;

internal enum RevisionKind
{
    Initial = 0,
    Correction = 1,
    Edit = 2,
    Cancel = 3,
    Restore = 4,
}
