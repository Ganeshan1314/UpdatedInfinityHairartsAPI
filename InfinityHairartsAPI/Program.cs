using InfinityHairartsAPI.Services;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

if (builder.Configuration.GetValue("FileLogging:Enabled", true))
{
    DailyFileLogWriter.Configure(builder.Configuration);
    builder.Logging.AddProvider(new DailyFileLoggerProvider(builder.Configuration));

    AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
    {
        if (eventArgs.ExceptionObject is Exception exception)
        {
            DailyFileLogWriter.Write(
                LogLevel.Critical,
                "InfinityHairartsAPI.Process",
                default,
                "An unhandled process exception terminated the API.",
                exception);
        }
    };

    TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
    {
        DailyFileLogWriter.Write(
            LogLevel.Error,
            "InfinityHairartsAPI.BackgroundTask",
            default,
            "An unobserved background task exception occurred.",
            eventArgs.Exception);
    };
}

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
builder.Services.AddScoped<LocationService>();
builder.Services.AddScoped<SalonOwnerAuthenticationService>();
builder.Services.AddScoped<SalonOwnerDashboardService>();
builder.Services.AddScoped<BookingQrService>();
builder.Services.Configure<BookingReminderOptions>(builder.Configuration.GetSection("Notifications"));
builder.Services.AddSingleton<NotificationRepository>();
builder.Services.AddSingleton<FirebasePushNotificationSender>();
builder.Services.AddHostedService<BookingReminderWorker>();

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
    options.Cookie.SameSite = builder.Environment.IsDevelopment()
        ? SameSiteMode.Lax
        : SameSiteMode.None;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseMiddleware<GlobalExceptionLoggingMiddleware>();

app.UseSwagger();
app.UseSwaggerUI();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

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
