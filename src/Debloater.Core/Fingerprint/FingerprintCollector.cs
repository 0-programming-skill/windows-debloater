using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Debloater.Core.Admx;
using Debloater.Core.Inventory;
using Debloater.Core.Model;
using Debloater.Core.Platform;

namespace Debloater.Core.Fingerprint;

public sealed class FingerprintOptions
{
    /// <summary>Raiz da instalação (C:\ ou a pasta onde uma imagem foi montada).</summary>
    public string Root { get; init; } = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public string BinaryMode { get; init; } = BinaryScanModes.Default;

    /// <summary>Catálogo para anotar o inventário (opcional).</summary>
    public IReadOnlyList<Tweak>? Catalog { get; init; }

    public bool CollectInventory { get; init; } = true;

    public StringExtractorOptions Extractor { get; init; } = StringExtractorOptions.Default;

    public Action<string>? Log { get; init; }
}

/// <summary>
/// Cria a impressão digital de uma instalação. Somente leitura: lê arquivos, registro (Windows em
/// execução) e as APIs de inventário; não altera nada.
///
/// Com uma raiz que não é o Windows em execução (imagem montada), só as partes baseadas em
/// arquivos são coletadas: versão do ntoskrnl.exe (build/UBR), ADMX, binários e tarefas
/// agendadas (XML em System32\Tasks). Serviços, drivers, appx, recursos e capacidades exigiriam
/// carregar as colmeias offline (SYSTEM/SOFTWARE) e ficam de fora.
/// </summary>
public static class FingerprintCollector
{
    public static async Task<WindowsFingerprint> CreateAsync(FingerprintOptions o, CancellationToken ct = default)
    {
        if (!BinaryScanModes.IsKnown(o.BinaryMode))
            throw new ArgumentException($"--binaries inválido '{o.BinaryMode}' (default|all|none).");
        var root = Path.GetFullPath(o.Root);
        if (!Directory.Exists(Path.Combine(root, "Windows")))
            throw new ArgumentException($"'{root}' não parece a raiz de uma instalação do Windows (falta a pasta Windows).");

        var fp = new WindowsFingerprint
        {
            CreatedUtc = DateTime.UtcNow,
            Root = root,
            Live = IsLiveRoot(root),
            Elevated = SystemInfo.IsProcessElevated(),
        };
        if (fp.Live) fp.MachineName = Environment.MachineName;
        var total = Stopwatch.StartNew();

        // ---- SO ----
        var sw = Stopwatch.StartNew();
        fp.Os = ReadOs(root, fp.Live, fp.Notes);
        fp.TimingsMs["os"] = sw.ElapsedMilliseconds;
        o.Log?.Invoke($"Windows {fp.Os.FullBuild} {fp.Os.Edition} ({(fp.Live ? "em execução" : "offline")}, {(fp.Elevated ? "elevado" : "sem elevação")})");

        // ---- binários (em paralelo com o resto) ----
        var binaryTimings = new Dictionary<string, long>();
        var binariesTask = Task.Run(() => ScanBinaries(root, o, fp, binaryTimings), ct);

        // ---- inventário ----
        if (o.CollectInventory)
        {
            sw.Restart();
            if (fp.Live)
            {
                fp.Inventory = await new InventoryCollector().CollectAsync(null, ct).ConfigureAwait(false);
            }
            else
            {
                fp.Inventory = CollectOfflineInventory(root);
                fp.Notes.Add("raiz offline: inventário só de tarefas agendadas (XML); serviços, drivers, appx, recursos e capacidades exigem carregar as colmeias offline (não implementado).");
            }
            if (o.Catalog is not null) CatalogCrossReference.Annotate(fp.Inventory, o.Catalog);
            foreach (var s in fp.Inventory.Sections.Where(s => !s.Available || s.Note is not null))
                fp.Notes.Add($"inventário {s.Kind}: {(s.Available ? "" : "INDISPONÍVEL ")}{s.Note}");
            fp.TimingsMs["inventory"] = sw.ElapsedMilliseconds;
            o.Log?.Invoke($"Inventário: {fp.Inventory.Sections.Sum(s => s.Items.Count)} itens em {sw.ElapsedMilliseconds} ms");
        }

        // ---- ADMX ----
        sw.Restart();
        var admxDir = Path.Combine(root, "Windows", "PolicyDefinitions");
        // en-US primeiro: textos iguais em instalações de idiomas diferentes (o diff compara as referências cruas).
        var admx = AdmxParser.ParseDirectory(admxDir, ["en-US", .. AdmxParser.DefaultLanguages]);
        fp.Policies = [.. admx.Policies.Select(ToFingerprintPolicy).OrderBy(p => p.QualifiedName, StringComparer.OrdinalIgnoreCase)];
        if (admx.FileCount == 0) fp.Notes.Add($"nenhum ADMX em {admxDir}");
        fp.TimingsMs["admx"] = sw.ElapsedMilliseconds;
        o.Log?.Invoke($"ADMX: {fp.Policies.Count} políticas de {admx.FileCount} arquivos em {sw.ElapsedMilliseconds} ms");

        await binariesTask.ConfigureAwait(false);
        foreach (var (k, v) in binaryTimings) fp.TimingsMs[k] = v;
        fp.TimingsMs["total"] = total.ElapsedMilliseconds;
        return fp;
    }

    private static void ScanBinaries(string root, FingerprintOptions o, WindowsFingerprint fp, Dictionary<string, long> timings)
    {
        var info = new BinaryScanInfo { Mode = o.BinaryMode, MaxStringsPerFile = o.Extractor.MaxStrings };
        fp.Binaries = info;
        if (o.BinaryMode == BinaryScanModes.None) return;

        var sw = Stopwatch.StartNew();
        var roots = BinaryScanner.DefaultRoots(root);
        info.Roots = [.. roots.Select(r => r.RelativePath + (r.Recursive ? @"\**" : @"\*"))];
        var candidates = BinaryScanner.Enumerate(root, o.BinaryMode, roots);
        info.FilesConsidered = candidates.Count;
        timings["binaries.enumerate"] = sw.ElapsedMilliseconds;
        o.Log?.Invoke($"Binários ({o.BinaryMode}): {candidates.Count} arquivos para ler...");

        sw.Restart();
        var result = BinaryScanner.Scan(candidates, o.Extractor, progress: new Progress<int>(n => o.Log?.Invoke($"  {n}/{candidates.Count}")));
        info.Files = result.Files;
        info.TotalBytes = result.TotalBytes;
        info.Errors = result.Errors.Count;
        info.ErrorSamples = [.. result.Errors.Take(20)];
        timings["binaries.scan"] = sw.ElapsedMilliseconds;
        o.Log?.Invoke($"Binários: {result.Files.Count} PE, {result.TotalBytes / 1048576.0:N0} MB, " +
                      $"{result.Files.Sum(f => f.Strings.Count + (f.NearNames?.Count ?? 0)):N0} cadeias, {result.Errors.Count} erros, " +
                      $"{sw.ElapsedMilliseconds} ms ({result.TotalBytes / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0} MB/s)");
    }

    internal static bool IsLiveRoot(string root)
    {
        var running = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.Combine(root, "Windows")),
            Path.TrimEndingDirectorySeparator(running), StringComparison.OrdinalIgnoreCase);
    }

    private static FingerprintOs ReadOs(string root, bool live, List<string> notes)
    {
        if (live)
        {
            var s = SystemInfo.Read();
            return new FingerprintOs
            {
                Build = s.Build, Ubr = s.Ubr, Edition = s.Edition, DisplayVersion = s.DisplayVersion,
                ProductName = s.ProductName, Source = "registro (HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion)",
            };
        }
        var kernel = Path.Combine(root, "Windows", "System32", "ntoskrnl.exe");
        if (File.Exists(kernel))
        {
            var v = FileVersionInfo.GetVersionInfo(kernel);
            notes.Add("raiz offline: build/UBR lidos da versão do ntoskrnl.exe; edição e versão de exibição exigem a colmeia SOFTWARE (não lida).");
            return new FingerprintOs { Build = v.ProductBuildPart, Ubr = v.ProductPrivatePart, Source = "versão do ntoskrnl.exe" };
        }
        notes.Add("raiz offline sem ntoskrnl.exe: build desconhecido.");
        return new FingerprintOs { Source = "desconhecido" };
    }

    internal static FingerprintPolicy ToFingerprintPolicy(AdmxPolicy p)
    {
        var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string? name) { if (!string.IsNullOrEmpty(name)) values.Add($@"{key}\{name}"); }
        void AddList(IEnumerable<AdmxListItem>? list) { foreach (var i in list ?? []) Add(i.Key, i.ValueName); }
        Add(p.Key, p.ValueName);
        AddList(p.EnabledList);
        AddList(p.DisabledList);
        foreach (var e in p.Elements)
        {
            Add(e.Key, e.ValueName);
            AddList(e.TrueList);
            AddList(e.FalseList);
            foreach (var item in e.Items ?? []) AddList(item.ValueList);
            if (e.Type == "list" && e.ValueName is null) values.Add($@"{e.Key}\*");
        }
        return new FingerprintPolicy
        {
            File = p.File,
            Name = p.Name,
            Class = p.Class.ToString(),
            Key = p.Key,
            ValueName = p.ValueName,
            DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? null : p.DisplayName,
            SupportedOn = p.SupportedOn ?? p.SupportedOnRef,
            SupportedOnRef = p.SupportedOnRef,
            Category = p.CategoryPath.Count > 0 ? string.Join(" > ", p.CategoryPath) : null,
            Values = [.. values],
        };
    }

    // ---------------- offline: tarefas a partir dos XML ----------------

    internal static InventorySnapshot CollectOfflineInventory(string root)
    {
        var snapshot = new InventorySnapshot { CreatedUtc = DateTime.UtcNow };
        var section = new InventorySection { Kind = InventoryKinds.Tasks };
        var tasksDir = Path.Combine(root, "Windows", "System32", "Tasks");
        int denied = 0;
        if (Directory.Exists(tasksDir))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(tasksDir, "*", options))
            {
                try
                {
                    var doc = XDocument.Load(file);
                    var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                    var enabled = doc.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
                    var path = "\\" + Path.GetRelativePath(tasksDir, file);
                    var item = new InventoryItem { Kind = InventoryKinds.Tasks, Id = path, DisplayName = Path.GetFileName(file) };
                    item.Properties["enabled"] = string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
                    section.Items.Add(item);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    denied++;
                }
            }
        }
        else
        {
            section.Available = false;
            section.Note = "pasta System32\\Tasks não encontrada";
        }
        if (denied > 0) section.Note = $"{denied} arquivo(s) de tarefa ilegíveis";
        section.Items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
        snapshot.Sections.Add(section);
        return snapshot;
    }
}

/// <summary>Grava e lê impressões digitais (JSON, compactado com gzip se o arquivo terminar em .gz).</summary>
public static class FingerprintStore
{
    public static JsonSerializerOptions JsonOptions { get; } = new(InventoryDiff.JsonOptions) { WriteIndented = false };

    public static void Save(WindowsFingerprint fp, string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(file);
        if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
            JsonSerializer.Serialize(gz, fp, JsonOptions);
        }
        else
        {
            JsonSerializer.Serialize(fs, fp, JsonOptions);
        }
    }

    public static WindowsFingerprint Load(string file)
    {
        using var fs = File.OpenRead(file);
        Span<byte> magic = stackalloc byte[2];
        int n = fs.Read(magic);
        fs.Position = 0;
        Stream s = n == 2 && magic[0] == 0x1F && magic[1] == 0x8B ? new GZipStream(fs, CompressionMode.Decompress) : fs;
        using (s == fs ? null : s)
        {
            return JsonSerializer.Deserialize<WindowsFingerprint>(s, JsonOptions)
                ?? throw new InvalidDataException($"impressão digital vazia: {file}");
        }
    }
}
