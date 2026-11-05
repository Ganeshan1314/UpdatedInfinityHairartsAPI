using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public sealed class SalonOwnerServicesReader(IConfiguration configuration)
{
    public async Task<IReadOnlyList<SalonOwnerServiceItem>> GetAsync(
        Guid ownerId, Guid salonId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(configuration.GetConnectionString("dbconnection")
            ?? throw new InvalidOperationException("The dbconnection connection string is not configured."));
        return (await connection.QueryAsync<SalonOwnerServiceItem>(new CommandDefinition(
            "dbo.GetSalonOwnerServices", new { OwnerID = ownerId, SalonID = salonId },
            commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken))).AsList();
    }
}

public sealed class SalonOwnerServiceItem
{
    public Guid SalonServiceID { get; set; }
    public string ServiceName { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public int? DurationMinutes { get; set; }
    public string Description { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public bool IsActive { get; set; }
    public bool CanEdit { get; set; }
}
