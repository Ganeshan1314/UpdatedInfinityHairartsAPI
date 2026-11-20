using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SalonexaAdminAPI.Services;

public sealed class SalonTimingSlot
{
    public Guid TimeAllocationID { get; set; }
    public string FullTiming { get; set; } = "";
    public bool IsAvailable { get; set; }
    // False when the platform has switched this time off (BookingAvailableStatus = 0): shown, but not selectable.
    public bool IsEnabledByPlatform { get; set; } = true;
}
/// <param name="Date">yyyy-MM-dd for a single-date schedule; null for the daily (every day) schedule.</param>
/// <param name="IsCustomised">True when that date has its own times; false when it falls back to the daily schedule.</param>
public sealed record SalonTimingSchedule(Guid SalonMasterID, long Version, IReadOnlyList<SalonTimingSlot> Slots,
    string? Date = null, bool IsCustomised = false);
public sealed class SalonTimingConflict(string message) : Exception(message);

public sealed class SalonTimingAvailability(IConfiguration configuration)
{
    // Share the existing reservation lock so disabling a time and confirming a booking cannot race.
    private const string ReservationLock = "Salonexa.TimeReservations.v1";
    // Owners may prepare times this many days ahead (customers currently see a shorter window).
    public const int MaxDaysAhead = 30;
    private SqlConnection Connection() => new(configuration.GetConnectionString("dbconnection")
        ?? throw new InvalidOperationException("The dbconnection connection string is required."));
    private static DateTime Today => SalonClock.Now.Date;

    private static void RequireValidDate(DateTime? date)
    {
        if (date is { } d && (d.Date < Today || d.Date > Today.AddDays(MaxDaysAhead)))
            throw new ArgumentException($"Choose a date from today to {MaxDaysAhead} days ahead.");
    }

    /// <summary>Reads the daily schedule, or the schedule for one date (its own times, else the daily ones).</summary>
    public async Task<SalonTimingSchedule> GetAsync(Guid ownerId, Guid salonId, CancellationToken cancellationToken,
        DateTime? date = null)
    {
        RequireValidDate(date);
        await using var db = Connection();
        await db.OpenAsync(cancellationToken);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        await RequireOwner(db, tx, ownerId, salonId, cancellationToken);
        var result = await Read(db, tx, salonId, date?.Date, cancellationToken);
        tx.Commit();
        return result;
    }

    /// <summary>Saves the daily schedule, or gives one date its own times (an empty list closes that date).</summary>
    public async Task<SalonTimingSchedule> SaveAsync(Guid ownerId, Guid salonId, long expectedVersion, Guid[] timeIds,
        CancellationToken cancellationToken, DateTime? date = null)
    {
        if (expectedVersion < 0 || timeIds.Length > 200 || timeIds.Any(id => id == Guid.Empty) || timeIds.Distinct().Count() != timeIds.Length)
            throw new ArgumentException("Choose valid, distinct appointment times.");
        RequireValidDate(date);
        var day = date?.Date;
        await using var db = Connection();
        await db.OpenAsync(cancellationToken);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        await LockAndCheck(db, tx, ownerId, salonId, expectedVersion, cancellationToken);
        if (timeIds.Length > 0)
        {
            var count = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM dbo.TimeAllocation WHERE TimeAllocationID IN @timeIds
                    AND ISNULL(IsDeleted,0)=0 AND BookingAvailableStatus=1
                """, new { timeIds }, tx, cancellationToken: cancellationToken));
            if (count != timeIds.Length) throw new SalonTimingConflict("Some appointment times are no longer available. Refresh before saving.");
        }
        await BumpVersion(db, tx, ownerId, salonId, cancellationToken);
        if (day is null)
        {
            await db.ExecuteAsync(new CommandDefinition("DELETE dbo.SalonBookableTime WHERE SalonMasterID=@salonId", new { salonId }, tx, cancellationToken: cancellationToken));
            foreach (var timeId in timeIds)
                await db.ExecuteAsync(new CommandDefinition("INSERT dbo.SalonBookableTime(SalonMasterID, TimeAllocationID) VALUES(@salonId,@timeId)",
                    new { salonId, timeId }, tx, cancellationToken: cancellationToken));
        }
        else
        {
            // Replace the date's override; deleting the override row also clears its times (ON DELETE CASCADE).
            await db.ExecuteAsync(new CommandDefinition("DELETE dbo.SalonTimingDateOverride WHERE SalonMasterID=@salonId AND BookingDate=@day",
                new { salonId, day }, tx, cancellationToken: cancellationToken));
            await db.ExecuteAsync(new CommandDefinition("INSERT dbo.SalonTimingDateOverride(SalonMasterID, BookingDate, UpdatedByOwnerID) VALUES(@salonId,@day,@ownerId)",
                new { salonId, day, ownerId }, tx, cancellationToken: cancellationToken));
            foreach (var timeId in timeIds)
                await db.ExecuteAsync(new CommandDefinition("INSERT dbo.SalonBookableTimeDate(SalonMasterID, BookingDate, TimeAllocationID) VALUES(@salonId,@day,@timeId)",
                    new { salonId, day, timeId }, tx, cancellationToken: cancellationToken));
        }
        var result = await Read(db, tx, salonId, day, cancellationToken);
        tx.Commit();
        return result;
    }

    /// <summary>Removes a date's own times so it follows the daily schedule again.</summary>
    public async Task<SalonTimingSchedule> ResetDateAsync(Guid ownerId, Guid salonId, long expectedVersion, DateTime date,
        CancellationToken cancellationToken)
    {
        if (expectedVersion < 0) throw new ArgumentException("Choose valid, distinct appointment times.");
        RequireValidDate(date);
        var day = date.Date;
        await using var db = Connection();
        await db.OpenAsync(cancellationToken);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        await LockAndCheck(db, tx, ownerId, salonId, expectedVersion, cancellationToken);
        await BumpVersion(db, tx, ownerId, salonId, cancellationToken);
        await db.ExecuteAsync(new CommandDefinition("DELETE dbo.SalonTimingDateOverride WHERE SalonMasterID=@salonId AND BookingDate=@day",
            new { salonId, day }, tx, cancellationToken: cancellationToken));
        var result = await Read(db, tx, salonId, day, cancellationToken);
        tx.Commit();
        return result;
    }

    private static async Task LockAndCheck(SqlConnection db, SqlTransaction tx, Guid ownerId, Guid salonId, long expectedVersion,
        CancellationToken cancellationToken)
    {
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
    }

    private static Task<int> BumpVersion(SqlConnection db, SqlTransaction tx, Guid ownerId, Guid salonId, CancellationToken cancellationToken) =>
        db.ExecuteAsync(new CommandDefinition("""
            IF EXISTS(SELECT 1 FROM dbo.SalonTimingAvailability WHERE SalonMasterID=@salonId)
                UPDATE dbo.SalonTimingAvailability SET Version=Version+1, UpdatedByOwnerID=@ownerId, UpdatedUtc=SYSUTCDATETIME() WHERE SalonMasterID=@salonId
            ELSE INSERT dbo.SalonTimingAvailability(SalonMasterID, Version, UpdatedByOwnerID) VALUES(@salonId,1,@ownerId)
            """, new { ownerId, salonId }, tx, cancellationToken: cancellationToken));

    private static async Task RequireOwner(SqlConnection db, SqlTransaction tx, Guid ownerId, Guid salonId, CancellationToken cancellationToken)
    {
        if (await db.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dbo.SalonOwner o JOIN dbo.SalonMaster s ON s.SalonMasterID=o.SalonMasterID
            WHERE o.SalonOwnerID=@ownerId AND o.SalonMasterID=@salonId AND o.IsActive=1 AND s.IsActive=1
            """, new { ownerId, salonId }, tx, cancellationToken: cancellationToken)) != 1)
            throw new UnauthorizedAccessException("Please sign in as an active salon owner.");
    }

    private static async Task<SalonTimingSchedule> Read(SqlConnection db, SqlTransaction tx, Guid salonId, DateTime? day,
        CancellationToken cancellationToken)
    {
        var version = await db.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT Version FROM dbo.SalonTimingAvailability WHERE SalonMasterID=@salonId", new { salonId }, tx, cancellationToken: cancellationToken));
        var customised = day.HasValue && await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.SalonTimingDateOverride WHERE SalonMasterID=@salonId AND BookingDate=@day",
            new { salonId, day }, tx, cancellationToken: cancellationToken)) > 0;
        // A customised date uses only its own times; any other date (and the daily view) uses the daily times.
        var slots = await db.QueryAsync<SalonTimingSlot>(new CommandDefinition("""
            SELECT t.TimeAllocationID, CONVERT(varchar(8),t.FullTiming,108) FullTiming,
                CONVERT(bit,CASE WHEN ISNULL(t.BookingAvailableStatus,0)=1
                    AND ((@customised=1 AND d.TimeAllocationID IS NOT NULL) OR (@customised=0 AND a.TimeAllocationID IS NOT NULL))
                    THEN 1 ELSE 0 END) IsAvailable,
                CONVERT(bit,ISNULL(t.BookingAvailableStatus,0)) IsEnabledByPlatform
            FROM dbo.TimeAllocation t
            LEFT JOIN dbo.SalonBookableTime a ON a.TimeAllocationID=t.TimeAllocationID AND a.SalonMasterID=@salonId
            LEFT JOIN dbo.SalonBookableTimeDate d ON d.TimeAllocationID=t.TimeAllocationID AND d.SalonMasterID=@salonId AND d.BookingDate=@day
            WHERE ISNULL(t.IsDeleted,0)=0
            ORDER BY t.FullTiming,t.TimeAllocationID
            """, new { salonId, day, customised }, tx, cancellationToken: cancellationToken));
        return new(salonId, version ?? 0, slots.ToList(), day?.ToString("yyyy-MM-dd"), customised);
    }

    // Called inside the customer's existing reservation transaction and lock.
    // A date with its own times uses only those; otherwise the salon's daily times apply.
    public static bool IsTimeAllowed(IDbConnection db, IDbTransaction tx, Guid? salonId, Guid timeId, DateTime bookingDate) =>
        db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.SalonMaster s
            JOIN dbo.TimeAllocation t ON t.TimeAllocationID=@timeId AND ISNULL(t.IsDeleted,0)=0 AND t.BookingAvailableStatus=1
            WHERE s.SalonMasterID=@salonId AND s.IsActive=1
              AND (CASE WHEN EXISTS(SELECT 1 FROM dbo.SalonTimingDateOverride o WHERE o.SalonMasterID=@salonId AND o.BookingDate=@day)
                        THEN (SELECT COUNT(*) FROM dbo.SalonBookableTimeDate d WHERE d.SalonMasterID=@salonId AND d.BookingDate=@day AND d.TimeAllocationID=@timeId)
                        ELSE (SELECT COUNT(*) FROM dbo.SalonBookableTime a WHERE a.SalonMasterID=@salonId AND a.TimeAllocationID=@timeId)
                   END)=1
            """, new { salonId, timeId, day = bookingDate.Date }, tx) == 1;
}
