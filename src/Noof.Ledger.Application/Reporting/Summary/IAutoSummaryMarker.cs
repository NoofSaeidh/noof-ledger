namespace Noof.Ledger.Application.Reporting.Summary;

public interface IAutoSummaryMarker
{
    // The first day of the last month sent automatically; null when none was.
    Task<DateOnly?> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(DateOnly firstDayOfMonth, CancellationToken cancellationToken);

    // Spec P-14: when this finished month was first found due but unsettled; null when not recorded for it.
    Task<DateTimeOffset?> GetWaitingSinceAsync(DateOnly firstDayOfMonth, CancellationToken cancellationToken);

    Task SetWaitingSinceAsync(DateOnly firstDayOfMonth, DateTimeOffset since, CancellationToken cancellationToken);
}
