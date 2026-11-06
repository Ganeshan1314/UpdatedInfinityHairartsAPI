using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace InfinityHairartsAPI.Services;

public static class ServiceImageFiles
{
    private const string ImageRoute = "/Images/HairCutItemListImages";
    private const string PlaceholderName = "service-placeholder.svg";
    private const string Placeholder = """
        <svg xmlns="http://www.w3.org/2000/svg" width="480" height="320" viewBox="0 0 480 320" role="img" aria-labelledby="title">
          <title id="title">Service image unavailable</title>
          <rect width="480" height="320" rx="16" fill="#f3f4f6"/>
          <g fill="none" stroke="#9ca3af" stroke-width="8" stroke-linecap="round" stroke-linejoin="round">
            <rect x="180" y="90" width="120" height="100" rx="10"/>
            <circle cx="211" cy="118" r="9"/>
            <path d="m185 175 34-32 25 22 21-21 29 31"/>
          </g>
          <text x="240" y="239" text-anchor="middle" font-family="Arial,sans-serif" font-size="18" fill="#6b7280">Service image unavailable</text>
        </svg>
        """;

    public static WebApplication UseServiceImages(this WebApplication app)
    {
        var directory = Path.Combine(app.Environment.ContentRootPath, "Images", "HairCutItemListImages");
        Directory.CreateDirectory(directory);
        var files = new PhysicalFileProvider(directory);
        app.Lifetime.ApplicationStopped.Register(files.Dispose);
        var types = new FileExtensionContentTypeProvider(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
            [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif"
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files, RequestPath = ImageRoute, ContentTypeProvider = types
        });
        // Only missing GUID-named service images receive a fallback. Other missing
        // paths retain 404, and an uploaded original always takes precedence above.
        app.Use(async (context, next) =>
        {
            if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && context.Request.Path.StartsWithSegments(ImageRoute, StringComparison.OrdinalIgnoreCase, out var remaining))
            {
                var name = remaining.Value?.TrimStart('/') ?? "";
                if (name == PlaceholderName)
                {
                    context.Response.ContentType = "image/svg+xml; charset=utf-8";
                    context.Response.Headers.CacheControl = "public,max-age=3600";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    if (!HttpMethods.IsHead(context.Request.Method))
                        await context.Response.WriteAsync(Placeholder, context.RequestAborted);
                    return;
                }
                if (!name.Contains('/') && !name.Contains('\\')
                    && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "D", out _)
                    && types.TryGetContentType(name, out _))
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Redirect(context.Request.PathBase + ImageRoute + "/" + PlaceholderName);
                    return;
                }
            }
            await next(context);
        });
        return app;
    }
}
