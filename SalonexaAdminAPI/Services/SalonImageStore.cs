using Microsoft.Extensions.FileProviders;

namespace SalonexaAdminAPI.Services;

public sealed record SavedSalonImage(string PublicPath, string FilePath);

/// <summary>
/// Saves the salon photo uploaded at signup under <c>/SalonImages/{salon}/{file}</c>. The folder defaults
/// to this host's own SalonImages folder; set SalonImages:Directory to share it with another host.
/// </summary>
public sealed class SalonImageStore(IConfiguration configuration, IHostEnvironment environment)
{
    public const int MaxImageBytes = 5 * 1024 * 1024;
    private const string Route = "/SalonImages";

    public static string ImageDirectory(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["SalonImages:Directory"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "SalonImages")
            : Path.GetFullPath(configured.Trim(), environment.ContentRootPath);
    }

    public async Task<SavedSalonImage> SaveAsync(Guid salonId, IFormFile image, CancellationToken cancellationToken)
    {
        if (image.Length <= 0 || image.Length > MaxImageBytes)
            throw new ArgumentException("Choose an image up to 5 MB.");
        await using var input = image.OpenReadStream();
        var header = new byte[12];
        var read = await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        var extension = DetectExtension(header.AsSpan(0, read))
            ?? throw new ArgumentException("Upload a JPG, PNG, or WebP image.");
        var contentType = extension switch { ".jpg" => "image/jpeg", ".png" => "image/png", _ => "image/webp" };
        if (!string.Equals(image.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The image format does not match its content type.");

        var directory = Path.Combine(ImageDirectory(configuration, environment), salonId.ToString("N"));
        Directory.CreateDirectory(directory);
        // Uploaded filenames are never used as filesystem paths.
        var filename = Guid.NewGuid().ToString("N") + extension;
        var filePath = Path.Combine(directory, filename);
        try
        {
            await using (var output = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await image.CopyToAsync(output, cancellationToken);
            return new SavedSalonImage($"{Route}/{salonId:N}/{filename}", filePath);
        }
        catch
        {
            File.Delete(filePath);
            throw;
        }
    }

    public void Delete(SavedSalonImage image)
    {
        try { File.Delete(image.FilePath); } catch (IOException) { /* An orphaned photo is harmless. */ }
    }

    public static WebApplication UseSalonImages(WebApplication app)
    {
        var directory = ImageDirectory(app.Configuration, app.Environment);
        Directory.CreateDirectory(directory);
        var files = new PhysicalFileProvider(directory);
        app.Lifetime.ApplicationStopped.Register(files.Dispose);
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, RequestPath = Route });
        return app;
    }

    private static string? DetectExtension(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ".jpg";
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }
}
