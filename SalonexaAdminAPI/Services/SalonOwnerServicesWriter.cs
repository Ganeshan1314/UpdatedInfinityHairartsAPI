using System.Data;
using Dapper;
using SalonexaAdminAPI.Modals;
using Microsoft.Data.SqlClient;

namespace SalonexaAdminAPI.Services;

/// <summary>
/// Creates and edits the signed-in salon's haircut menu items (dbo.SaveSalonOwnerService).
/// Owner and salon IDs always come from the restored owner ticket, never from the request.
/// </summary>
public sealed class SalonOwnerServicesWriter(
    IConfiguration configuration, IWebHostEnvironment environment,
    SalonOwnerServicesReader reader, ILogger<SalonOwnerServicesWriter> logger)
{
    private const int MaximumImageSize = 5 * 1024 * 1024;
    private const string ImageRoute = "/Images/HairCutItemListImages";

    public async Task<SalonOwnerServiceItem> SaveAsync(Guid ownerId, Guid salonId,
        Guid? serviceId, SalonOwnerServiceRequest request, CancellationToken cancellationToken)
    {
        // Authorize the owner and the existing record before writing an uploaded file.
        if (request.DurationMinutes != SalonOwnerServiceRequest.FixedDurationMinutes)
            throw new ArgumentException("Service duration is fixed at 30 minutes.");
        if (request.Price is not { } price || price != decimal.Round(price, 2))
            throw new ArgumentException("Enter a price with up to two decimal places.");
        if (string.IsNullOrWhiteSpace(request.ServiceName) || string.IsNullOrWhiteSpace(request.Category))
            throw new ArgumentException("Please check your service details.");
        if (!serviceId.HasValue && request.Image is null)
            throw new ArgumentException("A service image is required.");
        var services = await reader.GetAsync(ownerId, salonId, cancellationToken);
        if (serviceId.HasValue && !services.Any(service => service.SalonServiceID == serviceId && service.CanEdit))
            throw new KeyNotFoundException("Service not found in your salon.");

        string? imagePath = null;
        string? filePath = null;
        var commitStarted = false;
        try
        {
            if (request.Image is not null)
            {
                var (bytes, extension) = await ReadImage(request.Image, cancellationToken);
                var directory = ServiceImageFiles.ImageDirectory(configuration, environment);
                Directory.CreateDirectory(directory);
                // Uploaded filenames are never used as filesystem paths.
                var filename = Guid.NewGuid().ToString("N") + extension;
                filePath = Path.Combine(directory, filename);
                imagePath = $"{ImageRoute}/{filename}";
                await using var file = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.WriteAsync(bytes, cancellationToken);
            }

            await using var connection = new SqlConnection(configuration.GetConnectionString("dbconnection")
                ?? throw new InvalidOperationException("The dbconnection connection string is not configured."));
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var result = await connection.QuerySingleAsync<SalonOwnerServiceItem>(new CommandDefinition(
                "dbo.SaveSalonOwnerService", new
                {
                    OwnerID = ownerId, SalonID = salonId, ServiceID = serviceId ?? Guid.NewGuid(),
                    ServiceName = request.ServiceName.Trim(), Category = request.Category.Trim(),
                    Price = price, DurationMinutes = SalonOwnerServiceRequest.FixedDurationMinutes,
                    Description = request.Description?.Trim() ?? "", ImagePath = imagePath,
                    IsActive = request.IsActive!.Value, IsNew = !serviceId.HasValue
                }, transaction, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));
            // Keep the file if a connection loss makes the commit outcome uncertain.
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            if (!commitStarted && filePath is not null)
            {
                try { File.Delete(filePath); }
                catch (IOException exception) { logger.LogWarning(exception, "Could not remove an unused service image."); }
                catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Could not remove an unused service image."); }
            }
            throw;
        }
    }

    private static async Task<(byte[] Bytes, string Extension)> ReadImage(IFormFile image, CancellationToken cancellationToken)
    {
        if (image.Length is < 12 or > MaximumImageSize)
            throw new ArgumentException("Choose a JPG, PNG, or WebP image up to 5 MB.");
        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.Length is < 12 or > MaximumImageSize)
            throw new ArgumentException("Choose a JPG, PNG, or WebP image up to 5 MB.");
        // The declared type must match the file signature.
        var extension = image.ContentType.ToLowerInvariant() switch
        {
            "image/jpeg" when bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff }) => ".jpg",
            "image/png" when bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) => ".png",
            "image/webp" when bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) => ".webp",
            _ => throw new ArgumentException("Choose a valid JPG, PNG, or WebP image.")
        };
        return (bytes, extension);
    }
}
