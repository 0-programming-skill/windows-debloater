using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Debloater.Core.Catalog;
using Debloater.Core.Fingerprint;

namespace Debloater.Cli;

internal static partial class Program
{
    private static async Task<int> FingerprintAsync(Args a)
    {
        var sub = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : null;
        return sub switch
        {
            "create" => await FingerprintCreateAsync(a),
            "diff" => FingerprintDiffCommand(a),
            _ => Fail(L.T("Fp_Usage")),
        };
    }

    private static async Task<int> FingerprintCreateAsync(Args a)
    {
        if (a.Positional.Count < 2) return Fail(L.T("FpCreate_Usage"));
        var file = Path.GetFullPath(a.Positional[1]);
        var catalog = CatalogLoader.LoadDirectory(a.CatalogDir);
        var sw = Stopwatch.StartNew();
        var fp = await FingerprintCollector.CreateAsync(new FingerprintOptions
        {
            Root = a.Root ?? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
            BinaryMode = a.BinaryMode,
            Catalog = catalog.Tweaks,
            Log = line => Console.WriteLine(line),
        });
        var write = Stopwatch.StartNew();
        FingerprintStore.Save(fp, file);
        fp.TimingsMs["write"] = write.ElapsedMilliseconds;

        Console.WriteLine();
        Console.WriteLine(L.F("Fp_Created", file, new FileInfo(file).Length / 1024.0));
        Console.WriteLine("  " + L.F("Fp_Os", fp.Os.FullBuild, fp.Os.Edition, fp.Os.DisplayVersion, fp.Root,
            L.T(fp.Live ? "Fp_Live" : "Fp_Offline"), L.T(fp.Elevated ? "Fp_Elevated" : "Fp_NotElevated")));
        Console.WriteLine("  " + L.F("Fp_Counts", fp.Inventory?.Sections.Sum(s => s.Items.Count) ?? 0, fp.Policies.Count, fp.Binaries.Mode,
            fp.Binaries.Files.Count, fp.Binaries.Files.Sum(f => f.Strings.Count), fp.Binaries.Files.Sum(f => f.NearNames?.Count ?? 0), fp.Binaries.Errors));
        Console.WriteLine("  " + L.F("Fp_Timings", string.Join(", ", fp.TimingsMs.Select(t => $"{t.Key}={t.Value}"))));
        foreach (var n in fp.Notes) Console.WriteLine($"  {L.T("Label_Note")}: {n}");
        Console.WriteLine(L.F("Fp_Total", sw.Elapsed.TotalSeconds));
        return 0;
    }

    private static int FingerprintDiffCommand(Args a)
    {
        if (a.Positional.Count < 3) return Fail(L.T("FpDiff_Usage"));
        var sw = Stopwatch.StartNew();
        var oldFp = FingerprintStore.Load(a.Positional[1]);
        var newFp = FingerprintStore.Load(a.Positional[2]);
        var catalog = CatalogLoader.LoadDirectory(a.CatalogDir);
        var diff = FingerprintDiff.Compare(oldFp, newFp, catalog.Tweaks);
        if (a.Privacy) FingerprintPrivacy.Apply(diff, oldFp, newFp);

        if (a.MdOut is not null)
        {
            WriteFile(a.MdOut, FingerprintReport.ToMarkdown(diff, a.Top));
            Console.WriteLine(L.F("FpDiff_Markdown", a.MdOut));
        }
        if (a.JsonOut is not null)
        {
            WriteFile(a.JsonOut, JsonSerializer.Serialize(diff, FingerprintDiff.JsonOptions));
            Console.WriteLine(L.F("FpDiff_Json", a.JsonOut));
        }

        Console.WriteLine($"Build {diff.Old.Build} → {diff.New.Build}");
        foreach (var w in diff.Warnings) Console.WriteLine($"  {L.T("Label_Warning")}: {w}");
        Console.WriteLine("  " + (diff.Privacy is { } p
            ? L.F("FpDiff_PrivacyOn", p.Total, string.Join(", ", p.Omitted.Select(o => $"{o.Key}={o.Value}")))
            : L.T("FpDiff_PrivacyOff")));
        foreach (var (k, v) in diff.Counts) Console.WriteLine($"  {k,-34} {v,8:N0}");
        Console.WriteLine();
        Console.WriteLine(L.F("FpDiff_Top", Math.Min(25, diff.Candidates.Count)));
        foreach (var c in diff.Candidates.Take(25))
            Console.WriteLine($"  {c.Score,4} {c.Kind,-17} {c.Value}  [{c.Binaries.FirstOrDefault()?.Path}]");
        Console.WriteLine($"({sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    private static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
