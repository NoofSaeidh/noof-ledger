namespace Noof.Ledger.Application.Secrets;

public readonly record struct ProbeResult(bool Ok, string Message);

// A page may hold one of these and learn whether a stored secret works, without any method in
// its reach that returns a plaintext secret. SecretsPageSourceTests enforces that the page never
// contains the text "GetAsync(" — ProbeAsync is how the Test button stays inside that rule.
public interface ISecretProbe
{
    string SecretKey { get; }

    Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken);
}
