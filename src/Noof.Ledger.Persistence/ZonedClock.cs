namespace Noof.Ledger.Persistence;

internal static class ZonedClock
{
    public static DateTime LocalDateTime(DateTimeOffset instant, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime;

    public static DateOnly LocalDate(DateTimeOffset instant, string timeZoneId) =>
        DateOnly.FromDateTime(LocalDateTime(instant, timeZoneId));

    public static DateTimeOffset StartOfDay(DateOnly day, string timeZoneId)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var offset = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(midnight);

        // UTC because Npgsql refuses a non-zero offset for timestamptz.
        return new DateTimeOffset(midnight, offset).ToUniversalTime();
    }
}
