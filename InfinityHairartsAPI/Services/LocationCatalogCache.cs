using InfinityHairartsAPI.Modals;
using Microsoft.Extensions.Caching.Memory;

namespace InfinityHairartsAPI.Services;

public sealed record LocationCatalogResult(IReadOnlyList<LocationMasterModal> Items, bool CacheHit);

// One short-lived snapshot per API process. There are currently no API routes
// that edit locations; direct SQL changes become visible after at most 2 minutes
// here, in addition to the application's existing device-cache lifetime.
public sealed class LocationCatalogCache(IMemoryCache cache) : IDisposable
{
    private readonly object _key = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public async Task<LocationCatalogResult> GetAsync(
        Func<CancellationToken, Task<IReadOnlyList<LocationMasterModal>>> load,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cache.TryGetValue(_key, out IReadOnlyList<LocationMasterModal>? items))
            return new(items!, true);

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            // Requests arriving together must not all query SQL on a cache miss.
            if (cache.TryGetValue(_key, out items)) return new(items!, true);
            items = await load(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            cache.Set(_key, items, TimeSpan.FromMinutes(2));
            return new(items, false);
        }
        finally { _refresh.Release(); }
    }

    public void Dispose() => _refresh.Dispose();
}

public static class LocationCatalogRegistration
{
    public static IServiceCollection AddLocationCatalog(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<LocationCatalogCache>();
        services.AddScoped<LocationService>();
        return services;
    }
}
