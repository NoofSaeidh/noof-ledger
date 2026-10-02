namespace Noof.Ledger.Application.Diagnostics.Integrity;

public interface IFindingText
{
    string Title(IntegrityCheck check);

    string Description(IntegrityCheck check);

    string GroupLabel(IntegrityGroup group);

    // A SinceFact ages against asOf, so a snapshot reads as of when it was taken.
    string FormatValue(IntegrityFact fact, DateTimeOffset asOf);

    string Format(IntegrityFact fact, DateTimeOffset asOf);

    string FindingsBlock(IReadOnlyList<IntegrityFinding> findings, DateTimeOffset asOf);

    ExplanationRequest ForFinding(IntegrityFinding finding, DateTimeOffset asOf);

    // Null findings were not collected, and the model is told so rather than shown an empty list.
    ExplanationRequest ForReport(
        string? operatorText, string? recordSummary, IReadOnlyList<IntegrityFinding>? findings, DateTimeOffset asOf);
}
