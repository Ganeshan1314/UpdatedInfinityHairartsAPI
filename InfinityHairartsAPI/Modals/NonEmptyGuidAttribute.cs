using System;
using System.ComponentModel.DataAnnotations;

namespace InfinityHairartsAPI.Modals;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class NonEmptyGuidAttribute : ValidationAttribute
{
	public NonEmptyGuidAttribute()
		: base("Select a valid {0}.")
	{
	}

	public override bool IsValid(object? value)
	{
		if (value != null)
		{
			if (value is Guid guid)
			{
				return guid != Guid.Empty;
			}
			return false;
		}
		return true;
	}
}
