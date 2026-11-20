using System.Collections.Concurrent;

namespace InfinityHairartsAPI.Services;

/// <summary>
/// Keeps the public service catalog in memory so a slow or briefly unreachable
/// database does not make every customer wait. A saved copy is returned at once;
/// when it is older than <see cref="Fresh"/> one background refresh replaces it.
/// Only successful loads are stored, so an error is never served from the cache.
/// </summary>
public static class PublicCatalogCache
{
    private sealed record Entry(object Value, DateTime LoadedAtUtc);

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly ConcurrentDictionary<string, byte> Refreshing = new();
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public static async Task<T> GetOrLoadAsync<T>(string key, Func<Task<T>> load) where T : class
    {
        if (Entries.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.LoadedAtUtc < MaxAge)
        {
            if (DateTime.UtcNow - entry.LoadedAtUtc >= Fresh && Refreshing.TryAdd(key, 0))
            {
                _ = Task.Run(async () =>
                {
                    try { Entries[key] = new Entry(await load(), DateTime.UtcNow); }
                    catch { /* Keep serving the saved copy; the next request tries again. */ }
                    finally { Refreshing.TryRemove(key, out _); }
                });
            }
            return (T)entry.Value;
        }

        var result = await load();
        Entries[key] = new Entry(result, DateTime.UtcNow);
        return result;
    }

    public static T GetOrLoad<T>(string key, Func<T> load, Func<T, bool> isSuccessful) where T : class
    {
        if (Entries.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.LoadedAtUtc < MaxAge)
        {
            if (DateTime.UtcNow - entry.LoadedAtUtc >= Fresh && Refreshing.TryAdd(key, 0))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var refreshed = load();
                        if (isSuccessful(refreshed)) Entries[key] = new Entry(refreshed, DateTime.UtcNow);
                    }
                    catch
                    {
                        // Keep serving the saved copy; the next request tries again.
                    }
                    finally
                    {
                        Refreshing.TryRemove(key, out _);
                    }
                });
            }
            return (T)entry.Value;
        }

        // Nothing saved yet (first request after a restart): load it directly.
        var result = load();
        if (isSuccessful(result)) Entries[key] = new Entry(result, DateTime.UtcNow);
        return result;
    }
}
