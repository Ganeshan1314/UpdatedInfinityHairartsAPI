using System.Data;
using Dapper;
using InfinityHairartsAPI.Modals;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public sealed class SalonOwnerServicesService(IConfiguration configuration, IWebHostEnvironment environment,
    ILogger<SalonOwnerServicesService> logger)
{
    private const int MaximumImageSize = 5 * 1024 * 1024;
    private readonly string connectionString = configuration.GetConnectionString("dbconnection")
        ?? throw new InvalidOperationException("The dbconnection connection string is not configured.");

    public async Task<IReadOnlyList<SalonOwnerDashboardCatalogueService>> GetAsync(
        Guid ownerId, Guid salonId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        return (await connection.QueryAsync<SalonOwnerDashboardCatalogueService>(new CommandDefinition(
            "dbo.GetSalonOwnerServices", new { OwnerID = ownerId, SalonID = salonId },
            commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<SalonOwnerDashboardCatalogueService> SaveAsync(Guid ownerId, Guid salonId,
        Guid? serviceId, SalonOwnerServiceRequest request, CancellationToken cancellationToken)
    {
        // Authorize the owner and existing record before writing an uploaded file.
        if (request.DurationMinutes != SalonOwnerServiceRequest.FixedDurationMinutes)
            throw new ArgumentException("Service duration is fixed at 30 minutes.");
        var services = await GetAsync(ownerId, salonId, cancellationToken);
        if (serviceId.HasValue && !services.Any(service => service.SalonServiceID == serviceId && service.CanEdit))
            throw new KeyNotFoundException("Service not found in your salon.");
        if (request.Price != decimal.Round(request.Price!.Value, 2))
            throw new ArgumentException("Enter a price with up to two decimal places.");
        if (!serviceId.HasValue && request.Image is null)
            throw new ArgumentException("A service image is required.");

        string? imagePath = null;
        string? filePath = null;
        var commitStarted = false;
        try
        {
            if (request.Image is not null)
            {
                var (bytes, extension) = await ReadImage(request.Image, cancellationToken);
                var directory = Path.Combine(environment.ContentRootPath, "Images", "HairCutItemListImages");
                Directory.CreateDirectory(directory);
                var filename = Guid.NewGuid().ToString("N") + extension;
                filePath = Path.Combine(directory, filename);
                imagePath = $"/Images/HairCutItemListImages/{filename}";
                await using var file = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.WriteAsync(bytes, cancellationToken);
            }

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var result = await connection.QuerySingleAsync<SalonOwnerDashboardCatalogueService>(new CommandDefinition(
                "dbo.SaveSalonOwnerService", new {
                    OwnerID = ownerId, SalonID = salonId, ServiceID = serviceId ?? Guid.NewGuid(),
                    ServiceName = request.ServiceName.Trim(), Category = request.Category.Trim(),
                    Price = request.Price.Value, DurationMinutes = SalonOwnerServiceRequest.FixedDurationMinutes,
                    Description = request.Description?.Trim() ?? "", ImagePath = imagePath,
                    IsActive = request.IsActive!.Value, IsNew = !serviceId.HasValue
                }, transaction, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));
            // Preserve the file if a connection loss makes the commit outcome uncertain.
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
        var extension = image.ContentType.ToLowerInvariant() switch {
            "image/jpeg" when bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff }) => ".jpg",
            "image/png" when bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) => ".png",
            "image/webp" when bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) => ".webp",
            _ => throw new ArgumentException("Choose a valid JPG, PNG, or WebP image.")
        };
        return (bytes, extension);
    }
}
