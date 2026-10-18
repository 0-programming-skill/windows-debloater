using System.Collections.Concurrent;
using Debloater.Core.Platform;
using Debloater.Core.Platform.FeatureFlags;

namespace Debloater.Core.Engine;

/// <summary>
/// Context of a scan/operation. Caches expensive enumerations (appx package list,
/// optional feature list, Task Scheduler connection) so that they are done only once
/// per scan, not once per tweak.
/// </summary>
public sealed class ScanContext : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _cache = new();
    private readonly ConcurrentQueue<string> _notes = new();

    public ScanContext(SystemInfo system, IFeatureStore? featureStore = null)
    {
        System = system;
        FeatureStore = featureStore ?? NativeFeatureStore.Instance;
    }

    public SystemInfo System { get; }

    /// <summary>Windows feature configurations (feature flags); a fake one in tests.</summary>
    public IFeatureStore FeatureStore { get; }

    /// <summary>Gets (or computes only once) a cached value for this scan.</summary>
    public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        var lazy = _cache.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await factory().ConfigureAwait(false)));
        return (T)await lazy.Value.ConfigureAwait(false);
    }

    /// <summary>Discards a cached value (e.g. after applying something that changes it).</summary>
    public void Invalidate(string key) => _cache.TryRemove(key, out _);

    /// <summary>Notes recorded by the handlers during the operation (not errors).</summary>
    public IReadOnlyCollection<string> Notes => _notes;

    /// <summary>Records a note for the operation result (e.g. "restart for the service to stop").</summary>
    public void AddNote(string note) => _notes.Enqueue(note);

    public void Dispose() => _cache.Clear();
}
