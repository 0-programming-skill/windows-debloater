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
            _ => Fail("Uso: fingerprint create <saida.json.gz> [--root C:\\] [--binaries default|all|none] | fingerprint diff <antigo> <novo> [--md R.md] [--json R.json] [--top N] [--no-privacy]"),
        };
    }

    private static async Task<int> FingerprintCreateAsync(Args a)
    {
        if (a.Positional.Count < 2) return Fail("Uso: fingerprint create <saida.json.gz> [--root C:\\] [--binaries default|all|none]");
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
        Console.WriteLine($"Impressão digital: {file} ({new FileInfo(file).Length / 1024.0:N0} KB)");
        Console.WriteLine($"  Windows {fp.Os.FullBuild} {fp.Os.Edition} {fp.Os.DisplayVersion} | raiz {fp.Root} | {(fp.Live ? "em execução" : "offline")} | {(fp.Elevated ? "elevado" : "sem elevação")}");
        Console.WriteLine($"  inventário: {fp.Inventory?.Sections.Sum(s => s.Items.Count) ?? 0} itens | ADMX: {fp.Policies.Count} políticas | binários ({fp.Binaries.Mode}): {fp.Binaries.Files.Count} PE, {fp.Binaries.Files.Sum(f => f.Strings.Count):N0} cadeias + {fp.Binaries.Files.Sum(f => f.NearNames?.Count ?? 0):N0} nomes próximos, {fp.Binaries.Errors} erro(s)");
        Console.WriteLine($"  tempos (ms): {string.Join(", ", fp.TimingsMs.Select(t => $"{t.Key}={t.Value}"))}");
        foreach (var n in fp.Notes) Console.WriteLine($"  nota: {n}");
        Console.WriteLine($"Total: {sw.Elapsed.TotalSeconds:N1} s");
        return 0;
    }

    private static int FingerprintDiffCommand(Args a)
    {
        if (a.Positional.Count < 3) return Fail("Uso: fingerprint diff <antigo> <novo> [--md R.md] [--json R.json] [--top N] [--no-privacy]");
        var sw = Stopwatch.StartNew();
        var oldFp = FingerprintStore.Load(a.Positional[1]);
        var newFp = FingerprintStore.Load(a.Positional[2]);
        var catalog = CatalogLoader.LoadDirectory(a.CatalogDir);
        var diff = FingerprintDiff.Compare(oldFp, newFp, catalog.Tweaks);
        if (a.Privacy) FingerprintPrivacy.Apply(diff, oldFp, newFp);

        if (a.MdOut is not null)
        {
            WriteFile(a.MdOut, FingerprintReport.ToMarkdown(diff, a.Top));
            Console.WriteLine($"Relatório Markdown: {a.MdOut}");
        }
        if (a.JsonOut is not null)
        {
            WriteFile(a.JsonOut, JsonSerializer.Serialize(diff, FingerprintDiff.JsonOptions));
            Console.WriteLine($"Relatório JSON: {a.JsonOut}");
        }

        Console.WriteLine($"Build {diff.Old.Build} → {diff.New.Build}");
        foreach (var w in diff.Warnings) Console.WriteLine($"  aviso: {w}");
        Console.WriteLine(diff.Privacy is { } p
            ? $"  privacidade: ligada, {p.Total} item(ns) omitido(s) ({string.Join(", ", p.Omitted.Select(o => $"{o.Key}={o.Value}"))}); --no-privacy desliga"
            : "  privacidade: desligada (--no-privacy)");
        foreach (var (k, v) in diff.Counts) Console.WriteLine($"  {k,-34} {v,8:N0}");
        Console.WriteLine();
        Console.WriteLine($"Top {Math.Min(25, diff.Candidates.Count)} candidatos:");
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
