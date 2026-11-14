using Dapper;
using SalonexaAdminAPI.Modals;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Diagnostics;

namespace SalonexaAdminAPI.Services;

public sealed class LocationService
{
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
}
