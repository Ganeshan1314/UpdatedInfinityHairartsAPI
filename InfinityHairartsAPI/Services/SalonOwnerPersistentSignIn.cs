using System.Security.Claims;
using InfinityHairartsAPI.Modals;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace InfinityHairartsAPI.Services;

public sealed class SalonOwnerPersistentSignIn(ISalonOwnerAuthenticationService owners)
{
    public const string Scheme = "SalonexaOwner";
    public const string CookieName = "Salonexa.Owner";
    public const string Contract = "persistent-cookie-v1";
    private const string StampClaim = "salonexa:credential-version";
    private static readonly object RestoredOwnerKey = new();
    private static readonly string[] OwnerSessionKeys = ["SalonOwnerID", "SalonMasterID", "SalonOwnerName", "SalonName"];

    public Task SignInAsync(HttpContext context, SalonOwnerLoginResponse owner)
    {
        if (owner.SalonOwnerID == Guid.Empty || string.IsNullOrEmpty(owner.AuthenticationStamp))
            throw new InvalidOperationException("A validated owner is required to create a persistent sign-in.");

        SetSession(context, owner);
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, owner.SalonOwnerID.ToString()),
            new Claim(StampClaim, owner.AuthenticationStamp),
        ], Scheme);
        return context.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
        });
    }

    public async Task RestoreSessionAsync(HttpContext context)
    {
        var ticket = await context.AuthenticateAsync(Scheme);
        if (!ticket.Succeeded)
        {
            // A rejected ticket must not fall back to stale owner session data.
            if (context.Request.Cookies.ContainsKey(CookieName)) await SignOutAsync(context);
            return;
        }

        var id = ticket.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = ticket.Principal?.FindFirstValue(StampClaim);
        var owner = Guid.TryParse(id, out var ownerId) && ownerId != Guid.Empty
            ? await owners.GetActiveSessionOwnerAsync(ownerId, context.RequestAborted)
            : null;
        // Recheck account/salon activity and password version before restoring any owner access.
        if (owner is null || string.IsNullOrEmpty(stamp) || stamp != owner.AuthenticationStamp)
        {
            await SignOutAsync(context);
            return;
        }

        SetSession(context, owner);
        context.Items[RestoredOwnerKey] = owner;
    }

    public static SalonOwnerLoginResponse? RestoredOwner(HttpContext context) =>
        context.Items.TryGetValue(RestoredOwnerKey, out var value) ? value as SalonOwnerLoginResponse : null;

    public Task SignOutAsync(HttpContext context)
    {
        foreach (var key in OwnerSessionKeys) context.Session.Remove(key);
        context.Items.Remove(RestoredOwnerKey);
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        return context.SignOutAsync(Scheme);
    }

    private static void SetSession(HttpContext context, SalonOwnerLoginResponse owner)
    {
        context.Session.SetString("SalonOwnerID", owner.SalonOwnerID.ToString());
        context.Session.SetString("SalonMasterID", owner.SalonMasterID.ToString());
        context.Session.SetString("SalonOwnerName", owner.OwnerName);
        context.Session.SetString("SalonName", owner.SalonName);
    }
}

public static class SalonOwnerPersistentSignInRegistration
{
    public static IServiceCollection AddSalonOwnerPersistentSignIn(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredPath = configuration["Authentication:KeyRingPath"];
        var keyPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "DataProtectionKeys")
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);
        var protection = services.AddDataProtection()
            .SetApplicationName("Salonexa.OwnerAuthentication")
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        // IIS app-pool identities may not have a loaded Windows user profile.
        // Machine-scope DPAPI encrypts persisted keys without requiring that profile.
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi(protectToLocalMachine: true);

        services.AddAuthentication().AddCookie(SalonOwnerPersistentSignIn.Scheme, options =>
        {
            options.Cookie.Name = SalonOwnerPersistentSignIn.CookieName;
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            // Android's bundled app and the HTTPS API have different origins.
            options.Cookie.SameSite = SameSiteMode.None;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        services.AddScoped<SalonOwnerPersistentSignIn>();
        return services;
    }
}

public sealed class SalonOwnerSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SalonOwnerPersistentSignIn signIn)
    {
        if (context.Request.Path.StartsWithSegments("/api/Admin", StringComparison.OrdinalIgnoreCase, out var action)
            && !HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Salonexa-Owner-Auth"] = SalonOwnerPersistentSignIn.Contract;
            var route = action.Value?.TrimEnd('/');
            // Login replaces the ticket; logout must also work when the account is unavailable.
            if (!string.Equals(route, "/login", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(route, "/logout", StringComparison.OrdinalIgnoreCase))
                await signIn.RestoreSessionAsync(context);
        }
        await next(context);
    }
}
