using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace InfinityHairartsAPI.Services;

public sealed record SuperAdminSession(Guid SuperAdminID, string UserName, string DisplayName)
{
    public string Role => SuperAdminAuthentication.Role;
}
public sealed class SuperAdminLogin
{
    [Required, StringLength(100)] public string UserName { get; set; } = "";
    [Required, StringLength(256)] public string Password { get; set; } = "";
}

public sealed record SuperAdminSignIn(SuperAdminSession Session, string Stamp);

// Accounts live only in dbo.SuperAdmin. Salon registration never grants this role.
public sealed class SuperAdminAuthentication(ISuperAdminAccountStore accounts)
{
    public const string Scheme = "SalonexaSuperAdmin";
    public const string Role = "SuperAdmin";
    public const string CookieName = "Salonexa.SuperAdmin";
    public const string StampClaim = "salonexa:super-admin-version";
    private static readonly object SessionKey = new();
    public static string StampFor(SuperAdminAccount account) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{account.SuperAdminID}:{account.UserName}:{account.Role}:{account.PasswordHash}")));
    private static bool CanSignIn(SuperAdminAccount? account) => account is { IsActive: true, Role: Role };
    private static SuperAdminSession SessionFor(SuperAdminAccount account) => new(account.SuperAdminID, account.UserName, account.DisplayName);
    public static SuperAdminSession? CurrentSession(HttpContext context) => context.Items[SessionKey] as SuperAdminSession;

    public async Task<SuperAdminSignIn?> AuthenticateAsync(SuperAdminLogin request, CancellationToken cancellationToken)
    {
        var account = await accounts.FindByUserNameAsync(request.UserName.Trim(), cancellationToken);
        if (!CanSignIn(account)) return null;
        try
        {
            if (new PasswordHasher<string>().VerifyHashedPassword(account!.UserName, account.PasswordHash, request.Password)
                == PasswordVerificationResult.Failed) return null;
        }
        catch (FormatException) { return null; }
        if (!await accounts.MarkLoginAsync(account!, cancellationToken)) return null;
        return new(SessionFor(account!), StampFor(account!));
    }

    public async Task<SuperAdminSession?> ValidateAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken)
    {
        if (principal?.IsInRole(Role) != true
            || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return null;
        var account = await accounts.FindByIdAsync(id, cancellationToken);
        return CanSignIn(account) && principal.FindFirstValue(StampClaim) == StampFor(account!)
            && principal.FindFirstValue(ClaimTypes.Name) == account!.UserName ? SessionFor(account!) : null;
    }

    public static IServiceCollection Register(IServiceCollection services)
    {
        services.AddScoped<SuperAdminAuthentication>();
        services.AddScoped<ISuperAdminAccountStore, SuperAdminAccountStore>();
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
                var session = await auth.ValidateAsync(context.Principal, context.HttpContext.RequestAborted);
                if (session is null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutSuperAdminAsync();
                }
                else context.HttpContext.Items[SessionKey] = session;
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
