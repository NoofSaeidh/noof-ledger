namespace Noof.Ledger.Host.Startup;

public static class CaptureTimeZoneGuard
{
    public static TimeZoneInfo Resolve(string configuredId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configuredId);
        }
        catch (Exception exposed) when (exposed is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"Capture:TimeZone '{configuredId}' is not a valid IANA time zone id on this machine.", exposed);
        }
    }
}
