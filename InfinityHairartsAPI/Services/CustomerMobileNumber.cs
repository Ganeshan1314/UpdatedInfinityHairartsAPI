using System.Linq;

namespace InfinityHairartsAPI.Services;

public static class CustomerMobileNumber
{
	public static bool IsValid(string? value)
	{
		string text = value?.Trim();
		if (text != null && text.Length == 10)
		{
			char c = text[0];
			if (c >= '6' && c <= '9')
			{
				return text.All((char digit) => digit >= '0' && digit <= '9');
			}
		}
		return false;
	}
}
