using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
    ["Authentication:KeyRingPath"] = Path.Combine(AppContext.BaseDirectory, "test-keys"),
});
builder.Services.AddControllers().AddApplicationPart(typeof(SuperAdminController).Assembly);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Authentication:KeyRingPath"]!));
SuperAdminAuthentication.Register(builder.Services);
var accounts = new FixtureSuperAdminAccounts();
builder.Services.AddSingleton<ISuperAdminAccountStore>(accounts);
var fixture = new FixtureDashboard();
builder.Services.AddSingleton<ISuperAdminDashboard>(fixture);
builder.Services.AddAuthentication().AddCookie("FixtureSalonOwner", options => options.Cookie.Name = "Salonexa.Owner");
var app = builder.Build();
app.Use(async (context, next) => { context.Request.Scheme = "https"; await next(context); });
app.UseRouting();
app.UseRateLimiter();
app.UseMiddleware<SuperAdminRequestMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapPost("/fixture/owner", async (HttpContext context) => {
    await context.SignInAsync("FixtureSalonOwner", new ClaimsPrincipal(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, "salon-owner"), new Claim(ClaimTypes.Role, "SuperAdmin")
    ], "FixtureSalonOwner")));
    return Results.NoContent();
});
// Deliberately issue malformed platform tickets only inside this test host.
app.MapPost("/fixture/platform-ticket/{role}", async (string role, HttpContext context) => {
    var claims = new List<Claim> {
        new(ClaimTypes.NameIdentifier, accounts.Account!.SuperAdminID.ToString()),
        new(ClaimTypes.Name, accounts.Account.UserName),
        new(SuperAdminAuthentication.StampClaim, SuperAdminAuthentication.StampFor(accounts.Account))
    };
    if (role != "missing") claims.Add(new Claim(ClaimTypes.Role, role));
    await context.SignInAsync(SuperAdminAuthentication.Scheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, SuperAdminAuthentication.Scheme)));
    return Results.NoContent();
});
await app.StartAsync();
using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) {
    BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
};
var cookies = new CookieContainer();
var origin = new Uri("https://localhost");
var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, bool header = true)
{
    using var request = new HttpRequestMessage(method, path);
    request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(origin));
    if (header) request.Headers.Add("X-Salonexa-Request", "1");
    if (body is not null) request.Content = JsonContent.Create(body);
    var response = await client.SendAsync(request);
    if (response.Headers.TryGetValues("Set-Cookie", out var values)) foreach (var value in values) cookies.SetCookies(origin, value);
    return response;
}
var credentials = new { userName = "platform-owner", password = "fixture-password-123" };
try
{
    foreach (var path in new[] { "session", "dashboard", "salons" }) {
        using var response = await Send(HttpMethod.Get, "/api/SuperAdmin/" + path);
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous requests cannot read " + path);
        Check(response.Headers.CacheControl?.NoStore == true, path + " responses cannot be cached");
    }
    using (var response = await Send(HttpMethod.Put, $"/api/SuperAdmin/salons/{fixture.SalonId}/visibility", new { isVisibleToCustomers = true, expectedVisible = false }))
        Check(response.StatusCode == HttpStatusCode.Unauthorized && fixture.Writes == 0, "Anonymous visibility writes are rejected");
    using (var response = await Send(HttpMethod.Post, "/api/SuperAdmin/login", credentials, header: false))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Login requires the anti-CSRF request header");
    using (var response = await Send(HttpMethod.Post, "/api/SuperAdmin/login", new { userName = "platform-owner", password = "wrong" }))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Incorrect passwords cannot sign in");
    using (var response = await Send(HttpMethod.Post, "/fixture/owner")) Check(response.IsSuccessStatusCode, "A separate salon-owner ticket is issued for isolation checks");
    using (var response = await Send(HttpMethod.Get, "/api/SuperAdmin/dashboard"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "A salon-owner cookie with a forged role cannot access platform data");
    foreach (var role in new[] { "missing", "SalonOwner" }) {
        using (var response = await Send(HttpMethod.Post, "/fixture/platform-ticket/" + role))
            Check(response.IsSuccessStatusCode, "A malformed platform ticket can be issued by the test host: " + role);
        using (var response = await Send(HttpMethod.Put, $"/api/SuperAdmin/salons/{fixture.SalonId}/visibility", new { isVisibleToCustomers = true, expectedVisible = false }))
            Check(response.StatusCode == HttpStatusCode.Unauthorized && fixture.Writes == 0, "A platform ticket cannot approve salons without the SuperAdmin role: " + role);
        using (var response = await Send(HttpMethod.Get, "/api/SuperAdmin/session"))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "A rejected role cannot restore a platform session: " + role);
    }
    using (var response = await Send(HttpMethod.Post, "/api/SuperAdmin/login", new { credentials.userName, credentials.password, role = "SalonOwner" })) {
        Check(response.IsSuccessStatusCode, "The configured application owner can sign in");
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(SuperAdminAuthentication.CookieName + "="));
        Check(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("secure", StringComparison.OrdinalIgnoreCase)
            && cookie.Contains("samesite=none", StringComparison.OrdinalIgnoreCase), "The platform ticket is HTTP-only, secure and supports the app origin");
        var body = await response.Content.ReadAsStringAsync();
        Check(!body.Contains("password", StringComparison.OrdinalIgnoreCase) && !body.Contains("stamp", StringComparison.OrdinalIgnoreCase), "Sign-in does not expose credential material");
        using var session = System.Text.Json.JsonDocument.Parse(body);
        Check(session.RootElement.GetProperty("role").GetString() == SuperAdminAuthentication.Role,
            "The server assigns the SuperAdmin role independently of the requested role");
    }
    using (var response = await Send(HttpMethod.Get, "/api/SuperAdmin/session")) {
        Check(response.IsSuccessStatusCode, "Server-validated session restoration succeeds");
        using var session = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(session.RootElement.GetProperty("role").GetString() == SuperAdminAuthentication.Role,
            "Restored sessions carry the application-owner role");
    }
    using (var response = await Send(HttpMethod.Get, "/api/SuperAdmin/dashboard?fromDate=2026-10-01&toDate=2026-10-27")) {
        Check(response.IsSuccessStatusCode, "Authenticated overview requests succeed");
        Check(fixture.From == new DateOnly(2026, 10, 1) && fixture.To == new DateOnly(2026, 10, 27), "The complete date range reaches the data service");
    }
    foreach (var query in new[] { "fromDate=2026-10-28&toDate=2026-10-27", "fromDate=invalid", "fromDate=1999-12-31" })
        using (var response = await Send(HttpMethod.Get, "/api/SuperAdmin/dashboard?" + query)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid dates are rejected: " + query);
    using (var response = await Send(HttpMethod.Get, "/api/SuperAdmin/salons?search=test&status=pending&page=2"))
        Check(response.IsSuccessStatusCode && fixture.Search == "test" && fixture.Status == "pending" && fixture.Page == 2, "Salon search, filter and pagination reach the service");
    using (var response = await Send(HttpMethod.Get, "/api/SuperAdmin/salons?search=&status=all&page=1"))
        Check(response.IsSuccessStatusCode && fixture.Search == "", "An empty directory search loads all salons");
    foreach (var query in new[] { "status=bad", "page=0", "page=100001", "search=" + new string('x', 101) })
        using (var response = await Send(HttpMethod.Get, "/api/SuperAdmin/salons?" + query)) Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid directory input is rejected: " + query[..Math.Min(query.Length, 25)]);
    var visibilityPath = $"/api/SuperAdmin/salons/{fixture.SalonId}/visibility";
    using (var response = await Send(HttpMethod.Put, visibilityPath, new { isVisibleToCustomers = true, expectedVisible = false }, header: false))
        Check(response.StatusCode == HttpStatusCode.Forbidden && fixture.Writes == 0, "Authenticated writes also require CSRF protection");
    using (var response = await Send(HttpMethod.Put, visibilityPath, new { isVisibleToCustomers = true }))
        Check(response.StatusCode == HttpStatusCode.BadRequest && fixture.Writes == 0, "Visibility updates require an expected state");
    using (var response = await Send(HttpMethod.Put, visibilityPath, new { isVisibleToCustomers = true, expectedVisible = false, actor = "forged" }))
        Check(response.StatusCode == HttpStatusCode.NoContent && fixture.Writes == 1 && fixture.Actor == "platform-owner", "Approval records the authenticated actor rather than client input");
    using (var response = await Send(HttpMethod.Post, "/api/SuperAdmin/logout")) Check(response.StatusCode == HttpStatusCode.NoContent, "Sign out succeeds");
    using (var response = await Send(HttpMethod.Get, "/api/SuperAdmin/session")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "The signed-out browser cannot restore its session");
    using (var response = await Send(HttpMethod.Post, "/api/SuperAdmin/login", credentials)) Check(response.IsSuccessStatusCode, "The owner can sign in again");
    accounts.Account = accounts.Account! with { PasswordHash = new PasswordHasher<string>().HashPassword("platform-owner", "rotated-password-456") };
    using (var response = await Send(HttpMethod.Get, "/api/SuperAdmin/session")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Credential rotation revokes existing tickets");
    accounts.Account = null;
    using (var response = await Send(HttpMethod.Post, "/api/SuperAdmin/login", credentials)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "A missing database account cannot sign in");
    for (var attempt = 0; attempt < 3; attempt++) {
        using var response = await Send(HttpMethod.Post, "/api/SuperAdmin/login", credentials);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) { Check(true, "Repeated login attempts are rate limited"); break; }
        if (attempt == 2) throw new Exception("Rate limiter did not reject repeated attempts");
    }
    Console.WriteLine($"Passed {checks} super admin HTTP checks.");
    if (args.Contains("--database")) await DatabaseChecks.Run();
}
finally { await app.StopAsync(); await app.DisposeAsync(); }

sealed class FixtureDashboard : ISuperAdminDashboard
{
    public Guid SalonId { get; } = Guid.NewGuid();
    public int Writes; public string Actor = ""; public string Search = ""; public string Status = ""; public int Page;
    public DateOnly From; public DateOnly To;
    public Task<SuperAdminOverview> GetOverview(DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken) {
        From = fromDate; To = toDate;
        return Task.FromResult(new SuperAdminOverview(fromDate.ToString("yyyy-MM-dd"), toDate.ToString("yyyy-MM-dd"), new(1, 0, 1, 1, 0, 0, 0, 0), [], []));
    }
    public Task<SuperAdminSalonPage> GetSalons(string search, string status, int page, CancellationToken cancellationToken) {
        Search = search; Status = status; Page = page;
        return Task.FromResult(new SuperAdminSalonPage(0, page, 20, []));
    }
    public Task SetVisibility(Guid salonId, bool visible, bool expectedVisible, string actor, CancellationToken cancellationToken) {
        Writes++; Actor = actor; return Task.CompletedTask;
    }
}

sealed class FixtureSuperAdminAccounts : ISuperAdminAccountStore
{
    public SuperAdminAccount? Account = new(Guid.NewGuid(), "platform-owner", "Application owner",
        new PasswordHasher<string>().HashPassword("platform-owner", "fixture-password-123"), "SuperAdmin", true);
    public Task<SuperAdminAccount?> FindByUserNameAsync(string userName, CancellationToken cancellationToken) =>
        Task.FromResult(Account?.UserName == userName ? Account : null);
    public Task<SuperAdminAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Account?.SuperAdminID == id ? Account : null);
    public Task<bool> MarkLoginAsync(SuperAdminAccount account, CancellationToken cancellationToken) => Task.FromResult(Account == account);
}
