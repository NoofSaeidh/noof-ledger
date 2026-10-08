namespace Noof.Ledger.Application.Diagnostics.BugReports;

public interface IBugReportMarkdown
{
    string Render(IReadOnlyList<BugReportDocument> reports, DateTimeOffset generatedAt);
}
