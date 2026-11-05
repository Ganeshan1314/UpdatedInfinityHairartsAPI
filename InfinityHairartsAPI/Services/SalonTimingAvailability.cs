using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace InfinityHairartsAPI.Services;

public sealed class SalonTimingSlot
{
    public Guid TimeAllocationID { get; set; }
    public string FullTiming { get; set; } = "";
    public bool IsAvailable { get; set; }
}
public sealed record SalonTimingSchedule(Guid SalonMasterID, long Version, IReadOnlyList<SalonTimingSlot> Slots);
public sealed class SalonTimingConflict(string message) : Exception(message);

public sealed class SalonTimingAvailability(IConfiguration configuration)
{
    // Share the existing reservation lock so disabling a time and confirming a booking cannot race.
    private const string ReservationLock = "Salonexa.TimeReservations.v1";
    private SqlConnection Connection() => new(configuration.GetConnectionString("dbconnection")
        ?? throw new InvalidOperationException("The dbconnection connection string is required."));

    public async Task<SalonTimingSchedule> GetAsync(Guid ownerId, Guid salonId, CancellationToken cancellationToken)
    {
        await using var db = Connection();
        await db.OpenAsync(cancellationToken);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        await RequireOwner(db, tx, ownerId, salonId, cancellationToken);
        var result = await Read(db, tx, salonId, cancellationToken);
        tx.Commit();
        return result;
    }

    public async Task<SalonTimingSchedule> SaveAsync(Guid ownerId, Guid salonId, long expectedVersion, Guid[] timeIds,
        CancellationToken cancellationToken)
    {
        if (expectedVersion < 0 || timeIds.Length > 200 || timeIds.Any(id => id == Guid.Empty) || timeIds.Distinct().Count() != timeIds.Length)
            throw new ArgumentException("Choose valid, distinct appointment times.");
        await using var db = Connection();
        await db.OpenAsync(cancellationToken);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        var locked = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000;
            SELECT @result;
            """, new { resource = ReservationLock }, tx, cancellationToken: cancellationToken));
        if (locked < 0) throw new SalonTimingConflict("Bookings are being updated. Please try saving again.");
        await RequireOwner(db, tx, ownerId, salonId, cancellationToken);
        var version = await db.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT Version FROM dbo.SalonTimingAvailability WITH(UPDLOCK,HOLDLOCK) WHERE SalonMasterID=@salonId",
            new { salonId }, tx, cancellationToken: cancellationToken));
        if ((version ?? 0) != expectedVersion) throw new SalonTimingConflict("Available timings changed on another device. Refresh before saving.");
        if (timeIds.Length > 0)
        {
            var count = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM dbo.TimeAllocation WHERE TimeAllocationID IN @timeIds
                    AND ISNULL(IsDeleted,0)=0 AND BookingAvailableStatus=1
                """, new { timeIds }, tx, cancellationToken: cancellationToken));
            if (count != timeIds.Length) throw new SalonTimingConflict("Some appointment times are no longer available. Refresh before saving.");
        }
        await db.ExecuteAsync(new CommandDefinition(version.HasValue
            ? "UPDATE dbo.SalonTimingAvailability SET Version=Version+1, UpdatedByOwnerID=@ownerId, UpdatedUtc=SYSUTCDATETIME() WHERE SalonMasterID=@salonId"
            : "INSERT dbo.SalonTimingAvailability(SalonMasterID, Version, UpdatedByOwnerID) VALUES(@salonId,1,@ownerId)",
            new { ownerId, salonId }, tx, cancellationToken: cancellationToken));
        await db.ExecuteAsync(new CommandDefinition("DELETE dbo.SalonBookableTime WHERE SalonMasterID=@salonId", new { salonId }, tx, cancellationToken: cancellationToken));
        foreach (var timeId in timeIds)
            await db.ExecuteAsync(new CommandDefinition("INSERT dbo.SalonBookableTime(SalonMasterID, TimeAllocationID) VALUES(@salonId,@timeId)",
                new { salonId, timeId }, tx, cancellationToken: cancellationToken));
        var result = await Read(db, tx, salonId, cancellationToken);
        tx.Commit();
        return result;
    }

    private static async Task RequireOwner(SqlConnection db, SqlTransaction tx, Guid ownerId, Guid salonId, CancellationToken cancellationToken)
    {
        if (await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID=o.SalonMasterID
            WHERE o.SalonOwnerID=@ownerId AND o.SalonMasterID=@salonId AND o.IsActive=1 AND s.IsActive=1
            """, new { ownerId, salonId }, tx, cancellationToken: cancellationToken)) != 1)
            throw new UnauthorizedAccessException("Please sign in as an active salon owner.");
    }

    private static async Task<SalonTimingSchedule> Read(SqlConnection db, SqlTransaction tx, Guid salonId, CancellationToken cancellationToken)
    {
        var version = await db.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT Version FROM dbo.SalonTimingAvailability WHERE SalonMasterID=@salonId", new { salonId }, tx, cancellationToken: cancellationToken));
        var slots = await db.QueryAsync<SalonTimingSlot>(new CommandDefinition("""
            SELECT t.TimeAllocationID, CONVERT(varchar(8),t.FullTiming,108) FullTiming,
                CONVERT(bit,CASE WHEN a.TimeAllocationID IS NULL THEN 0 ELSE 1 END) IsAvailable
            FROM dbo.TimeAllocation t LEFT JOIN dbo.SalonBookableTime a
                ON a.TimeAllocationID=t.TimeAllocationID AND a.SalonMasterID=@salonId
            WHERE ISNULL(t.IsDeleted,0)=0 AND t.BookingAvailableStatus=1
            ORDER BY t.FullTiming,t.TimeAllocationID
            """, new { salonId }, tx, cancellationToken: cancellationToken));
        return new(salonId, version ?? 0, slots.ToList());
    }

    // Called inside the customer's existing reservation transaction and lock.
    public static bool IsTimeAllowed(IDbConnection db, IDbTransaction tx, Guid? salonId, Guid timeId) =>
        db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.SalonBookableTime a
            JOIN dbo.SalonMaster s ON s.SalonMasterID=a.SalonMasterID AND s.IsActive=1
            JOIN dbo.TimeAllocation t ON t.TimeAllocationID=a.TimeAllocationID AND ISNULL(t.IsDeleted,0)=0 AND t.BookingAvailableStatus=1
            WHERE a.SalonMasterID=@salonId AND a.TimeAllocationID=@timeId
            """, new { salonId, timeId }, tx) == 1;
}
