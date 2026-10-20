namespace InfinityHairartsAPI.Services;

// Applied after UseSession. Owner/admin endpoints keep their existing authentication.
public sealed class CustomerApiSessionMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> PrivateLoginRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/session", "/uploadCustomerImage", "/updateCustomerRegistration",
        "/checkCustomerMobileNumberExist", "/getEmployeeDetails"
    };

    public async Task InvokeAsync(HttpContext context, CustomerSessionRegistry registry)
    {
        var request = context.Request;
        var appointment = request.Path.StartsWithSegments("/api/Appoinment");
        var login = request.Path.StartsWithSegments("/api/Login", out var remaining);
        if (appointment || login)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
                && !HttpMethods.IsOptions(request.Method) && request.Headers["X-Salonexa-Request"] != "1")
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { message = "Use the application to submit this request." });
                return;
            }
            if ((appointment || PrivateLoginRoutes.Contains(remaining.Value?.TrimEnd('/') ?? ""))
                && (!Guid.TryParse(context.Session.GetString("CustomerID"), out var customer)
                    || customer == Guid.Empty || !registry.IsActive(context.Session, customer)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { item1 = "Session Expired" });
                return;
            }
        }
        await next(context);
    }
}
