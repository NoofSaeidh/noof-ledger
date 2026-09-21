namespace Noof.Ledger.Host.Startup;

public static class CaptureTimeZoneGuard
{
    public static TimeZoneInfo Resolve(string configuredId)
    {
        TimeZoneInfo resolved;
        try
        {
            resolved = TimeZoneInfo.FindSystemTimeZoneById(configuredId);
        }
        catch (Exception exposed) when (exposed is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw NotAnIanaId(configuredId, exposed);
        }

        // FindSystemTimeZoneById also accepts a Windows id (e.g. "Central Europe Standard Time")
        // on Windows, but Transaction.TimeZoneId is documented and consumed downstream as IANA.
        // TryConvertWindowsIdToIanaId only ever succeeds for a Windows id -- an IANA id passed to
        // it returns false -- which makes it the cheapest available discriminator: if the
        // configured id itself converts, it was never IANA to begin with.
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(configuredId, out _))
            throw NotAnIanaId(configuredId, exposed: null);

        return resolved;
    }

    static InvalidOperationException NotAnIanaId(string configuredId, Exception? exposed) =>
        new($"Capture:TimeZone '{configuredId}' is not a valid IANA time zone id on this machine.", exposed);
}
