namespace Noof.Ledger.Persistence;

internal static class ZonedClock
{
    public static DateTime LocalDateTime(DateTimeOffset instant, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime;

    public static DateOnly LocalDate(DateTimeOffset instant, string timeZoneId) =>
        DateOnly.FromDateTime(LocalDateTime(instant, timeZoneId));
}
