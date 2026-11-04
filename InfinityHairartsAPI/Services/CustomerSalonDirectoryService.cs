using System.Data;
using Dapper;
using InfinityHairartsAPI.Modals;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public interface ICustomerSalonDirectory
{
    Task<CustomerSalonPage> GetAsync(CustomerSalonQuery query, CustomerSalonCursor? after, CancellationToken cancellationToken);
}

public sealed class CustomerSalonDirectoryService(IConfiguration configuration) : ICustomerSalonDirectory
{
    public async Task<CustomerSalonPage> GetAsync(CustomerSalonQuery query, CustomerSalonCursor? after,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(configuration.GetConnectionString("dbconnection")
            ?? throw new InvalidOperationException("The dbconnection connection string is not configured."));
        var search = (query.Search ?? "").Trim();
        var rows = (await connection.QueryAsync<CustomerSalonCard>(new CommandDefinition(
            "dbo.getCustomerSalons", new {
                Search = search, query.PageSize, AfterDisplayOrder = after?.DisplayOrder,
                AfterSalonName = after?.SalonName, AfterSalonID = after?.SalonMasterID
            }, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken))).ToList();
        var hasMore = rows.Count > query.PageSize;
        var items = rows.Take(query.PageSize).ToList();
        var last = items.LastOrDefault();
        var cursor = hasMore && last is not null
            ? new CustomerSalonCursor(last.DisplayOrder, last.SalonName, last.SalonMasterID, CustomerSalonCursor.Fingerprint(search)).Encode()
            : null;
        return new(items, cursor);
    }
}
