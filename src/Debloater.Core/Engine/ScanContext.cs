using System.Collections.Concurrent;
using Debloater.Core.Platform;

namespace Debloater.Core.Engine;

/// <summary>
/// Contexto de uma varredura/operação. Guarda em cache enumerações caras (lista de pacotes appx,
/// lista de recursos opcionais, conexão com o Agendador de Tarefas) para que sejam feitas uma única
/// vez por varredura, e não uma vez por tweak.
/// </summary>
public sealed class ScanContext : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _cache = new();

    public ScanContext(SystemInfo system) => System = system;

    public SystemInfo System { get; }

    /// <summary>Obtém (ou calcula uma única vez) um valor em cache para esta varredura.</summary>
    public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        var lazy = _cache.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await factory().ConfigureAwait(false)));
        return (T)await lazy.Value.ConfigureAwait(false);
    }

    /// <summary>Descarta um valor em cache (ex.: após aplicar algo que o altera).</summary>
    public void Invalidate(string key) => _cache.TryRemove(key, out _);

    public void Dispose() => _cache.Clear();
}
