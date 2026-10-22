using System;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Dapper;
using InfinityHairartsAPI.Modals;
using Microsoft.Extensions.Configuration;

namespace InfinityHairartsAPI.Services;

public sealed class CustomerReservationStore(IConfiguration configuration)
{
	private sealed class Booking
	{
		public Guid SeatBookingDetailsID { get; set; }

		public DateTime BookingDate { get; set; }

		public Guid? SalonMasterID { get; set; }

		public int SeatCount { get; set; }

		public long ReservationVersion { get; set; }

		public bool Confirmed { get; set; }
	}

	private const string LockResource = "Salonexa.TimeReservations.v1";

	private SqlConnection Open()
	{
		SqlConnection sqlConnection = new SqlConnection(configuration.GetConnectionString("dbconnection"));
		sqlConnection.Open();
		sqlConnection.Execute("SET XACT_ABORT ON");
		return sqlConnection;
	}

	private static void Lock(SqlConnection db, SqlTransaction tx, bool write)
	{
		int num = db.ExecuteScalar<int>("DECLARE @result int;\nEXEC @result=sys.sp_getapplock @Resource=@resource, @LockMode=@mode,\n    @LockOwner='Transaction', @LockTimeout=5000;\nSELECT @result;", new
		{
			resource = "Salonexa.TimeReservations.v1",
			mode = (write ? "Exclusive" : "Shared")
		}, tx);
		if (num < 0)
		{
			throw new InvalidOperationException("Reservation lock unavailable.");
		}
	}

	private static Booking? ReadBooking(SqlConnection db, SqlTransaction tx, Guid customer)
	{
		return db.QueryFirstOrDefault<Booking>("SELECT TOP(1) s.SeatBookingDetailsID,s.BookingDate,s.SalonMasterID,s.SeatCount,s.ReservationVersion,\n    CONVERT(bit,CASE WHEN s.BookingStatusMasterID=2 OR EXISTS\n        (SELECT 1 FROM dbo.HairCut_Item_Bill_Master b WHERE b.SeatBookingDetailsID=s.SeatBookingDetailsID\n         AND ISNULL(b.IsDeleted,0)=0) THEN 1 ELSE 0 END) Confirmed\nFROM dbo.SeatBookingDetails s WITH(HOLDLOCK)\nWHERE s.CustomerID=@customer ORDER BY s.CreatedOn DESC,s.SeatBookingDetailsID;", new { customer }, tx);
	}

	private static ReservationDraft? ReadDraft(SqlConnection db, SqlTransaction tx, Guid customer)
	{
		Booking booking = ReadBooking(db, tx, customer);
		if (booking == null)
		{
			return null;
		}
		ReservedTime[] selections = db.Query<ReservedTime>("SELECT c.TimeAllocationID Id,CONVERT(varchar(8),t.FullTiming,108) Time\nFROM dbo.CustomerTimeSelection c JOIN dbo.TimeAllocation t ON t.TimeAllocationID=c.TimeAllocationID\nWHERE c.CustomerID=@customer AND c.SeatBookingDetailsID=@id\n  AND (@confirmed=1 OR c.CreatedOn>=DATEADD(MINUTE,-10,GETDATE()))\nORDER BY t.FullTiming,c.TimeAllocationID;", new
		{
			customer = customer,
			id = booking.SeatBookingDetailsID,
			confirmed = booking.Confirmed
		}, tx).ToArray();
		return new ReservationDraft(booking.SeatBookingDetailsID, booking.BookingDate.ToString("yyyy-MM-dd"), booking.SalonMasterID, booking.ReservationVersion, booking.Confirmed, selections);
	}

	public ReservationDraft? GetDraft(Guid customer)
	{
		using SqlConnection sqlConnection = Open();
		using SqlTransaction sqlTransaction = sqlConnection.BeginTransaction(IsolationLevel.Serializable);
		Lock(sqlConnection, sqlTransaction, write: false);
		ReservationDraft result = ReadDraft(sqlConnection, sqlTransaction, customer);
		sqlTransaction.Commit();
		return result;
	}

	private static bool Conflicts(SqlConnection db, SqlTransaction tx, ReservationDraft draft, Guid slot)
	{
		return db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.CustomerTimeSelection c\nJOIN dbo.SeatBookingDetails s ON s.SeatBookingDetailsID=c.SeatBookingDetailsID\nWHERE c.TimeAllocationID=@slot AND s.BookingDate=@date AND s.SeatBookingDetailsID<>@id\n  AND (s.BookingStatusMasterID=2 OR c.CreatedOn>=DATEADD(MINUTE,-10,GETDATE()) OR EXISTS\n    (SELECT 1 FROM dbo.HairCut_Item_Bill_Master b WHERE b.SeatBookingDetailsID=s.SeatBookingDetailsID\n     AND ISNULL(b.IsDeleted,0)=0));", new
		{
			slot = slot,
			date = draft.BookingDate,
			id = draft.SeatBookingDetailsID
		}, tx) > 0;
	}

	public ReservationReply Change(Guid customer, AppoinmentModal.insertCustomerTimeSelection request, bool remove)
	{
		SqlConnection db = Open();
		SqlTransaction tx;
		try
		{
			tx = db.BeginTransaction(IsolationLevel.Serializable);
			try
			{
				Lock(db, tx, write: true);
				ReservationDraft reservationDraft = ReadDraft(db, tx, customer);
				if ((object)reservationDraft == null)
				{
					return Finish("Booking Required");
				}
				if (request.SeatBookingDetailsID.HasValue)
				{
					Guid? seatBookingDetailsID = request.SeatBookingDetailsID;
					Guid seatBookingDetailsID2 = reservationDraft.SeatBookingDetailsID;
					if (!seatBookingDetailsID.HasValue || seatBookingDetailsID.GetValueOrDefault() != seatBookingDetailsID2 || request.BookingDate != reservationDraft.BookingDate || request.SalonMasterID != reservationDraft.SalonMasterID)
					{
						return Finish("Selection Changed");
					}
				}
				if (reservationDraft.Confirmed)
				{
					return Finish("Booking Confirmed");
				}
				if (request.ExpectedVersion.HasValue && request.ExpectedVersion != reservationDraft.Version)
				{
					bool flag = (remove ? reservationDraft.Selections.All((ReservedTime s) => s.Id != request.TimeAllocationID) : (reservationDraft.Selections.Count == 1 && reservationDraft.Selections[0].Id == request.TimeAllocationID));
					return Finish(flag ? "Success" : "Selection Changed");
				}
				if (!CustomerBookingRules.TryReadDate(reservationDraft.BookingDate, out var date) || !CustomerBookingRules.IsAvailableDate(date, CustomerBookingRules.Today))
				{
					return Finish("Invalid Date");
				}
				Booking booking = ReadBooking(db, tx, customer);
				if (booking.SeatCount != 1)
				{
					return Finish("Invalid Seat Count");
				}
				db.Execute("UPDATE dbo.SeatBookingDetails SET ReservationVersion=ReservationVersion+1 WHERE SeatBookingDetailsID=@id", new
				{
					id = reservationDraft.SeatBookingDetailsID
				}, tx);
				if (!remove)
				{
					var slotTime = db.ExecuteScalar<object>("SELECT FullTiming FROM dbo.TimeAllocation WHERE TimeAllocationID=@id AND ISNULL(IsDeleted,0)=0 AND BookingAvailableStatus=1", new
					{
						id = request.TimeAllocationID
					}, tx);
					var now = CustomerBookingRules.Now;
					if (!CustomerBookingRules.TryReadSlotTime(slotTime, out var startsAt)
						|| (date.Date == now.Date && startsAt < now.TimeOfDay.Add(TimeSpan.FromMinutes(90))))
					{
						return Finish("Invalid Time");
					}
					if (Conflicts(db, tx, reservationDraft, request.TimeAllocationID))
					{
						return Finish("Booked");
					}
				}
				db.Execute("DELETE dbo.CustomerTimeSelection WHERE CustomerID=@customer AND SeatBookingDetailsID=@id\n  AND (@remove=0 OR TimeAllocationID=@slot);", new
				{
					customer = customer,
					id = reservationDraft.SeatBookingDetailsID,
					remove = remove,
					slot = request.TimeAllocationID
				}, tx);
				if (!remove)
				{
					SqlConnection cnn = db;
					var param = new
					{
						TimeSelectionID = Guid.NewGuid(),
						SeatBookingDetailsID = reservationDraft.SeatBookingDetailsID,
						CustomerID = customer,
						TimeAllocationID = request.TimeAllocationID
					};
					SqlTransaction transaction = tx;
					CommandType? commandType = CommandType.StoredProcedure;
					cnn.Execute("dbo.insertCustomerTimeSelection", param, transaction, null, commandType);
				}
				return Finish("Success", (!remove && reservationDraft.Selections.Count > 0) ? "Replace" : "");
			}
			finally
			{
				if (tx != null)
				{
					((IDisposable)tx).Dispose();
				}
			}
		}
		finally
		{
			if (db != null)
			{
				((IDisposable)db).Dispose();
			}
		}
		ReservationReply Finish(string message, string kind = "")
		{
			ReservationReply result = new ReservationReply(message, request.TimeAllocationID, kind, ReadDraft(db, tx, customer));
			tx.Commit();
			return result;
		}
	}

	private static bool Matches(ReservationDraft? draft, AppoinmentModal.ReviewTimeSelectionRequest request, bool ignoreVersion = false)
	{
		if ((object)draft != null && draft.SeatBookingDetailsID == request.SeatBookingDetailsID && draft.BookingDate == request.BookingDate && (ignoreVersion || draft.Version == request.Version))
		{
			Guid[] timeAllocationIDs = request.TimeAllocationIDs;
			if (timeAllocationIDs != null && timeAllocationIDs.Length == 1 && draft.Selections.Count == 1)
			{
				return draft.Selections[0].Id == request.TimeAllocationIDs[0];
			}
		}
		return false;
	}

	public ReservationReply Review(Guid customer, AppoinmentModal.ReviewTimeSelectionRequest request)
	{
		using SqlConnection sqlConnection = Open();
		using SqlTransaction sqlTransaction = sqlConnection.BeginTransaction(IsolationLevel.Serializable);
		Lock(sqlConnection, sqlTransaction, write: false);
		ReservationDraft reservationDraft = ReadDraft(sqlConnection, sqlTransaction, customer);
		string text = ((Matches(reservationDraft, request) && !reservationDraft.Confirmed) ? "Success" : "Selection Changed");
		sqlTransaction.Commit();
		return new ReservationReply(text, Guid.Empty, (text == "Success") ? "Completed" : "", reservationDraft);
	}

	public ReservationReply Checkout(Guid customer, Guid bill, AppoinmentModal.ReviewTimeSelectionRequest? request)
	{
		SqlConnection db = Open();
		SqlTransaction tx;
		try
		{
			tx = db.BeginTransaction(IsolationLevel.Serializable);
			try
			{
				Lock(db, tx, write: true);
				ReservationDraft reservationDraft = ReadDraft(db, tx, customer);
				if ((object)reservationDraft == null)
				{
					return Finish("Booking Required");
				}
				if (bill == Guid.Empty)
				{
					bill = db.QueryFirstOrDefault<Guid>("SELECT TOP(1) UniqueBillID FROM dbo.HairCut_Item_Bill_Master_Primary\nWHERE CreatedBy=@customer AND SeatBookingDetailsID=@id ORDER BY CreatedDate DESC;", new
					{
						customer = customer,
						id = reservationDraft.SeatBookingDetailsID
					}, tx);
				}
				if (reservationDraft.Confirmed)
				{
					return Finish((db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_Bill_Master WHERE UniqueBillID=@bill AND CreatedBy=@customer AND SeatBookingDetailsID=@id AND ISNULL(IsDeleted,0)=0", new
					{
						bill = bill,
						customer = customer,
						id = reservationDraft.SeatBookingDetailsID
					}, tx) > 0 && (request == null || Matches(reservationDraft, request, ignoreVersion: true))) ? "Success" : "Selection Changed");
				}
				if (request != null && !Matches(reservationDraft, request))
				{
					return Finish("Selection Changed");
				}
				if (reservationDraft.Selections.Count != 1 || Conflicts(db, tx, reservationDraft, reservationDraft.Selections[0].Id))
				{
					return Finish("Time Slot Unavailable");
				}
				if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.HairCut_Item_Bill_Master_Primary b\nWHERE b.UniqueBillID=@bill AND b.CreatedBy=@customer AND b.SeatBookingDetailsID=@id\n  AND EXISTS(SELECT 1 FROM dbo.HairCut_Item_Customer_Bill_Primary i WHERE i.UniqueBillID=b.UniqueBillID AND i.Created_By=@customer);", new
				{
					bill = bill,
					customer = customer,
					id = reservationDraft.SeatBookingDetailsID
				}, tx) != 1)
				{
					return Finish("Cart Empty");
				}
				SqlConnection cnn = db;
				var param = new
				{
					UniqueBillID = bill
				};
				SqlTransaction transaction = tx;
				CommandType? commandType = CommandType.StoredProcedure;
				cnn.Execute("dbo.SelectInsertPrimaryToConstant", param, transaction, null, commandType);
				SqlConnection cnn2 = db;
				var param2 = new
				{
					SeatBookingDetailsID = reservationDraft.SeatBookingDetailsID,
					CustomerID = customer,
					BookingStatusMasterID = 2
				};
				SqlTransaction transaction2 = tx;
				commandType = CommandType.StoredProcedure;
				cnn2.Execute("dbo.updateBookingStatus", param2, transaction2, null, commandType);
				// Preserve this project's cart cleanup, within the confirmation transaction.
				db.Execute("dbo.deleteClientSideCartItemsByBill", new
				{
					UniqueBillID = bill,
					CustomerID = customer
				}, tx, commandType: CommandType.StoredProcedure);
				return Finish("Success");
			}
			finally
			{
				if (tx != null)
				{
					((IDisposable)tx).Dispose();
				}
			}
		}
		finally
		{
			if (db != null)
			{
				((IDisposable)db).Dispose();
			}
		}
		ReservationReply Finish(string message)
		{
			ReservationReply result = new ReservationReply(message, Guid.Empty, "", ReadDraft(db, tx, customer));
			tx.Commit();
			return result;
		}
	}
}
