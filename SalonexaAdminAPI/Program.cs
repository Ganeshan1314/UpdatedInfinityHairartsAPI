using Microsoft.Extensions.FileProviders;
using SalonexaAdminAPI.Services;

// Salonexa Admin API: the host for the salon-owner / super-admin app only.
// It is published, configured and restarted independently of the customer InfinityHairartsAPI host.
var builder = WebApplication.CreateBuilder(args);

if (builder.Configuration.GetValue("FileLogging:Enabled", true))
{
    DailyFileLogWriter.Configure(builder.Configuration);
    builder.Logging.AddProvider(new DailyFileLoggerProvider(builder.Configuration));

    AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
    {
        if (eventArgs.ExceptionObject is Exception exception)
        {
            DailyFileLogWriter.Write(LogLevel.Critical, "SalonexaAdminAPI.Process", default,
                "An unhandled process exception terminated the admin API.", exception);
        }
    };

    TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
    {
        DailyFileLogWriter.Write(LogLevel.Error, "SalonexaAdminAPI.BackgroundTask", default,
            "An unobserved background task exception occurred.", eventArgs.Exception);
    };
}

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddClientErrorReporting();

// CORS: the Capacitor/Ionic admin app. Extra origins (for example a hosted web build or
// http://localhost:4200 for `ng serve`) come from Cors:AllowedOrigins.
var allowedOrigins = new[]
    {
        "http://localhost:8100",
        "http://localhost",
        "https://localhost",
        "capacitor://localhost",
        "ionic://localhost",
    }
    .Concat(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAdminApp", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10))
        .AllowCredentials());
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddLocationCatalog();
builder.Services.AddScoped<SalonOwnerAuthenticationService>();
builder.Services.AddScoped<ISalonOwnerAuthenticationService>(services => services.GetRequiredService<SalonOwnerAuthenticationService>());
builder.Services.AddSalonOwnerPersistentSignIn(builder.Configuration, builder.Environment);
SuperAdminAuthentication.Register(builder.Services);
builder.Services.AddScoped<SalonOwnerDashboardService>();
builder.Services.AddScoped<SalonOwnerServicesReader>();
builder.Services.AddScoped<SalonOwnerServicesWriter>();
builder.Services.AddScoped<SalonOwnerAppointmentsReader>();
builder.Services.AddScoped<BookingQrService>();
builder.Services.AddScoped<SalonTimingAvailability>();
builder.Services.AddScoped<SalonOwnerSalonProfileService>();
builder.Services.AddScoped<SalonOwnerRegistrationService>();
builder.Services.AddSingleton<SalonImageStore>();
SalonOwnerRegistrationService.AddOwnerSignupRateLimit(builder.Services);
builder.Services.AddSingleton<FirebasePushNotificationSender>();
builder.Services.AddSingleton<OwnerBookingNotificationStore>();
builder.Services.AddSingleton<IOwnerBookingPushSender>(services => services.GetRequiredService<FirebasePushNotificationSender>());
// Owner booking alerts are claimed from a leased database queue, so this worker can safely run
// beside the one still in the customer host while both are deployed.
if (builder.Configuration.GetValue("OwnerNotifications:PushWorkerEnabled", true))
{
    builder.Services.AddHostedService<OwnerBookingPushWorker>();
}

builder.Services.AddDistributedMemoryCache();
builder.Services.AddMemoryCache();

var sessionIdleTimeoutMinutes = builder.Configuration.GetValue<int?>("Session:IdleTimeoutMinutes") ?? 30;
if (sessionIdleTimeoutMinutes <= 0)
{
    throw new InvalidOperationException("Session:IdleTimeoutMinutes must be greater than zero.");
}

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(sessionIdleTimeoutMinutes);
    // A distinct cookie name keeps this host's session apart from the customer API's session.
    options.Cookie.Name = ".Salonexa.Admin.Session";
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

app.UseServiceImages();
SalonImageStore.UseSalonImages(app);

app.UseRouting();

app.UseCors("AllowAdminApp");
app.UseRateLimiter();
app.UseMiddleware<SuperAdminRequestMiddleware>();

app.UseSession();
app.UseMiddleware<SalonOwnerSessionMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();
