namespace Noof.Ledger.Host.Diagnostics;

// GetType().Name on this instance is always "RedactedException" - .NET gives no way to fake a
// runtime type. "Preserving the type name" instead means the ORIGINAL exception's type name is
// baked into the redacted text itself, so a reader of the log still knows what kind of failure
// this was without ever seeing the value that made it unsafe to log verbatim.
internal sealed class RedactedException : Exception
{
    readonly string redactedToString;

    public RedactedException(Exception original, string redactedMessage, string redactedToString)
        : base($"{original.GetType().Name}: {redactedMessage}")
    {
        this.redactedToString = redactedToString;
    }

    public override string ToString() => redactedToString;
}
