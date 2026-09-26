using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using InfinityHairartsAPI.Modals;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AdminController : ControllerBase
    {
        private readonly AdminService _adminservice;
        private readonly IWebHostEnvironment _env;
        private readonly SalonOwnerAuthenticationService _salonOwnerAuthenticationService;
        private readonly SalonOwnerDashboardService _salonOwnerDashboardService;
        private readonly BookingQrService _bookingQrService;
        public AdminController(
            IHttpContextAccessor httpContextAccessor,
            IConfiguration configuration,
            IWebHostEnvironment env,
            SalonOwnerAuthenticationService salonOwnerAuthenticationService,
            SalonOwnerDashboardService salonOwnerDashboardService,
            BookingQrService bookingQrService)
        {
            _adminservice = new AdminService(httpContextAccessor, configuration, env);
            _env = env;
            _salonOwnerAuthenticationService = salonOwnerAuthenticationService;
            _salonOwnerDashboardService = salonOwnerDashboardService;
            _bookingQrService = bookingQrService;
        }

        [HttpPost("complete-booking-qr")]
        public async Task<ActionResult<CompleteBookingQrResponse>> CompleteBookingQr(
            [FromBody] CompleteBookingQrRequest request,
            CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(HttpContext.Session.GetString("SalonOwnerID"), out var salonOwnerId) ||
                !Guid.TryParse(HttpContext.Session.GetString("SalonMasterID"), out var salonMasterId))
            {
                return Unauthorized(new { message = "The salon-owner session has expired." });
            }

            try
            {
                return Ok(await _bookingQrService.CompleteAsync(
                    request.QrReference,
                    salonOwnerId,
                    salonMasterId,
                    cancellationToken));
            }
            catch (FormatException exception)
            {
                return BadRequest(new { message = exception.Message });
            }
            catch (SqlException exception) when (exception.Number == 50010)
            {
                return NotFound(new { message = exception.Message });
            }
            catch (SqlException exception) when (exception.Number is 50011 or 50012)
            {
                return Conflict(new { message = exception.Message });
            }
            catch (SqlException exception) when (exception.Number == 50013)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = exception.Message });
            }
        }

        [HttpPost("login")]
        public async Task<ActionResult<SalonOwnerLoginResponse>> Login(
            [FromBody] SalonOwnerLoginRequest request,
            CancellationToken cancellationToken)
        {
            var owner = await _salonOwnerAuthenticationService.AuthenticateAsync(
                request,
                cancellationToken);

            if (owner is null)
            {
                return Unauthorized(new { message = "Invalid username or password." });
            }

            SetSalonOwnerSession(owner);
            return Ok(owner);
        }

        [HttpGet("session")]
        public async Task<ActionResult<SalonOwnerLoginResponse>> GetSession(
            CancellationToken cancellationToken)
        {
            var salonOwnerIdValue = HttpContext.Session.GetString("SalonOwnerID");
            if (!Guid.TryParse(salonOwnerIdValue, out var salonOwnerId))
            {
                return Unauthorized(new { message = "The salon-owner session has expired." });
            }

            var owner = await _salonOwnerAuthenticationService.GetActiveSessionOwnerAsync(
                salonOwnerId,
                cancellationToken);

            if (owner is null)
            {
                HttpContext.Session.Clear();
                return Unauthorized(new { message = "The salon-owner session has expired." });
            }

            SetSalonOwnerSession(owner);
            return Ok(owner);
        }

        [HttpPost("logout")]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return NoContent();
        }

        [HttpGet("dashboard")]
        public async Task<ActionResult<SalonOwnerDashboardResponse>> GetDashboard(
            CancellationToken cancellationToken)
        {
            var salonOwnerIdValue = HttpContext.Session.GetString("SalonOwnerID");
            var salonMasterIdValue = HttpContext.Session.GetString("SalonMasterID");
            if (!Guid.TryParse(salonOwnerIdValue, out _) ||
                !Guid.TryParse(salonMasterIdValue, out var salonMasterId))
            {
                return Unauthorized(new { message = "The salon-owner session has expired." });
            }

            try
            {
                return Ok(await _salonOwnerDashboardService.GetDashboardAsync(
                    salonMasterId,
                    cancellationToken));
            }
            catch (SqlException exception) when (exception.Number is 50002 or 50003)
            {
                return Conflict(new { message = exception.Message });
            }
        }

        [HttpPost("initialize-salon-owner")]
        public async Task<ActionResult<SalonOwnerLoginResponse>> InitializeSalonOwner(
            [FromBody] InitializeSalonOwnerRequest request,
            CancellationToken cancellationToken)
        {
            if (!_env.IsDevelopment())
            {
                return NotFound();
            }

            try
            {
                var owner = await _salonOwnerAuthenticationService.InitializeAsync(
                    request,
                    cancellationToken);
                return CreatedAtAction(nameof(GetSession), owner);
            }
            catch (SqlException exception) when (exception.Number == 50001)
            {
                return Conflict(new { message = exception.Message });
            }
            catch (SqlException exception) when (exception.Number == 50002)
            {
                return BadRequest(new { message = exception.Message });
            }
        }

        private void SetSalonOwnerSession(SalonOwnerLoginResponse owner)
        {
            HttpContext.Session.SetString("SalonOwnerID", owner.SalonOwnerID.ToString());
            HttpContext.Session.SetString("SalonMasterID", owner.SalonMasterID.ToString());
            HttpContext.Session.SetString("SalonOwnerName", owner.OwnerName);
            HttpContext.Session.SetString("SalonName", owner.SalonName);
        }

        [HttpPost("InsertNewHairCutTpyes")]
        public ActionResult InsertNewHairCutTpyes(string HairCutItemName, string HairCutItemPrice, IFormFile imgHairCutphoto)
        {
            return Ok(_adminservice.InsertNewHairCutTpyes(HairCutItemName, HairCutItemPrice, imgHairCutphoto));
        }
        [HttpPost("UpdateNewHairCutTpyes")]
        public ActionResult UpdateNewHairCutTpyes(Guid UniqueItemID, string HairCutItemName, string HairCutItemPrice, IFormFile imgHairCutphoto)
        {
            return Ok(_adminservice.UpdateNewHairCutTpyes(UniqueItemID, HairCutItemName, HairCutItemPrice, imgHairCutphoto));
        }
        [HttpPost("deleteHairCutItemDetails")]
        public ActionResult deleteHairCutItemDetails(Guid UniqueItemID, string ImageName)
        {
            return Ok(_adminservice.deleteHairCutItemDetails(UniqueItemID, ImageName));
        }
        [HttpPost("GetListHairCutTpyes")]
        public ActionResult GetListHairCutTpyes()
        {
            return Ok(_adminservice.GetListHairCutTpyes());
        }
        [HttpPost("getHairCutItemDetails")]
        public ActionResult getHairCutItemDetails(Guid ItemID)
        {
            return Ok(_adminservice.getHairCutItemDetails(ItemID));
        }
        
        [HttpPost("checkSessionExpiredorNot")]
        public ActionResult checkSessionExpiredorNot()
        {
            string Message = string.Empty;
            //if (Convert.ToString(HttpContext.Session["CustomerID"]).Trim() != "")
            //{
            //    Message = "Not Expired";
            //}
            //else
            //{
            //    Message = "Expired";
            //}
            return Ok(Message);
        }
    }
}
