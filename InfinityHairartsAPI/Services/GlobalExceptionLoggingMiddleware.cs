using System.Diagnostics;

namespace InfinityHairartsAPI.Services;

public sealed class GlobalExceptionLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionLoggingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionLoggingMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionLoggingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var exceptionLogged = false;

        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            exceptionLogged = true;
            _logger.LogError(
                exception,
                "Unhandled exception for {Method} {Path}. TraceId: {TraceId}",
                context.Request.Method,
                context.Request.Path.Value,
                context.TraceIdentifier);

            if (_environment.IsDevelopment() || context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                message = "An unexpected server error occurred.",
                traceId = context.TraceIdentifier
            });
        }
        finally
        {
            stopwatch.Stop();
            if (!exceptionLogged && context.Response.StatusCode >= StatusCodes.Status400BadRequest)
            {
                _logger.LogWarning(
                    "HTTP {StatusCode} for {Method} {Path} in {ElapsedMilliseconds} ms. TraceId: {TraceId}",
                    context.Response.StatusCode,
                    context.Request.Method,
                    context.Request.Path.Value,
                    stopwatch.ElapsedMilliseconds,
                    context.TraceIdentifier);
            }
        }
    }
}
