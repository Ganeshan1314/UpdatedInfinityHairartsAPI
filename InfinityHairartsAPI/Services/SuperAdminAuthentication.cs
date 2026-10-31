using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace InfinityHairartsAPI.Services;

public sealed record SuperAdminSession(string UserName, string DisplayName);
public sealed class SuperAdminLogin
{
    [Required, StringLength(100)] public string UserName { get; set; } = "";
    [Required, StringLength(256)] public string Password { get; set; } = "";
}

// Provisioned only through server configuration. Salon registration never grants this role.
public sealed class SuperAdminAuthentication(IConfiguration configuration)
{
    public const string Scheme = "SalonexaSuperAdmin";
    public const string CookieName = "Salonexa.SuperAdmin";
    public const string StampClaim = "salonexa:super-admin-version";
    public SuperAdminSession? Current =>
        !string.IsNullOrWhiteSpace(configuration["SuperAdmin:UserName"])
        && !string.IsNullOrWhiteSpace(configuration["SuperAdmin:PasswordHash"])
            ? new(configuration["SuperAdmin:UserName"]!, configuration["SuperAdmin:DisplayName"] ?? "Application owner")
            : null;
    public string Stamp => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        configuration["SuperAdmin:UserName"] + ":" + configuration["SuperAdmin:PasswordHash"])));

    public SuperAdminSession? Authenticate(SuperAdminLogin request)
    {
        var account = Current;
        if (account is null || !string.Equals(account.UserName, request.UserName.Trim(), StringComparison.Ordinal)) return null;
        try
        {
            return new PasswordHasher<string>().VerifyHashedPassword(account.UserName,
                configuration["SuperAdmin:PasswordHash"]!, request.Password) != PasswordVerificationResult.Failed ? account : null;
        }
        catch (FormatException) { return null; }
    }

    public static IServiceCollection Register(IServiceCollection services)
    {
        services.AddScoped<SuperAdminAuthentication>();
        services.AddScoped<ISuperAdminDashboard, SuperAdminDashboardService>();
        services.AddAuthorization();
        services.AddAuthentication().AddCookie(Scheme, options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.None;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = async context =>
            {
                var auth = context.HttpContext.RequestServices.GetRequiredService<SuperAdminAuthentication>();
                if (auth.Current is null || context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) != auth.Current.UserName
                    || context.Principal?.FindFirstValue(StampClaim) != auth.Stamp)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutSuperAdminAsync();
                }
            };
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("super-admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }
}

public static class SuperAdminCookieExtensions
{
    public static Task SignOutSuperAdminAsync(this HttpContext context) =>
        Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(context, SuperAdminAuthentication.Scheme);
}

public sealed class SuperAdminRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/SuperAdmin", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-store";
            // Non-simple header forces browser preflight; CORS admits only the configured application origins.
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                && !HttpMethods.IsOptions(context.Request.Method) && context.Request.Headers["X-Salonexa-Request"] != "1")
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { message = "Refresh the application and try again." });
                return;
            }
        }
        await next(context);
    }
}
