using System;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace InfinityHairartsAPI.Services;

public sealed class CustomerSessionRegistry(IMemoryCache cache)
{
	public string Create(Guid customer)
	{
		string text = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
		cache.Set("customer-auth:" + text, customer, new MemoryCacheEntryOptions
		{
			SlidingExpiration = TimeSpan.FromMinutes(30.0),
			AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1.0)
		});
		return text;
	}

	public bool IsActive(ISession session, Guid customer)
	{
		string text = session.GetString("CustomerAuthToken");
		if (text != null && text.Length == 64 && cache.TryGetValue<Guid>("customer-auth:" + text, out var value))
		{
			return value == customer;
		}
		return false;
	}

	public void Revoke(ISession session)
	{
		string text = session.GetString("CustomerAuthToken");
		if (text != null)
		{
			cache.Remove("customer-auth:" + text);
		}
	}
}
