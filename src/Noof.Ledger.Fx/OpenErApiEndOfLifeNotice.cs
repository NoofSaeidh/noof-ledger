namespace Noof.Ledger.Fx;

// A singleton: the rate source is scoped, and the endpoint announcing its end is worth one Warning per process, not one
// per fetch.
internal sealed class OpenErApiEndOfLifeNotice
{
    int claimed;

    public bool TryClaim() => Interlocked.Exchange(ref claimed, 1) == 0;
}
