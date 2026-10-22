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

	public static bool TryReadSlotTime(object? value, out TimeSpan time)
	{
		if (value is TimeSpan span)
		{
			time = span;
			return span >= TimeSpan.Zero && span < TimeSpan.FromDays(1);
		}
		if (value is DateTime dateTime)
		{
			time = dateTime.TimeOfDay;
			return true;
		}
		string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
		if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time)
			&& time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)) return true;
		if (DateTime.TryParseExact(text, new[] { "h:mm tt", "hh:mm tt", "h:mm:ss tt", "hh:mm:ss tt" },
			CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
		{
			time = parsed.TimeOfDay;
			return true;
		}
		time = default;
		return false;
	}
}
