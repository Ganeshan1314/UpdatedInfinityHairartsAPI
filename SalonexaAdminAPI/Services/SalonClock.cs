namespace SalonexaAdminAPI.Services;

/// <summary>Salon-local time. Salons operate on India Standard Time.</summary>
public static class SalonClock
{
    private static readonly TimeZoneInfo SalonTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    public static DateTime Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SalonTimeZone).DateTime;

    public static DateTime Today => Now.Date;
}
