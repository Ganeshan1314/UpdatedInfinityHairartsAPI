using Dapper;
using InfinityHairartsAPI.Modals;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace InfinityHairartsAPI.Services;

public sealed class LocationService
{
    private sealed record DirectoryCursor(string Search, string SalonName, Guid SalonMasterID);
    // These bytes are base64 encoded, never embedded as HTML. Keep Unicode names
    // compact enough for the app's 2048-character cursor limit.
    private static readonly JsonSerializerOptions CursorJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly string _connectionString;
    private readonly LocationCatalogCache _cache;
    private readonly ILogger<LocationService> _logger;

    public LocationService(IConfiguration configuration, LocationCatalogCache cache, ILogger<LocationService> logger)
    {
        _cache = cache;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("dbconnection")
            ?? throw new InvalidOperationException("The dbconnection connection string is not configured.");
    }

    public Task<LocationCatalogResult> GetLocationsAsync(CancellationToken cancellationToken = default)
        => _cache.GetAsync(LoadLocationsAsync, cancellationToken);

    private async Task<IReadOnlyList<LocationMasterModal>> LoadLocationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        var total = Stopwatch.StartNew();
        var stage = Stopwatch.StartNew();
        var opened = false;
        double openMs = 0;
        try
        {
            await connection.OpenAsync(cancellationToken);
            openMs = stage.Elapsed.TotalMilliseconds;
            opened = true;
            stage.Restart();
            var locations = await connection.QueryAsync<LocationMasterModal>(new CommandDefinition(
                "dbo.getActiveLocations", commandType: CommandType.StoredProcedure,
                commandTimeout: 5, cancellationToken: cancellationToken));
            return locations.ToList();
        }
        finally
        {
            if (!opened) openMs = stage.Elapsed.TotalMilliseconds;
            _logger.LogInformation(
                "Location SQL timing: OpenMs={OpenMs:F1} QueryMs={QueryMs:F1} TotalMs={TotalMs:F1} TraceId={TraceId}",
                openMs, opened ? stage.Elapsed.TotalMilliseconds : 0, total.Elapsed.TotalMilliseconds,
                Activity.Current?.TraceId.ToString());
        }
    }

    public async Task<IReadOnlyList<SalonMasterModal>> GetSalonsByLocationAsync(Guid locationMasterId)
    {
        await using var connection = new SqlConnection(_connectionString);
        var salons = await connection.QueryAsync<SalonMasterModal>(
            "dbo.getActiveSalonsByLocation",
            new { LocationMasterID = locationMasterId },
            commandType: CommandType.StoredProcedure);
        return salons.ToList();
    }

    public async Task<SalonDirectoryPage> GetDirectoryAsync(
        string? search, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var term = (search ?? "").Trim();
        if (term.Length > 100 || pageSize is < 1 or > 20)
            throw new ArgumentException("Search must be at most 100 characters and pageSize must be between 1 and 20.");

        DirectoryCursor? after = null;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length is < 1 or > 2048) throw new FormatException();
                after = JsonSerializer.Deserialize<DirectoryCursor>(Convert.FromBase64String(cursor));
                if (after is null || after.Search != term || string.IsNullOrWhiteSpace(after.SalonName)
                    || after.SalonName.Length > 200 || after.SalonMasterID == Guid.Empty)
                    throw new FormatException();
            }
            catch (Exception error) when (error is FormatException or JsonException)
            {
                throw new ArgumentException("Invalid salon directory cursor.", error);
            }
        }

        await using var connection = new SqlConnection(_connectionString);
        var rows = (await connection.QueryAsync<DirectorySalon>(new CommandDefinition(
            "dbo.GetCustomerSalonDirectory", new
            {
                Search = term,
                PageSize = pageSize,
                AfterSalonName = after?.SalonName,
                AfterSalonMasterID = after?.SalonMasterID
            }, commandType: CommandType.StoredProcedure, commandTimeout: 8,
            cancellationToken: cancellationToken))).AsList();
        var more = rows.Count > pageSize;
        var items = rows.Take(pageSize).ToList();
        var next = more ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new DirectoryCursor(term, items[^1].SalonName, items[^1].SalonMasterID), CursorJson)) : null;
        return new SalonDirectoryPage(items, next);
    }
}
