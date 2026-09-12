using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfinityHairartsAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AdminController : ControllerBase
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;
        private readonly AdminService _adminservice;
        private readonly IWebHostEnvironment _env;
        public AdminController(IHttpContextAccessor httpContextAccessor, IConfiguration configuration, IWebHostEnvironment env)
        {
            _adminservice = new AdminService(httpContextAccessor, configuration, env);
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
