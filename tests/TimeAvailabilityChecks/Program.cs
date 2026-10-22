using System.Globalization;
using InfinityHairartsAPI.Services;

// Exercises the production parser without connecting to any database or API.
var cases = new (object? Value, TimeSpan? Expected)[]
{
    (new TimeSpan(19, 0, 0), new TimeSpan(19, 0, 0)),
    (new DateTime(2026, 10, 21, 19, 30, 0), new TimeSpan(19, 30, 0)),
    ("19:00:00", new TimeSpan(19, 0, 0)),
    ("19:30:00.0000000", new TimeSpan(19, 30, 0)),
    ("08:00:00", new TimeSpan(8, 0, 0)),
    ("7:30", new TimeSpan(7, 30, 0)),
    ("07:00 PM", new TimeSpan(19, 0, 0)),
    ("7:30 PM", new TimeSpan(19, 30, 0)),
    ("12:00 AM", TimeSpan.Zero),
    ("12:00 PM", new TimeSpan(12, 0, 0)),
    ("07:30:00 PM", new TimeSpan(19, 30, 0)),
    ("00:00:00", TimeSpan.Zero),
    (null, null), (DBNull.Value, null), ("", null), ("not a time", null),
    ("25:00:00", null), (TimeSpan.FromDays(1), null), (TimeSpan.FromMinutes(-1), null)
};
var checks = 0;
foreach (var culture in new[] { "en-US", "en-IN", "fr-FR" })
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
    foreach (var test in cases)
    {
        bool parsed = CustomerBookingRules.TryReadSlotTime(test.Value, out var time);
        if (parsed != test.Expected.HasValue || (parsed && time != test.Expected))
            throw new InvalidOperationException($"Slot parsing failed for {test.Value} under {culture}.");
        checks++;
    }
    CustomerBookingRules.TryReadSlotTime("19:00:00", out var evening);
    if (evening <= new TimeSpan(14, 0, 0).Add(TimeSpan.FromMinutes(90)))
        throw new InvalidOperationException("An evening SQL time must remain later than the afternoon booking cutoff.");
    checks++;
}
Console.WriteLine($"PASS: {checks} slot-time parsing and cutoff checks; no database or network requests.");
