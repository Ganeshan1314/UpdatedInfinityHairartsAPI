using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InfinityHairartsAPI.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/AppUpdates")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AppUpdatesController(IConfiguration configuration, IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet("salonexa-admin/android")]
    public IActionResult GetAndroidAdminPolicy()
    {
        // Ship the initial release policy with the API. Server settings can override
        // it, and editing this small JSON file takes effect without restarting IIS.
        try
        {
            var configuredSettings = configuration.GetSection("AdminAppUpdates:Android");
            if (configuredSettings.Exists()) return ReadPolicy(configuredSettings);
            var policyConfiguration = new ConfigurationBuilder()
                .SetBasePath(environment.ContentRootPath)
                .AddJsonFile("admin-app-updates.json", optional: true, reloadOnChange: false)
                .Build();
            using var policyLifetime = policyConfiguration as IDisposable;
            return ReadPolicy(policyConfiguration.GetSection("AdminAppUpdates:Android"));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            return PolicyUnavailable();
        }
    }

    private IActionResult ReadPolicy(IConfigurationSection settings)
    {
        var version = settings["LatestVersion"]?.Trim();
        if (!int.TryParse(settings["LatestVersionCode"], NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            || code < 1 || string.IsNullOrEmpty(version) || version.Length > 60)
        {
            return PolicyUnavailable();
        }

        return Ok(new {
            applicationId = "com.salonexa.admin",
            latestVersionCode = code,
            latestVersion = version
        });
    }

    private ObjectResult PolicyUnavailable() => StatusCode(StatusCodes.Status503ServiceUnavailable,
        new { message = "The admin app update policy is not configured." });
}
