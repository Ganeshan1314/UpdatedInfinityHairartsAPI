using System;
using Microsoft.AspNetCore.Http;

namespace InfinityHairartsAPI.Services;

public static class CustomerSession
{
	private static readonly string[] Keys = new string[6] { "CustomerID", "CustomerVerified", "CustomerAuthToken", "SeatBookingDetailsID", "UniqueBillID", "CustomerBillID" };

	public static void Clear(ISession session)
	{
		string[] keys = Keys;
		foreach (string key in keys)
		{
			session.Remove(key);
		}
	}

	public static void SignIn(ISession session, Guid customerID)
	{
		if (!Guid.TryParse(session.GetString("CustomerID"), out var result) || result != customerID)
		{
			Clear(session);
		}
		session.SetString("CustomerID", customerID.ToString());
	}
}
