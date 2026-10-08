namespace Noof.Ledger.Application.Reporting.Summary;

public sealed record SummaryExplanationRequest(string SummaryText);

public interface ISummaryExplainer
{
    // Throws on any failure — ModelCallException or anything else — and never returns blank text.
    Task<string> ExplainAsync(SummaryExplanationRequest request, CancellationToken cancellationToken);
}
