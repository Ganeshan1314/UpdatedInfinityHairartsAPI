using InfinityHairartsAPI.Services;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowIonic", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:8100",
                "http://localhost",
                "https://localhost",
                "capacitor://localhost",
                "ionic://localhost",
                "https://infinityhairarts.com",
                "https://www.infinityhairarts.com"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddScoped<LoginService>();

builder.Services.AddDistributedMemoryCache();

var sessionIdleTimeoutMinutes = builder.Configuration.GetValue<int?>("Session:IdleTimeoutMinutes") ?? 30;
if (sessionIdleTimeoutMinutes <= 0)
{
    throw new InvalidOperationException("Session:IdleTimeoutMinutes must be greater than zero.");
}

builder.Services.AddSession(options =>
{
    // Server-side, sliding expiration. The session data is removed after the
    // configured period of inactivity, even if the browser still has a cookie.
    options.IdleTimeout = TimeSpan.FromMinutes(sessionIdleTimeoutMinutes);
    options.Cookie.Name = ".InfinityHairArts.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(app.Environment.ContentRootPath, "CustomerImages")),
    RequestPath = "/CustomerImages"
});

app.UseRouting();

app.UseCors("AllowIonic");

app.UseSession();

app.UseAuthorization();

app.MapControllers();

app.Run();
