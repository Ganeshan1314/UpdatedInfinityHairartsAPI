using System.Text.RegularExpressions;

namespace InfinityHairartsAPI.Services;

public static class CustomerEmailAddress
{
    // Keep the profile form's emailPattern in sync with these domain/local-part rules.
    private static readonly Regex Pattern = new(
        @"\A(?=.{1,64}@)[a-z0-9!#$%&'*+/=?^_\x60{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_\x60{|}~-]+)*@gmail\.com\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static bool IsValid(string? value)
    {
        var email = value?.Trim() ?? string.Empty;
        return email.Length is > 0 and <= 200 && Pattern.IsMatch(email);
    }
}
