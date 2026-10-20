using System;
using System.ComponentModel.DataAnnotations;

namespace InfinityHairartsAPI.Modals;

public class AppoinmentModal
{
	public class insertupdateSeatBookingDetails
	{
		[Range(1, 1, ErrorMessage = "Book one person at a time.")]
		public int SeatCount { get; set; }

		[Required]
		[StringLength(10)]
		public string BookingDate { get; set; } = string.Empty;

		[Required]
		[NonEmptyGuid]
		public Guid? SalonMasterID { get; set; }
	}

	public class insertCustomerTimeSelection
	{
		[NonEmptyGuid]
		public Guid TimeAllocationID { get; set; }

		[NonEmptyGuid]
		public Guid? SeatBookingDetailsID { get; set; }

		[Range(0.0, 9.223372036854776E+18)]
		public long? ExpectedVersion { get; set; }

		[StringLength(10)]
		public string? BookingDate { get; set; }

		[NonEmptyGuid]
		public Guid? SalonMasterID { get; set; }
	}

	public class ReviewTimeSelectionRequest
	{
		[NonEmptyGuid]
		public Guid SeatBookingDetailsID { get; set; }

		[Required]
		[Range(0.0, 9.223372036854776E+18)]
		public long? Version { get; set; }

		[Required]
		[RegularExpression("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")]
		public string BookingDate { get; set; } = string.Empty;

		[Required]
		[MinLength(1)]
		[MaxLength(1)]
		public Guid[] TimeAllocationIDs { get; set; } = Array.Empty<Guid>();
	}

	public class HairCutItemGroupMasterModal
	{
		public Guid HairCutItemGroupMaster_ID { get; set; }
	}

	public class AddtoCartModal
	{
		[NonEmptyGuid]
		public Guid HairCut_Item_ID { get; set; }

		[Range(0, 1)]
		public int seatCount { get; set; }
	}
}
