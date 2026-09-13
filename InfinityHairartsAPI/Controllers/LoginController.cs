using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using static InfinityHairartsAPI.Modals.LoginModal;

namespace InfinityHairartsAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
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
        public ActionResult CustomerLoginButtomClick([FromBody] CustomerLoginRequest modal)
        {
            var result = _loginservice.customerLoginButtomClick(modal);
            return Ok(result);
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
            return Ok(_loginservice.updateCustomerRegistration(modal));
        }

        [HttpGet("clearSessionValues")]
        public ActionResult ClearSessionValues()
        {
            HttpContext.Session.Clear();
            return new JsonResult("Success");
        }

        [HttpPost("getEmployeeDetails")]
        [SessionTimeout]
        public ActionResult GetEmployeeDetails()
        {
            return Ok(_loginservice.getEmployeeDetails());
        }

        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new { status = "ok" });
        }
    }
}
