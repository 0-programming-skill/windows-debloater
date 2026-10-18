using System.Text;
using Debloater.Core.Catalog;
using Debloater.Core.Engine;
using Debloater.Core.Localization;
using Debloater.Core.Model;

namespace Debloater.Cli;

internal static partial class Program
{
    private static string Usage => L.T("Usage");

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // The global --lang option is accepted anywhere and applied before any text is produced.
        string? lang;
        try
        {
            lang = ExtractLanguage(ref args);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        try
        {
            UiLanguage.Apply(lang);
        }
        catch (ArgumentException)
        {
            return Fail(L.F("Err_UnsupportedLanguage", lang));
        }

        try
        {
            // 'catalog' has its own options (pack/verify/update...).
            if (args.Length > 0 && args[0].Equals("catalog", StringComparison.OrdinalIgnoreCase))
                return await CatalogCommandAsync(args[1..]);

            var parsed = Args.Parse(args);
            if (parsed.Command is null or "help" or "-h" or "--help")
            {
                Console.WriteLine(Usage);
                return parsed.Command is null ? 1 : 0;
            }

            return parsed.Command switch
            {
                "validate" => parsed.Reference ? ValidateReference(parsed) : Validate(parsed),
                "list" => List(parsed),
                "status" => await StatusAsync(parsed),
                "apply" => await ApplyAsync(parsed),
                "revert" => await RevertAsync(parsed),
                "inventory" => await InventoryAsync(parsed),
                "admx" => Admx(parsed),
                "policy" => Policy(parsed),
                "reference" => Reference(parsed),
                "selftest" => await SelfTestAsync(parsed),
                "fingerprint" => await FingerprintAsync(parsed),
                _ => Fail($"{L.F("Err_UnknownCommand", parsed.Command)}\n\n{Usage}"),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (ElevationRequiredException ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Removes the global "--lang X" (or "--lang=X") option from the arguments and returns its value
    /// (null when absent = follow the Windows display language).
    /// </summary>
    private static string? ExtractLanguage(ref string[] args)
    {
        string? lang = null;
        var rest = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--lang")
            {
                if (i + 1 >= args.Length) throw new ArgumentException(L.F("Args_NeedsValue", args[i]));
                lang = args[++i];
            }
            else if (args[i].StartsWith("--lang=", StringComparison.Ordinal))
            {
                lang = args[i]["--lang=".Length..];
            }
            else
            {
                rest.Add(args[i]);
            }
        }
        args = [.. rest];
        return lang;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    // ---------------- commands ----------------

    private static int Validate(Args a)
    {
        var result = CatalogLoader.LoadDirectory(a.CatalogDir);
        Console.WriteLine(L.F("Validate_Catalog", a.CatalogDir));
        Console.WriteLine(L.F("Validate_Summary", result.FileCount, result.Tweaks.Count, result.Errors.Count, result.Warnings.Count));
        foreach (var error in result.Errors) Console.WriteLine($"  {L.T("Label_ErrorUpper")} {error}");
        // Warnings (missing translations, category names) do not change the exit code.
        foreach (var warning in result.Warnings) Console.WriteLine($"  {L.T("Label_WarningUpper")} {warning}");
        if (result.Warnings.Count > 0) Console.WriteLine(L.F("Validate_WarningCount", result.Warnings.Count));
        return result.IsValid ? 0 : 1;
    }

    private static int List(Args a)
    {
        var loaded = LoadOrFail(a);
        if (loaded is null) return 1;
        var (tweaks, categories) = loaded.Value;
        foreach (var group in tweaks.GroupBy(t => t.Category))
        {
            Console.WriteLine($"\n[{categories.DisplayPath(group.Key)}]");
            foreach (var t in group)
                Console.WriteLine($"  {t.Id,-48} {RiskText(t.Risk),-8} {t.DisplayName}");
        }
        Console.WriteLine();
        Console.WriteLine(L.F("List_Total", tweaks.Count));
        return 0;
    }

    private static async Task<int> StatusAsync(Args a)
    {
        var loaded = LoadOrFail(a);
        if (loaded is null) return 1;
        var (tweaks, categories) = loaded.Value;
        if (tweaks.Count == 0)
        {
            Console.WriteLine(L.T("NoTweakMatches"));
            return 0;
        }
        var engine = new TweakEngine();
        Console.WriteLine(engine.System);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var statuses = await engine.GetStatusesAsync(tweaks);
        sw.Stop();

        foreach (var group in statuses.GroupBy(s => s.Tweak.Category))
        {
            Console.WriteLine($"\n[{categories.DisplayPath(group.Key)}]");
            foreach (var s in group)
            {
                Console.WriteLine($"  {s.Tweak.Id,-48} {s.State,-13} {RiskText(s.Tweak.Risk),-8} {s.Tweak.DisplayName}");
                bool showDetails = a.Verbose || s.State is TweakState.Error or TweakState.Partial;
                if (s.Message is not null && (a.Verbose || s.State == TweakState.Error))
                    Console.WriteLine($"      {s.Message}");
                if (showDetails)
                    foreach (var act in s.Actions)
                        Console.WriteLine($"      - {act.State,-13} {act.Action}  ({act.Detail})");
                if (a.Verbose && s.Tweak.RequiresFeature is { Count: > 0 } required)
                    foreach (var id in required)
                    {
                        var f = Debloater.Core.Platform.FeatureFlags.FeatureFlagStatus.Read(Debloater.Core.Platform.FeatureFlags.NativeFeatureStore.Instance, id);
                        Console.WriteLine($"      {L.F("Status_RequiresFeature", Debloater.Core.Platform.FeatureFlags.KnownFeatures.Label(id), f)}");
                    }
            }
        }

        Console.WriteLine();
        var summary = statuses.GroupBy(s => s.State).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}");
        Console.WriteLine($"Total {statuses.Count} | {string.Join(" | ", summary)} | {sw.ElapsedMilliseconds} ms");
        return statuses.Any(s => s.State == TweakState.Error) ? 3 : 0;
    }

    private static async Task<int> ApplyAsync(Args a)
    {
        var selected = SelectByIds(a);
        if (selected is null) return 1;
        var engine = new TweakEngine();
        int failures = 0;
        foreach (var tweak in selected)
        {
            try
            {
                if (!tweak.Reversible) Console.WriteLine(L.F("Apply_NotReversible", tweak.Id));
                if (tweak.DisplayBreaks is { } breaks) Console.WriteLine(L.F("Apply_MayAffect", tweak.Id, breaks.Trim()));
                var result = await engine.ApplyAsync(tweak, a.MaxRisk);
                Report(result);
                if (!result.Success) failures++;
            }
            catch (RiskNotAllowedException ex)
            {
                Console.Error.WriteLine($"x {ex.Message} {L.F("Apply_UseMaxRisk", ex.Tweak.Risk.ToString().ToLowerInvariant())}");
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> RevertAsync(Args a)
    {
        var selected = SelectByIds(a);
        if (selected is null) return 1;
        var engine = new TweakEngine();
        int failures = 0;
        foreach (var tweak in selected)
        {
            var result = await engine.RevertAsync(tweak);
            Report(result);
            if (!result.Success) failures++;
        }
        return failures == 0 ? 0 : 1;
    }

    // ---------------- helpers ----------------

    private static void Report(OperationResult r)
    {
        Console.WriteLine($"{(r.Success ? "ok" : "x ")} {r.TweakId}: {r.Message}");
        foreach (var e in r.Errors) Console.WriteLine($"    {e}");
        foreach (var n in r.Notes) Console.WriteLine($"    {L.T("Label_Warning")}: {n}");
    }

    private static (List<Tweak> Tweaks, CategoryNames Categories)? LoadOrFail(Args a)
    {
        var result = CatalogLoader.LoadDirectory(a.CatalogDir);
        if (!result.IsValid)
        {
            Console.Error.WriteLine(L.F("Catalog_HasErrors", result.Errors.Count, result.Errors[0]));
            if (result.Tweaks.Count == 0) return null;
        }
        var categories = result.Categories;
        var tweaks = result.Tweaks
            .Where(t => a.Category is null || CategoryMatches(t.Category, a.Category, categories))
            .OrderBy(t => categories.DisplayPath(t.Category), StringComparer.CurrentCulture)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
        return (tweaks, categories);
    }

    /// <summary>
    /// Case-insensitive category prefix, either the identifier (e.g. "privacy", "System/Scheduled tasks")
    /// or the displayed (translated) path.
    /// </summary>
    private static bool CategoryMatches(string category, string filter, CategoryNames categories)
    {
        var prefix = filter.Trim().TrimEnd('/');
        return category.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)
            || categories.DisplayPath(category).StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase);
    }

    private static List<Tweak>? SelectByIds(Args a)
    {
        if (a.Positional.Count == 0)
        {
            Console.Error.WriteLine(L.T("Select_NeedId"));
            return null;
        }
        var all = CatalogLoader.LoadDirectory(a.CatalogDir);
        var byId = all.Tweaks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var missing = a.Positional.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine(L.F("Select_IdsNotFound", string.Join(", ", missing)));
            return null;
        }
        return a.Positional.Select(id => byId[id]).ToList();
    }

    private static string RiskText(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => L.T("Risk_Low"),
        RiskLevel.Medium => L.T("Risk_Medium"),
        RiskLevel.High => L.T("Risk_High"),
        RiskLevel.Critical => L.T("Risk_Critical"),
        _ => risk.ToString(),
    };

    internal sealed class Args
    {
        public string? Command { get; private set; }
        public List<string> Positional { get; } = [];
        public string? Category { get; private set; }
        private string? _catalogDir;

        /// <summary>--catalog, or the active catalog (newest verified update, else the built-in one), resolved on first use.</summary>
        public string CatalogDir { get => _catalogDir ??= ActiveCatalogDirectory(); private set => _catalogDir = value; }
        public RiskLevel MaxRisk { get; private set; } = RiskLevel.Medium;
        public bool Verbose { get; private set; }
        public List<string> Kinds { get; } = [];
        public bool Uncatalogued { get; private set; }
        public bool Json { get; private set; }
        public string? Search { get; private set; }
        public bool Configured { get; private set; }
        public bool WithState { get; private set; }
        public string AdmxDir { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "PolicyDefinitions");
        public bool Reference { get; private set; }
        public string? CspFile { get; private set; }
        public string? WarningsOut { get; private set; }
        public bool Write { get; private set; }
        public bool Force { get; private set; }
        public bool MaxRiskSet { get; private set; }
        public List<string> Ids { get; } = [];
        public bool IncludeIrreversible { get; private set; }
        public string? JsonOut { get; private set; }
        public string? BackupDir { get; private set; }
        public bool RestorePoint { get; private set; }
        public bool NotAVmOverride { get; private set; }
        public string Phase { get; private set; } = "full";
        public string? StateFile { get; private set; }
        public string? Label { get; private set; }
        public string? Root { get; private set; }
        public string BinaryMode { get; private set; } = "default";
        public string? MdOut { get; private set; }
        public int Top { get; private set; } = 100;
        public bool Privacy { get; private set; } = true;
        public string? PolicyState { get; private set; }
        public List<string> Values { get; } = [];
        public string? Scope { get; private set; }
        public bool DryRun { get; private set; }

        /// <summary>Parses the arguments; the global --lang option must already have been removed.</summary>
        public static Args Parse(string[] args)
        {
            var a = new Args();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(L.F("Args_NeedsValue", args[i]));
                switch (args[i])
                {
                    case "--category": a.Category = Next(); break;
                    case "--catalog": a.CatalogDir = Path.GetFullPath(Next()); break;
                    case "--verbose": case "-v": a.Verbose = true; break;
                    case "--kind": a.Kinds.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    case "--uncatalogued": a.Uncatalogued = true; break;
                    case "--json":
                        // In selftest/fingerprint, --json takes the report path; elsewhere it is a switch.
                        if (a.Command is "selftest" or "fingerprint") a.JsonOut = Path.GetFullPath(Next());
                        else a.Json = true;
                        break;
                    case "--id": a.Ids.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    case "--include-irreversible": a.IncludeIrreversible = true; break;
                    case "--backup-dir": a.BackupDir = Path.GetFullPath(Next()); break;
                    case "--restore-point": a.RestorePoint = true; break;
                    case "--i-know-this-is-not-a-vm": a.NotAVmOverride = true; break;
                    case "--phase": a.Phase = Next().ToLowerInvariant(); break;
                    case "--state":
                        // In 'policy', --state is the desired state; in selftest, the phase state file.
                        if (a.Command == "policy") a.PolicyState = Next();
                        else a.StateFile = Path.GetFullPath(Next());
                        break;
                    case "--value": a.Values.Add(Next()); break;
                    case "--scope": a.Scope = Next(); break;
                    case "--dry-run": a.DryRun = true; break;
                    case "--label": a.Label = Next(); break;
                    case "--root": a.Root = Path.GetFullPath(Next()); break;
                    case "--binaries": a.BinaryMode = Next().ToLowerInvariant(); break;
                    case "--md": a.MdOut = Path.GetFullPath(Next()); break;
                    case "--privacy": a.Privacy = true; break;
                    case "--no-privacy": a.Privacy = false; break;
                    case "--top":
                        var top = Next();
                        a.Top = int.TryParse(top, out var t) && t > 0 ? t : throw new ArgumentException(L.F("Args_InvalidTop", top));
                        break;
                    case "--search": a.Search = Next(); break;
                    case "--configured": a.Configured = true; break;
                    case "--with-state": a.WithState = true; break;
                    case "--admx-dir": a.AdmxDir = Path.GetFullPath(Next()); break;
                    case "--reference": a.Reference = true; break;
                    case "--csp": a.CspFile = Path.GetFullPath(Next()); break;
                    case "--warnings-out": a.WarningsOut = Path.GetFullPath(Next()); break;
                    case "--write": a.Write = true; break;
                    case "--force": a.Force = true; break;
                    case "--max-risk":
                        var v = Next();
                        a.MaxRisk = Enum.TryParse<RiskLevel>(v, ignoreCase: true, out var r) && Enum.IsDefined(r)
                            ? r
                            : throw new ArgumentException(L.F("Args_InvalidMaxRisk", v));
                        a.MaxRiskSet = true;
                        break;
                    default:
                        if (args[i].StartsWith("--")) throw new ArgumentException(L.F("Args_UnknownOption", args[i]));
                        if (a.Command is null) a.Command = args[i].ToLowerInvariant();
                        else a.Positional.Add(args[i]);
                        break;
                }
            }
            return a;
        }
    }
}
