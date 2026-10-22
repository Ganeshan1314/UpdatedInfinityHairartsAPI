using InfinityHairartsAPI.Services;
using InfinityHairartsAPI.Modals;
using Microsoft.AspNetCore.Mvc;
using static InfinityHairartsAPI.Modals.LoginModal;

namespace InfinityHairartsAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class LoginController : ControllerBase
    {
        private readonly LoginService _loginservice;

        public LoginController(LoginService loginservice)
        {
            _loginservice = loginservice;
        }

        [HttpPost("insertUserRegistration")]
        public ActionResult InsertUserRegistration()
        {
            return Ok("");
        }

        [HttpPost("CheckUserLogin")]
        public ActionResult CheckUserLogin(string MobileNo)
        {
            return Ok(_loginservice.CheckUserLogin(MobileNo));
        }

        [HttpPost("checkCustomerMobileNumberExist")]
        public ActionResult CheckCustomerMobileNumberExist([FromBody] getaddressModal modal)
        {
            return Ok(_loginservice.checkCustomerMobileNumberExist(modal));
        }

	[HttpPost("customerLoginButtomClick")]
	public async Task<IActionResult> CustomerLoginButtomClick([FromBody] LoginModal.CustomerLoginRequest modal, [FromServices] CustomerSessionRegistry registry, CancellationToken token)
	{
		string text = modal.MobileNo?.Trim() ?? "";
		if (!CustomerMobileNumber.IsValid(text))
		{
			return BadRequest(new
			{
				message = "Enter a valid mobile number."
			});
		}
		registry.Revoke(base.HttpContext.Session);
		CustomerSession.Clear(base.HttpContext.Session);
		try
		{
			Tuple<string, List<Dictionary<string, object>>> result = _loginservice.customerLoginButtomClick(new LoginModal.CustomerLoginRequest
			{
				MobileNo = text,
				Address = modal.Address?.Trim()
			});
			if (result.Item1 != "Success" || !Guid.TryParse(base.HttpContext.Session.GetString("CustomerID"), out var result2) || result2 == Guid.Empty)
			{
				CustomerSession.Clear(base.HttpContext.Session);
				return StatusCode(503, new
				{
					message = "Sign-in is temporarily unavailable."
				});
			}
			base.HttpContext.Session.SetString("CustomerAuthToken", registry.Create(result2));
			await base.HttpContext.Session.CommitAsync(token);
			return Ok(result);
		}
		catch (Exception ex) when (!(ex is OperationCanceledException))
		{
			registry.Revoke(base.HttpContext.Session);
			CustomerSession.Clear(base.HttpContext.Session);
			return StatusCode(503, new
			{
				message = "Sign-in is temporarily unavailable."
			});
		}
	}

        [HttpPost("uploadCustomerImage")]
        [SessionTimeout]
        public async Task<ActionResult> UploadCustomerImage(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("No file uploaded");
            }

            var result = await _loginservice.uploadCustomerImage(file);
            return Ok(new { fileName = result });
        }

        [HttpPost("updateCustomerRegistration")]
        [SessionTimeout]
        public ActionResult UpdateCustomerRegistration([FromBody] UpdateCustomerProfileRequest modal)
        {
            if (!CustomerEmailAddress.IsValid(modal.EmailAddress))
            {
                return BadRequest(new { message = "Enter a valid Gmail address ending in @gmail.com." });
            }
            return Ok(_loginservice.updateCustomerRegistration(modal));
        }

	[HttpPost("logout")]
	[HttpPost("clearSessionValues")]
	[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
	public async Task<ActionResult> ClearSessionValues([FromServices] CustomerSessionRegistry registry, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (base.Request.Headers["X-Salonexa-Request"] != "1")
		{
			return BadRequest(new
			{
				message = "Sign out using the application."
			});
		}
		registry.Revoke(base.HttpContext.Session);
		CustomerSession.Clear(base.HttpContext.Session);
		await base.HttpContext.Session.CommitAsync(cancellationToken);
		return new JsonResult("Success");
	}

        [HttpGet("session")]
        public IActionResult SessionStatus() => Ok(new { item1 = "Success" });

        [HttpPost("getEmployeeDetails")]
        [SessionTimeout]
        public ActionResult GetEmployeeDetails()
        {
            return Ok(_loginservice.getEmployeeDetails());
        }

        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new { status = "ok", apiContract = "mobile-login-reservations-v1", build = "updated-api-20261020" });
        }
    }
}
