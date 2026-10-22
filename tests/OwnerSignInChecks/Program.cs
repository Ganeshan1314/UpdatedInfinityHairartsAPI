using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Modals;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

// Runs real owner controllers, cookies, session middleware and data protection on loopback.
// Only credential/account lookups use an in-memory fixture; no database or live API is touched.
var owners = new FixtureOwners();
var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
var fixtureDirectory = Path.Combine(baseDirectory, "owner-auth-" + Guid.NewGuid().ToString("N"));
var cookieOrigin = new Uri("https://localhost");
WebApplication? app = null;
HttpClient? client = null;
var cookies = new CookieContainer();
var checks = 0;

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS: " + description);
    checks++;
}

async Task Start()
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Authentication:KeyRingPath"] = fixtureDirectory,
        ["ConnectionStrings:dbconnection"] = "Server=unused;Database=unused;Integrated Security=true",
    });
    builder.Services.AddControllers().AddApplicationPart(typeof(AdminController).Assembly);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<ISalonOwnerAuthenticationService>(owners);
    builder.Services.AddScoped<SalonOwnerDashboardService>();
    builder.Services.AddScoped<BookingQrService>();
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession(options => options.Cookie.Name = ".OwnerSignInChecks.Session");
    builder.Services.AddSalonOwnerPersistentSignIn(builder.Configuration, builder.Environment);
    app = builder.Build();
    // Simulate TLS termination solely inside this loopback test server.
    app.Use(async (context, next) => { context.Request.Scheme = "https"; await next(context); });
    app.Use(async (context, next) =>
    {
        try { await next(context); }
        catch (FixtureUnavailableException) { context.Response.StatusCode = 503; }
    });
    app.UseRouting();
    app.UseSession();
    app.UseMiddleware<SalonOwnerSessionMiddleware>();
    app.MapControllers();
    app.MapPost("/test/legacy-session", (HttpContext context) =>
    {
        context.Session.SetString("SalonOwnerID", owners.Owner.SalonOwnerID.ToString());
        context.Session.SetString("SalonMasterID", owners.Owner.SalonMasterID.ToString());
        return Results.NoContent();
    });
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) };
}

async Task Stop()
{
    client?.Dispose();
    if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
}

async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? explicitCookie = null)
{
    using var request = new HttpRequestMessage(method, path);
    var header = explicitCookie ?? cookies.GetCookieHeader(cookieOrigin);
    if (header.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", header);
    if (body is not null) request.Content = JsonContent.Create(body);
    var response = await client!.SendAsync(request);
    if (response.Headers.TryGetValues("Set-Cookie", out var values))
        foreach (var value in values) cookies.SetCookies(cookieOrigin, value);
    return response;
}

Task<HttpResponseMessage> Login() => Send(HttpMethod.Post, "/api/Admin/login", new { userName = "fixture-owner", password = "fixture-password" });
string OwnerCookie() => cookies.GetCookies(cookieOrigin)[SalonOwnerPersistentSignIn.CookieName]?.Value
    ?? throw new InvalidOperationException("No persistent owner cookie was issued.");

try
{
    await Start();
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session"))
    {
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous session checks require sign-in");
        Check(response.Headers.TryGetValues("X-Salonexa-Owner-Auth", out var versions)
            && versions.Single() == SalonOwnerPersistentSignIn.Contract,
            "The deployed authentication version can be verified without using account credentials");
        Check(response.Headers.CacheControl?.NoStore == true, "Anonymous session failures cannot be cached");
    }
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session", explicitCookie: "Salonexa.Owner=invalid-diagnostic-cookie"))
    {
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "An invalid persistent cookie cannot restore a session");
        Check(response.Headers.TryGetValues("Set-Cookie", out var deletions)
            && deletions.Any(value => value.StartsWith(SalonOwnerPersistentSignIn.CookieName + "=")
                && value.Contains("expires=", StringComparison.OrdinalIgnoreCase)),
            "Rejected persistent cookies are explicitly removed by the updated server");
    }
    using (var response = await Login())
    {
        Check(response.StatusCode == HttpStatusCode.OK, "The actual owner login controller accepts validated credentials");
        var header = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(SalonOwnerPersistentSignIn.CookieName + "="));
        Check(header.Contains("expires=", StringComparison.OrdinalIgnoreCase), "Sign-in issues a persistent cookie instead of a temporary session cookie");
        Check(header.Contains("httponly", StringComparison.OrdinalIgnoreCase)
            && header.Contains("secure", StringComparison.OrdinalIgnoreCase)
            && header.Contains("samesite=none", StringComparison.OrdinalIgnoreCase), "Owner cookie supports Android cross-origin requests and is HTTP-only and secure");
        var payload = await response.Content.ReadAsStringAsync();
        Check(!payload.Contains("authenticationStamp", StringComparison.OrdinalIgnoreCase)
            && !payload.Contains(owners.Owner.AuthenticationStamp), "The credential version is excluded from the public login response");
        Check(response.Headers.CacheControl?.NoStore == true, "Owner authentication responses cannot be cached");
    }
    var ownerCookie = OwnerCookie();
    if (OperatingSystem.IsWindows())
    {
        var keyFiles = Directory.GetFiles(fixtureDirectory, "key-*.xml");
        Check(keyFiles.Length > 0 && keyFiles.All(path =>
        {
            var xml = XDocument.Load(path);
            return xml.Descendants().Any(element => element.Name.LocalName == "encryptedSecret")
                && !xml.Descendants().Any(element => element.Name.LocalName == "masterKey");
        }), "Windows persists encrypted login keys without plaintext master keys");
    }
    Check(cookies.GetCookies(cookieOrigin)[SalonOwnerPersistentSignIn.CookieName]!.Expires > DateTime.UtcNow.AddDays(6), "Persistent sign-in lasts seven days");

    // Android may discard all temporary cookies when its WebView process restarts.
    cookies = new CookieContainer();
    cookies.Add(cookieOrigin, new Cookie(SalonOwnerPersistentSignIn.CookieName, ownerCookie, "/") { Secure = true, Expires = DateTime.UtcNow.AddDays(7) });
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session"))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "Reopening Android restores sign-in with only the persistent cookie");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Check(payload.GetProperty("salonOwnerID").GetGuid() == owners.Owner.SalonOwnerID, "Restoration returns the correct owner");
    }
    using (var response = await Send(HttpMethod.Post, "/api/Admin/complete-booking-qr", new { qrReference = "invalid-fixture-qr" },
        explicitCookie: SalonOwnerPersistentSignIn.CookieName + "=" + ownerCookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "A private booking endpoint restores authentication before validating its QR input");

    await Stop();
    await Start();
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session", explicitCookie: SalonOwnerPersistentSignIn.CookieName + "=" + ownerCookie))
        Check(response.StatusCode == HttpStatusCode.OK, "An API restart restores sign-in using the persisted encryption keys and a fresh session cache");

    owners.Unavailable = true;
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session"))
    {
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable, "Temporary account-store failures are surfaced as server errors");
        Check(OwnerCookie() == ownerCookie, "A temporary failure preserves the persistent sign-in cookie");
    }
    owners.Unavailable = false;
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session"))
        Check(response.StatusCode == HttpStatusCode.OK, "Restoration succeeds when the temporary failure ends");

    using (var response = await Send(HttpMethod.Post, "/api/Admin/logout", new { }))
        Check(response.StatusCode == HttpStatusCode.NoContent, "Explicit logout succeeds");
    Check(cookies.GetCookies(cookieOrigin)[SalonOwnerPersistentSignIn.CookieName] is null, "Logout deletes the persistent owner cookie");
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Closing and reopening after logout does not restore sign-in");

    using (var response = await Login()) Check(response.IsSuccessStatusCode, "A fresh login works after logout");
    ownerCookie = OwnerCookie();
    owners.Owner.AuthenticationStamp = "changed-password-version";
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Changing the password revokes persistent and cached owner authentication");
    Check(cookies.GetCookies(cookieOrigin)[SalonOwnerPersistentSignIn.CookieName] is null, "A revoked ticket is deleted");

    using (var response = await Login()) Check(response.IsSuccessStatusCode, "New credentials can establish a fresh ticket");
    owners.Active = false;
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Inactive owners or salons cannot restore sign-in");
    owners.Active = true;

    using (var response = await Login()) Check(response.IsSuccessStatusCode, "A reactivated owner can sign in again");
    ownerCookie = OwnerCookie();
    var tampered = ownerCookie[..20] + (ownerCookie[20] == 'A' ? "B" : "A") + ownerCookie[21..];
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session", explicitCookie: cookies.GetCookieHeader(cookieOrigin).Replace(ownerCookie, tampered)))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Tampered cookies cannot fall back to a stale session");

    using (var response = await Login()) Check(response.IsSuccessStatusCode, "Sign-in recovers after a rejected ticket");
    var options = app!.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(SalonOwnerPersistentSignIn.Scheme);
    var ticket = options.TicketDataFormat.Unprotect(OwnerCookie())!;
    ticket.Properties.IssuedUtc = DateTimeOffset.UtcNow.AddDays(-8);
    ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1);
    var expired = options.TicketDataFormat.Protect(ticket);
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session", explicitCookie: cookies.GetCookieHeader(cookieOrigin).Replace(OwnerCookie(), expired)))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Expired tickets require sign-in again");

    cookies = new CookieContainer();
    using (var response = await Send(HttpMethod.Post, "/test/legacy-session")) Check(response.IsSuccessStatusCode, "The fixture can represent an older temporary owner session");
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session", explicitCookie: ""))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "The old session-only sign-in is lost when Android removes temporary cookies");
    using (var response = await Send(HttpMethod.Get, "/api/Admin/session"))
        Check(response.StatusCode == HttpStatusCode.OK, "Existing valid temporary owner sessions can be upgraded");
    Check(cookies.GetCookies(cookieOrigin)[SalonOwnerPersistentSignIn.CookieName]?.Expires > DateTime.UtcNow.AddDays(6), "Legacy restoration issues the new persistent cookie");

    Console.WriteLine($"All {checks} owner sign-in checks passed. No live accounts or databases were accessed.");
}
finally
{
    await Stop();
    var resolved = Path.GetFullPath(fixtureDirectory);
    if (Path.GetDirectoryName(resolved) == baseDirectory.TrimEnd(Path.DirectorySeparatorChar)
        && Path.GetFileName(resolved).StartsWith("owner-auth-", StringComparison.Ordinal)
        && Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
}

sealed class FixtureUnavailableException : Exception;

sealed class FixtureOwners : ISalonOwnerAuthenticationService
{
    public bool Active { get; set; } = true;
    public bool Unavailable { get; set; }
    public SalonOwnerLoginResponse Owner { get; } = new()
    {
        SalonOwnerID = Guid.NewGuid(), SalonMasterID = Guid.NewGuid(), OwnerName = "Fixture Owner",
        UserName = "fixture-owner", SalonName = "Fixture Salon", AuthenticationStamp = "fixture-password-version",
    };

    public Task<SalonOwnerLoginResponse?> AuthenticateAsync(SalonOwnerLoginRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Active && request.UserName == "fixture-owner" && request.Password == "fixture-password" ? Owner : null);

    public Task<SalonOwnerLoginResponse?> GetActiveSessionOwnerAsync(Guid salonOwnerId, CancellationToken cancellationToken)
    {
        if (Unavailable) throw new FixtureUnavailableException();
        return Task.FromResult(Active && salonOwnerId == Owner.SalonOwnerID ? Owner : null);
    }

    public Task<SalonOwnerLoginResponse> InitializeAsync(InitializeSalonOwnerRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Account creation is outside this fixture.");
}
