using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Web;


namespace InfinityHairartsAPI.Services
{
    public class SessionTimeout : ActionFilterAttribute
    {
        public override void OnActionExecuted(ActionExecutedContext context)
        {
            var customerId = context.HttpContext.Session.GetString("CustomerID");
            if (customerId == "")
            {
                //HttpContext.Session.Clear();
                //context.Result = new RedirectResult(ConfigurationManager.AppSettings["LoginUrl"]);
                context.Result = new RedirectToRouteResult(
                    new RouteValueDictionary(new
                    {
                        controller = "Login",
                        action = "Login"
                    })
                );
            }
        }
    }
}
