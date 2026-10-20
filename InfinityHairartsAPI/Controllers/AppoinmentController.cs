using System;
using System.Collections.Generic;
using System.Linq;
using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace InfinityHairartsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[SessionTimeout]
public class AppoinmentController : ControllerBase
{
	private readonly AppoinmentService _appoinmentService;

	public AppoinmentController(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
	{
		_appoinmentService = new AppoinmentService(httpContextAccessor, configuration);
	}

	[HttpPost("getSeatTimeAllocation")]
	public ActionResult getSeatTimeAllocation([FromQuery] DateTime? bookingDate = null)
	{
		if (bookingDate.HasValue && !CustomerBookingRules.IsAvailableDate(bookingDate.Value, CustomerBookingRules.Today))
		{
			return BadRequest(new
			{
				message = "Please choose an available booking date."
			});
		}
		Tuple<string, List<Dictionary<string, object>>, int, List<Dictionary<string, object>>, List<Dictionary<string, object>>> seatTimeAllocation = _appoinmentService.getSeatTimeAllocation(bookingDate);
		if (seatTimeAllocation.Item1 != "Success")
		{
			return new JsonResult(seatTimeAllocation);
		}
		try
		{
			return new JsonResult(new
			{
				Item1 = seatTimeAllocation.Item1,
				Item2 = seatTimeAllocation.Item2,
				Item3 = seatTimeAllocation.Item3,
				Item4 = seatTimeAllocation.Item4,
				Item5 = seatTimeAllocation.Item5,
				Draft = _appoinmentService.ReservationStore().GetDraft(_appoinmentService.CustomerID())
			});
		}
		catch
		{
			return StatusCode(503, new
			{
				message = "Appointment times could not be confirmed."
			});
		}
	}

	[HttpGet("time-selection")]
	public ActionResult GetTimeSelection()
	{
		try
		{
			return Ok(new
			{
				item1 = "Success",
				Draft = _appoinmentService.ReservationStore().GetDraft(_appoinmentService.CustomerID())
			});
		}
		catch
		{
			return StatusCode(503, new
			{
				message = "Your saved time could not be confirmed."
			});
		}
	}

	[HttpPost("goNextSeatSelection")]
	public ActionResult goNextSeatSelection([FromBody] AppoinmentModal.ReviewTimeSelectionRequest request)
	{
		if (request.TimeAllocationIDs.Any((Guid id) => id == Guid.Empty))
		{
			return BadRequest();
		}
		try
		{
			ReservationReply reservationReply = _appoinmentService.ReservationStore().Review(_appoinmentService.CustomerID(), request);
			return new JsonResult(new
			{
				Item1 = reservationReply.Item1,
				Item2 = reservationReply.Item3,
				Item3 = 1,
				Draft = reservationReply.Draft
			});
		}
		catch
		{
			return StatusCode(503, new
			{
				message = "Your selected time could not be checked."
			});
		}
	}

	[HttpPost("insertupdateSeatBookingDetails")]
	public ActionResult insertupdateSeatBookingDetails([FromBody] AppoinmentModal.insertupdateSeatBookingDetails seatBookingModal)
	{
		if (seatBookingModal.SeatCount != 1)
		{
			return BadRequest(new
			{
				message = "Book one person at a time."
			});
		}
		if (seatBookingModal.SalonMasterID == Guid.Empty)
		{
			return BadRequest(new
			{
				message = "Select a valid salon."
			});
		}
		if (!CustomerBookingRules.TryReadDate(seatBookingModal.BookingDate, out var date) || !CustomerBookingRules.IsAvailableDate(date, CustomerBookingRules.Today))
		{
			return BadRequest(new
			{
				message = "Please choose an available booking date."
			});
		}
		return new JsonResult(_appoinmentService.insertupdateSeatBookingDetails(1, date, seatBookingModal.SalonMasterID!.Value));
	}

	[HttpPost("insertCustomerTimeSelection")]
	public ActionResult insertCustomerTimeSelection([FromBody] AppoinmentModal.insertCustomerTimeSelection TimeAllocationModal)
	{
		Guid timeAllocationID = TimeAllocationModal.TimeAllocationID;
		if (timeAllocationID == Guid.Empty)
		{
			return BadRequest(new
			{
				message = "Select a valid time slot."
			});
		}
		if (!TimeAllocationModal.SeatBookingDetailsID.HasValue || !TimeAllocationModal.ExpectedVersion.HasValue || !CustomerBookingRules.TryReadDate(TimeAllocationModal.BookingDate, out var _))
		{
			return BadRequest(new
			{
				message = "Refresh your booking before changing its time."
			});
		}
		try
		{
			return new JsonResult(_appoinmentService.ReservationStore().Change(_appoinmentService.CustomerID(), TimeAllocationModal, remove: false));
		}
		catch
		{
			return StatusCode(503, new
			{
				message = "Your selected time could not be confirmed. Retry the same request."
			});
		}
	}

	[HttpPost("deleteCustomerTimeSelection")]
	public ActionResult deleteCustomerTimeSelection([FromBody] AppoinmentModal.insertCustomerTimeSelection TimeAllocationModal)
	{
		Guid timeAllocationID = TimeAllocationModal.TimeAllocationID;
		if (timeAllocationID == Guid.Empty)
		{
			return BadRequest(new
			{
				message = "Select a valid time slot."
			});
		}
		if (!TimeAllocationModal.SeatBookingDetailsID.HasValue || !TimeAllocationModal.ExpectedVersion.HasValue || !CustomerBookingRules.TryReadDate(TimeAllocationModal.BookingDate, out var _))
		{
			return BadRequest(new
			{
				message = "Refresh your booking before changing its time."
			});
		}
		try
		{
			return new JsonResult(_appoinmentService.ReservationStore().Change(_appoinmentService.CustomerID(), TimeAllocationModal, remove: true));
		}
		catch
		{
			return StatusCode(503, new
			{
				message = "Your selected time could not be confirmed. Retry the same request."
			});
		}
	}

	[HttpPost("filterHariCutItemGroupMaster")]
	public ActionResult filterHariCutItemGroupMaster([FromBody] AppoinmentModal.HairCutItemGroupMasterModal modal)
	{
		Guid hairCutItemGroupMaster_ID = modal.HairCutItemGroupMaster_ID;
		return new JsonResult(_appoinmentService.filterHariCutItemGroupMaster(hairCutItemGroupMaster_ID));
	}

	[HttpPost("AddtoCartHairCutItem")]
	public ActionResult AddtoCartHairCutItem([FromBody] AppoinmentModal.AddtoCartModal modal)
	{
		Guid hairCut_Item_ID = modal.HairCut_Item_ID;
		if (hairCut_Item_ID == Guid.Empty)
		{
			return BadRequest(new
			{
				message = "Select a valid service."
			});
		}
		return new JsonResult(_appoinmentService.AddtoCartHairCutItem(modal));
	}

	[HttpPost("removeClientSideCartItem")]
	public ActionResult removeClientSideCartItem([FromBody] AppoinmentModal.AddtoCartModal modal)
	{
		if (modal.HairCut_Item_ID == Guid.Empty)
		{
			return BadRequest(new
			{
				message = "Select a valid service."
			});
		}
		return new JsonResult(_appoinmentService.removeClientSideCartItem(modal));
	}

	[HttpPost("checkSeatCountCartItems")]
	public ActionResult checkSeatCountCartItems()
	{
		return new JsonResult(_appoinmentService.checkSeatCountCartItems());
	}

	[HttpPost("getCustomerCart")]
	public ActionResult getCustomerCart()
	{
		return new JsonResult(_appoinmentService.getCustomerCart());
	}

	[HttpPost("getCustomerBookingDetails")]
	public ActionResult getCustomerBookingDetails()
	{
		return new JsonResult(_appoinmentService.getCustomerBookingDetails());
	}

	[HttpPost("SendOTPCustomer")]
	public ActionResult SendOTPCustomer([FromBody] AppoinmentModal.ReviewTimeSelectionRequest request)
	{
		if (request.TimeAllocationIDs.Any((Guid id) => id == Guid.Empty))
		{
			return BadRequest();
		}
		return new JsonResult(_appoinmentService.clientSideCheckoutCartItem(request));
	}
}
