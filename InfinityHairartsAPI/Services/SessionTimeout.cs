using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
namespace InfinityHairartsAPI.Services;

/// <summary>
/// Requires a live customer session for customer-specific API endpoints.
/// ASP.NET Core removes CustomerID after the configured idle timeout.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SessionTimeoutAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        // Public catalog actions opt out explicitly with [AllowAnonymous].
        if (context.ActionDescriptor.EndpointMetadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any())
        {
            return;
        }

        var customerId = context.HttpContext.Session.GetString("CustomerID");
        if (Guid.TryParse(customerId, out _))
        {
            return;
        }

        // The Ionic HTTP interceptor handles 401 and redirects to /login.
        // Include item1 to preserve compatibility with existing API responses.
        context.Result = new UnauthorizedObjectResult(new { item1 = "Session Expired" });
    }
}
