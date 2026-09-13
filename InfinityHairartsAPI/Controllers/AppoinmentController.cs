using InfinityHairartsAPI.Services;
using InfinityHairartsAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using static InfinityHairartsAPI.Modals.AppoinmentModal;
using static InfinityHairartsAPI.Modals.LoginModal;

namespace InfinityHairartsAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [SessionTimeout]
    public class AppoinmentController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly AppoinmentService _appoinmentService;
        public AppoinmentController(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
        {
            _appoinmentService = new AppoinmentService(httpContextAccessor, configuration);
        }

        [HttpPost("getSeatTimeAllocation")]
        public ActionResult getSeatTimeAllocation()
        {
            return new JsonResult(_appoinmentService.getSeatTimeAllocation());
        }
        [HttpPost("goNextSeatSelection")]
        public ActionResult goNextSeatSelection()
        {
            return new JsonResult(_appoinmentService.goNextSeatSelection());
        }
        [HttpPost("insertupdateSeatBookingDetails")]
        public ActionResult insertupdateSeatBookingDetails([FromBody] insertupdateSeatBookingDetails seatBookingModal)
        {
            int SeatCount = seatBookingModal.SeatCount;
            DateTime BookingDate = Convert.ToDateTime(seatBookingModal.BookingDate);
            return new JsonResult(_appoinmentService.insertupdateSeatBookingDetails(SeatCount, BookingDate));
            //return new JsonResult("");
        }
        [HttpPost("insertCustomerTimeSelection")]
        public ActionResult insertCustomerTimeSelection([FromBody] insertCustomerTimeSelection TimeAllocationModal)
        {
            Guid TimeAllocationID = new Guid(TimeAllocationModal.TimeAllocationID);
            return new JsonResult(_appoinmentService.insertCustomerTimeSelection(TimeAllocationID));
        }
        [HttpPost("deleteCustomerTimeSelection")]
        public ActionResult deleteCustomerTimeSelection([FromBody] insertCustomerTimeSelection TimeAllocationModal)
        {
            Guid TimeAllocationID = new Guid(TimeAllocationModal.TimeAllocationID);
            return new JsonResult(_appoinmentService.deleteCustomerTimeSelection(TimeAllocationID));
        }
        [HttpPost("filterHariCutItemGroupMaster")]
        public ActionResult filterHariCutItemGroupMaster([FromBody] HairCutItemGroupMasterModal modal)
        {
            Guid HairCutItemGroupMaster_ID = modal.HairCutItemGroupMaster_ID;
            return new JsonResult(_appoinmentService.filterHariCutItemGroupMaster(HairCutItemGroupMaster_ID));
        }

        [HttpPost("AddtoCartHairCutItem")]
        public ActionResult AddtoCartHairCutItem([FromBody] AddtoCartModal modal)
        {
            Guid HairCut_Item_ID = modal.HairCut_Item_ID;
            return new JsonResult(_appoinmentService.AddtoCartHairCutItem(modal));
        }
        [HttpPost("removeClientSideCartItem")]
        public ActionResult removeClientSideCartItem([FromBody] AddtoCartModal modal)
        {
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
        public ActionResult SendOTPCustomer()
        {
            return new JsonResult(_appoinmentService.SendOTPCustomer());
        }
    }
}
