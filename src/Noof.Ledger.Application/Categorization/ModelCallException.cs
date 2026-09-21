namespace Noof.Ledger.Application.Categorization;

public enum ModelFailureKind
{
    // Retry later: the network, a rate limit, an overloaded API, a malformed answer.
    Transient = 0,

    // Never retry: a bad key, no credit, a request this code will build identically next time.
    Terminal = 1,
}

public sealed class ModelCallException(ModelFailureKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ModelFailureKind Kind { get; } = kind;
}
