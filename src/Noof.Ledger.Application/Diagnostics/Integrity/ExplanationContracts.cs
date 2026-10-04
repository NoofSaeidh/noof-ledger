namespace Noof.Ledger.Application.Diagnostics.Integrity;

// Composed by IFindingText: every figure in it is already formatted by C#.
public sealed record ExplanationRequest(string Findings, string? OperatorText = null, string? RecordSummary = null);

// LooksLikeBug only shows or hides the offer to file a report; nothing it says changes a finding or a health level.
public sealed record Explanation(string Text, bool LooksLikeBug);

public interface IFindingExplainer
{
    // Throws on any failure - ModelCallException or anything else - and never returns a blank Text.
    Task<Explanation> ExplainAsync(ExplanationRequest request, CancellationToken cancellationToken);
}
