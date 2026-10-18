using Debloater.Core.Inventory;

namespace Debloater.Core.Fingerprint;

/// <summary>O que o filtro de privacidade tirou do diff (contagens por seção).</summary>
public sealed class FingerprintPrivacyInfo
{
    public const string Binaries = "binaries";
    public const string RemovedStrings = "removedStrings";
    public const string Policies = "policies";
    public const string OldBinaryReferences = "oldBinaryReferences";
    public static string Inventory(string kind) => $"inventory:{kind}";

    /// <summary>Itens omitidos por seção (chaves: <see cref="Binaries"/>, <see cref="Inventory"/>...).</summary>
    public SortedDictionary<string, int> Omitted { get; set; } = new(StringComparer.Ordinal);

    public int Get(string key) => Omitted.GetValueOrDefault(key);
    public int Total => Omitted.Values.Sum();

    internal void Add(string key, int n = 1)
    {
        if (n > 0) Omitted[key] = Get(key) + n;
    }
}

/// <summary>
/// Filtro de privacidade do diff (ligado por padrão no <c>fingerprint diff</c>): o relatório costuma ir
/// para um repositório público e um dos lados pode ser o PC pessoal de alguém. Tira o que revela o
/// software/hardware de uma máquina e mantém o que é do Windows:
/// <list type="bullet">
/// <item>itens de inventário marcados como ruído de ambiente (terceiros, hardware, frameworks, idiomas,
/// inicialização automática, SID/GUID), dos dois lados; ficam só os ruídos de elevação e de alvo do catálogo;</item>
/// <item>itens que só existem no lado antigo (removidos) sem evidência de serem do Windows: serviço/driver
/// cujo binário não existe no build novo, tarefa fora de <c>\Microsoft\Windows\</c>, appx que não é pacote
/// do sistema, capacidades (instaladas à parte);</item>
/// <item>binários removidos fora de <c>Windows\SystemApps</c>; cadeias que sumiram e referências a binários
/// antigos que não existem no build novo; políticas de arquivos ADMX que só existem no antigo (modelos
/// instalados à parte);</item>
/// <item>o nome das máquinas.</item>
/// </list>
/// As quantidades omitidas ficam em <see cref="FingerprintDiffResult.Privacy"/> e aparecem no relatório.
/// </summary>
public static class FingerprintPrivacy
{
    /// <summary>Ruídos que não dizem nada da máquina (são do Windows ou do catálogo): ficam.</summary>
    private static readonly HashSet<string> KeptNoise = new(StringComparer.Ordinal)
    {
        FingerprintDiff.ElevationNoise, FingerprintDiff.CatalogTargetNoise, FingerprintDiff.CatalogAppNoise,
    };

    private const string SystemApps = @"Windows\SystemApps\";

    public static FingerprintDiffResult Apply(FingerprintDiffResult d, WindowsFingerprint oldFp, WindowsFingerprint newFp)
    {
        var info = new FingerprintPrivacyInfo();
        var newPaths = new HashSet<string>(newFp.Binaries.Files.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        var oldPaths = oldFp.Binaries.Files.Select(b => b.Path).ToList();
        var oldItems = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in oldFp.Inventory?.Sections ?? [])
            foreach (var i in s.Items) oldItems.TryAdd($"{s.Kind}|{i.Id}", i);

        bool InBoxOldBinary(string path) =>
            path.StartsWith(SystemApps, StringComparison.OrdinalIgnoreCase) || newPaths.Contains(path);

        // ---- inventário ----
        var inventory = new List<InventoryChange>();
        foreach (var i in d.Inventory)
        {
            bool keep = i.Noise is null ? i.Change != DiffChange.Removed || OldItemIsWindows(i) : KeptNoise.Contains(i.Noise);
            if (keep) inventory.Add(i);
            else info.Add(FingerprintPrivacyInfo.Inventory(i.Kind));
        }
        d.Inventory = inventory;

        // ---- binários removidos: só os de pacotes do sistema ----
        var binaries = new List<BinaryChange>();
        foreach (var b in d.Binaries)
        {
            if (b.Change != DiffChange.Removed || b.Path.StartsWith(SystemApps, StringComparison.OrdinalIgnoreCase)) binaries.Add(b);
            else info.Add(FingerprintPrivacyInfo.Binaries);
        }
        d.Binaries = binaries;

        // ---- cadeias que sumiram: só as de binários que continuam no build novo ----
        var removed = new List<Candidate>();
        foreach (var c in d.RemovedStrings)
        {
            var kept = c.Binaries.Where(b => InBoxOldBinary(b.Path)).ToList();
            if (kept.Count == 0) { info.Add(FingerprintPrivacyInfo.RemovedStrings); continue; }
            if (kept.Count != c.Binaries.Count || c.BinaryCount > c.Binaries.Count) c.BinaryCount = kept.Count;
            c.Binaries = kept;
            removed.Add(c);
        }
        d.RemovedStrings = removed;

        // ---- referências a binários do lado antigo ----
        foreach (var c in d.Candidates.Where(c => c.OldBinaries is { Count: > 0 }))
        {
            var kept = c.OldBinaries!.Where(InBoxOldBinary).ToList();
            info.Add(FingerprintPrivacyInfo.OldBinaryReferences, c.OldBinaries!.Count - kept.Count);
            c.OldBinaries = kept;
        }
        foreach (var t in d.PossiblyObsolete)
        {
            var kept = t.OldBinaries.Where(s => InBoxOldBinary(PathOf(s))).ToList();
            info.Add(FingerprintPrivacyInfo.OldBinaryReferences, t.OldBinaries.Count - kept.Count);
            t.OldBinaries = kept;
        }

        // ---- políticas de modelos ADMX que só existem no antigo ----
        var policies = new List<PolicyChange>();
        foreach (var p in d.Policies)
        {
            if (p.Change == DiffChange.Removed && p.Note is not null) info.Add(FingerprintPrivacyInfo.Policies);
            else policies.Add(p);
        }
        d.Policies = policies;

        d.Old.MachineName = null;
        d.New.MachineName = null;
        d.Privacy = info;
        return d;

        // "caminho versão" (tweaks obsoletos) → caminho: o caminho é o binário antigo mais longo que for prefixo.
        string PathOf(string s) =>
            oldPaths.Where(p => s.Equals(p, StringComparison.OrdinalIgnoreCase) || s.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                .MaxBy(p => p.Length) ?? s;

        bool OldItemIsWindows(InventoryChange i)
        {
            var item = oldItems.GetValueOrDefault($"{i.Kind}|{i.Id}");
            switch (i.Kind)
            {
                case InventoryKinds.Services:
                case InventoryKinds.Drivers:
                {
                    if (item is null) return false;
                    var raw = item.Info?.GetValueOrDefault("serviceDll") ?? item.Properties.GetValueOrDefault("imagePath")
                              ?? (i.Kind == InventoryKinds.Drivers ? $@"\SystemRoot\System32\drivers\{item.Id}.sys" : null);
                    var path = FingerprintDiff.ResolveImagePath(raw);
                    // svchost sem ServiceDll não diz de quem é o serviço.
                    return path is not null && !path.EndsWith(@"\svchost.exe", StringComparison.OrdinalIgnoreCase) && InBoxOldBinary(path);
                }
                case InventoryKinds.Tasks:
                    return i.Id.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase);
                case InventoryKinds.Appx:
                    return i.Id.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase)
                        || i.Id.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase)
                        || oldPaths.Any(p => p.StartsWith($@"{SystemApps}{i.Id}_", StringComparison.OrdinalIgnoreCase)
                                          || p.StartsWith($@"{SystemApps}SxS\{i.Id}_", StringComparison.OrdinalIgnoreCase));
                case InventoryKinds.Features:
                    return true;   // recursos opcionais são do Windows por definição
                default:
                    return false;  // capacidades instaladas à parte, autoruns...
            }
        }
    }
}
