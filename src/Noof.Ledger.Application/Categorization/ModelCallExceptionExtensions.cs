namespace Noof.Ledger.Application.Categorization;

// Marks a Terminal ModelCallException whose cause is the ACCOUNT (a bad key, no credit, a revoked
// permission) rather than this job's specific request - every other job's request would fail
// identically too, because the account is what is broken, not the request. A worker that wants to
// pause claiming new work until the operator fixes it, instead of burning the whole backlog's
// attempt budget within seconds, needs to tell this apart from a genuinely per-job terminal
// failure (a malformed request, a response that fails verification).
//
// This rides Exception.Data - already part of every Exception - rather than a new field on
// ModelCallException or a third ModelFailureKind: both of those shapes are reproduced verbatim in
// the plan's locked contract section, and this carries the signal without touching either.
public static class ModelCallExceptionExtensions
{
    const string AccountLevelKey = "Noof.Ledger.AccountLevelFailure";

    public static ModelCallException AsAccountLevel(this ModelCallException exception)
    {
        exception.Data[AccountLevelKey] = true;
        return exception;
    }

    public static bool IsAccountLevel(this ModelCallException exception) =>
        exception.Data.Contains(AccountLevelKey);
}
