using System;
using System.Globalization;

namespace InfinityHairartsAPI.Services;

public static class CustomerBookingRules
{
	private static readonly TimeZoneInfo SalonTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

	public static DateTime Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SalonTimeZone).DateTime;

	public static DateTime Today => Now.Date;

	public static bool IsAvailableDate(DateTime date, DateTime today)
	{
		if (date.Date >= today.Date && date.Date <= today.Date.AddDays(5.0))
		{
			return date.DayOfWeek != DayOfWeek.Friday;
		}
		return false;
	}

	public static bool TryReadDate(string? value, out DateTime date)
	{
		return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
	}
}
