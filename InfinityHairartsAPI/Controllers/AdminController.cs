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
        private readonly ISalonOwnerAuthenticationService _salonOwnerAuthenticationService;
        private readonly SalonOwnerDashboardService _salonOwnerDashboardService;
        private readonly BookingQrService _bookingQrService;
        private readonly SalonOwnerPersistentSignIn _persistentSignIn;
        public AdminController(
            IHttpContextAccessor httpContextAccessor,
            IConfiguration configuration,
            IWebHostEnvironment env,
            ISalonOwnerAuthenticationService salonOwnerAuthenticationService,
            SalonOwnerDashboardService salonOwnerDashboardService,
            BookingQrService bookingQrService,
            SalonOwnerPersistentSignIn persistentSignIn)
        {
            _adminservice = new AdminService(httpContextAccessor, configuration, env);
            _env = env;
            _salonOwnerAuthenticationService = salonOwnerAuthenticationService;
            _salonOwnerDashboardService = salonOwnerDashboardService;
            _bookingQrService = bookingQrService;
            _persistentSignIn = persistentSignIn;
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

            await _persistentSignIn.SignInAsync(HttpContext, owner);
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

            var owner = SalonOwnerPersistentSignIn.RestoredOwner(HttpContext)
                ?? await _salonOwnerAuthenticationService.GetActiveSessionOwnerAsync(salonOwnerId, cancellationToken);

            if (owner is null)
            {
                await _persistentSignIn.SignOutAsync(HttpContext);
                return Unauthorized(new { message = "The salon-owner session has expired." });
            }

            // Upgrade a still-valid legacy session on the next session check.
            if (SalonOwnerPersistentSignIn.RestoredOwner(HttpContext) is null)
                await _persistentSignIn.SignInAsync(HttpContext, owner);
            return Ok(owner);
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            await _persistentSignIn.SignOutAsync(HttpContext);
            return NoContent();
        }

        [HttpGet("dashboard")]
        public async Task<ActionResult<SalonOwnerDashboardResponse>> GetDashboard(
            [FromQuery] DateTime? date,
            CancellationToken cancellationToken)
        {
            var salonOwnerIdValue = HttpContext.Session.GetString("SalonOwnerID");
            var salonMasterIdValue = HttpContext.Session.GetString("SalonMasterID");
            if (!Guid.TryParse(salonOwnerIdValue, out var salonOwnerId) ||
                !Guid.TryParse(salonMasterIdValue, out var salonMasterId))
            {
                return Unauthorized(new { message = "The salon-owner session has expired." });
            }

            try
            {
                return Ok(await _salonOwnerDashboardService.GetDashboardAsync(
                    salonOwnerId,
                    salonMasterId,
                    date,
                    cancellationToken));
            }
            catch (SqlException exception) when (exception.Number is 50002 or 50003)
            {
                return Conflict(new { message = exception.Message });
            }
            catch (SqlException exception) when (exception.Number == 50013)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = exception.Message });
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
