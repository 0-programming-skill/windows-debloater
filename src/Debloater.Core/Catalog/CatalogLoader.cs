using System.Text.RegularExpressions;
using Debloater.Core.Actions;
using Debloater.Core.Localization;
using Debloater.Core.Model;
using YamlDotNet.Core;

namespace Debloater.Core.Catalog;

/// <summary>Catalog error (or warning), always with the source file.</summary>
public sealed record CatalogError(string File, string? TweakId, string Message)
{
    public override string ToString() =>
        TweakId is null ? $"{File}: {Message}" : $"{File} [{TweakId}]: {Message}";
}

/// <summary>Load result: valid tweaks, errors and warnings (warnings do not make the catalog invalid).</summary>
public sealed class CatalogLoadResult
{
    public required IReadOnlyList<Tweak> Tweaks { get; init; }
    public required IReadOnlyList<CatalogError> Errors { get; init; }

    /// <summary>Non-fatal findings, e.g. missing translations.</summary>
    public IReadOnlyList<CatalogError> Warnings { get; init; } = [];

    /// <summary>Translated category names (catalog/i18n/categories.yaml); empty when the file is absent.</summary>
    public CategoryNames Categories { get; init; } = CategoryNames.Empty;

    public required int FileCount { get; init; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Loads and validates every *.yaml/*.yml of a directory (recursive), except the profiles and i18n folders.</summary>
public static partial class CatalogLoader
{
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[0-9]+/[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex ReIdPattern();

    /// <summary>Catalog subfolder with the profiles (YAML in another format); not read as catalog.</summary>
    public const string ProfilesFolder = "profiles";

    /// <summary>Catalog subfolder with translations that are not part of the tweaks (category names).</summary>
    public const string I18nFolder = "i18n";

    public static CatalogLoadResult LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new CatalogLoadResult
            {
                Tweaks = [],
                Errors = [new CatalogError(directory, null, "catalog directory not found.")],
                FileCount = 0,
            };
        }

        var profilesDir = Path.Combine(Path.GetFullPath(directory), ProfilesFolder) + Path.DirectorySeparatorChar;
        var i18nDir = Path.Combine(Path.GetFullPath(directory), I18nFolder) + Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFullPath(f).StartsWith(profilesDir, StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFullPath(f).StartsWith(i18nDir, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var categoriesFile = Path.Combine(directory, I18nFolder, CategoryNames.FileName);
        Func<string>? categories = File.Exists(categoriesFile) ? () => File.ReadAllText(categoriesFile) : null;

        var sources = files.Select(f => (Path.GetRelativePath(directory, f), (Func<string>)(() => File.ReadAllText(f))));
        return Load(sources, categories);
    }

    /// <summary>
    /// Loads from (file name, content reader) pairs. Useful for tests. <paramref name="categoryNames"/> reads
    /// catalog/i18n/categories.yaml; when given, category nodes without a translation are reported as warnings.
    /// </summary>
    public static CatalogLoadResult Load(IEnumerable<(string FileName, Func<string> ReadContent)> sources, Func<string>? categoryNames = null)
    {
        var tweaks = new List<Tweak>();
        var errors = new List<CatalogError>();
        var warnings = new List<CatalogError>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;

        foreach (var (fileName, read) in sources)
        {
            fileCount++;
            CatalogFile? file;
            try
            {
                file = CatalogYaml.Deserializer.Deserialize<CatalogFile>(read());
            }
            catch (YamlException ex)
            {
                var inner = ex.InnerException is null ? "" : $" ({ex.InnerException.Message})";
                errors.Add(new CatalogError(fileName, null, $"invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}{inner}"));
                continue;
            }
            catch (IOException ex)
            {
                errors.Add(new CatalogError(fileName, null, $"could not read: {ex.Message}"));
                continue;
            }

            if (file is null || file.Tweaks.Count == 0)
            {
                errors.Add(new CatalogError(fileName, null, "file without tweaks (expected: 'tweaks:' with at least one item)."));
                continue;
            }

            for (int i = 0; i < file.Tweaks.Count; i++)
            {
                var tweak = file.Tweaks[i];
                tweak.SourceFile = fileName;
                if (string.IsNullOrWhiteSpace(tweak.Category) && !string.IsNullOrWhiteSpace(file.Category))
                    tweak.Category = file.Category!;

                var tweakErrors = Validate(tweak);
                var label = string.IsNullOrWhiteSpace(tweak.Id) ? $"#{i + 1}" : tweak.Id;

                if (!string.IsNullOrWhiteSpace(tweak.Id))
                {
                    if (seen.TryGetValue(tweak.Id, out var other))
                        tweakErrors.Add($"duplicate id (already defined in {other}).");
                    else
                        seen[tweak.Id] = fileName;
                }

                if (tweakErrors.Count == 0)
                {
                    tweaks.Add(tweak);
                    warnings.AddRange(TranslationWarnings(tweak).Select(w => new CatalogError(fileName, label, w)));
                }
                else errors.AddRange(tweakErrors.Select(e => new CatalogError(fileName, label, e)));
            }
        }

        var names = CategoryNames.Empty;
        if (categoryNames is not null)
        {
            var namesFile = I18nFolder + "/" + CategoryNames.FileName;
            try
            {
                names = CategoryNames.Parse(categoryNames());
                foreach (var lang in names.Languages.Where(l => !TranslationLanguages.Contains(l, StringComparer.OrdinalIgnoreCase)))
                    errors.Add(new CatalogError(namesFile, null, $"unknown language '{lang}' (translations: {string.Join(", ", TranslationLanguages)})."));
                var nodes = tweaks.SelectMany(t => CategoryNames.NodePaths(t.Category)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
                foreach (var node in nodes)
                {
                    foreach (var lang in TranslationLanguages.Where(l => !names.Has(l, node)))
                        warnings.Add(new CatalogError(namesFile, null, $"category '{node}' has no {lang} name."));
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                errors.Add(new CatalogError(namesFile, null, ex.Message));
            }
        }

        return new CatalogLoadResult { Tweaks = tweaks, Errors = errors, Warnings = warnings, Categories = names, FileCount = fileCount };
    }

    /// <summary>Languages the catalog is translated into (every supported language except the English base).</summary>
    public static IReadOnlyList<string> TranslationLanguages { get; } =
        UiLanguage.Supported.Where(l => l != UiLanguage.English).ToList();

    /// <summary>Missing translations of a valid tweak (warnings: the English text is used instead).</summary>
    public static List<string> TranslationWarnings(Tweak tweak)
    {
        var warnings = new List<string>();
        foreach (var lang in TranslationLanguages)
        {
            var text = tweak.I18n?.FirstOrDefault(kv => kv.Key.Equals(lang, StringComparison.OrdinalIgnoreCase)).Value;
            if (text is null)
            {
                warnings.Add($"missing {lang} translation (i18n.{lang}).");
                continue;
            }
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(text.Name)) missing.Add("name");
            if (string.IsNullOrWhiteSpace(text.Description)) missing.Add("description");
            if (!string.IsNullOrWhiteSpace(tweak.Breaks) && string.IsNullOrWhiteSpace(text.Breaks)) missing.Add("breaks");
            if (!string.IsNullOrWhiteSpace(tweak.Precondition?.Reason) && string.IsNullOrWhiteSpace(text.Reason)) missing.Add("reason");
            if (missing.Count > 0) warnings.Add($"i18n.{lang} is missing: {string.Join(", ", missing)}.");
        }
        return warnings;
    }

    /// <summary>Validates a single tweak (without checking id uniqueness).</summary>
    public static List<string> Validate(Tweak tweak)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(tweak.Id)) errors.Add("field 'id' is required.");
        else if (!IdPattern().IsMatch(tweak.Id))
            errors.Add($"invalid id '{tweak.Id}': use lowercase letters, digits and hyphens, separated by dots (e.g. privacy.telemetry.allow-telemetry).");
        if (string.IsNullOrWhiteSpace(tweak.Name)) errors.Add("field 'name' is required.");
        if (string.IsNullOrWhiteSpace(tweak.Description)) errors.Add("field 'description' is required.");
        if (string.IsNullOrWhiteSpace(tweak.Category)) errors.Add("field 'category' is required (in the tweak or at the top of the file).");
        else if (tweak.Category.StartsWith('/') || tweak.Category.EndsWith('/') || tweak.Category.Contains("//"))
            errors.Add($"malformed category '{tweak.Category}' (use 'A/B').");
        if (!Enum.IsDefined(tweak.Risk)) errors.Add($"invalid risk '{tweak.Risk}'.");
        if (tweak.MinBuild is { } min && tweak.MaxBuild is { } max && min > max)
            errors.Add($"minBuild ({min}) is greater than maxBuild ({max}).");
        foreach (var url in tweak.References)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                errors.Add($"reference is not an http(s) URL: '{url}'.");
        }
        if (tweak.Confidence is { } conf && !Enum.IsDefined(conf)) errors.Add($"invalid confidence '{conf}'.");
        foreach (var source in tweak.Sources)
        {
            if (!SourceRef.KnownKinds.Contains(source.Kind))
                errors.Add($"source with invalid kind '{source.Kind}' (expected: {string.Join(" | ", SourceRef.KnownKinds)}).");
            if (string.IsNullOrWhiteSpace(source.Ref))
                errors.Add("source without 'ref'.");
            else if (source.Kind == SourceRef.ReKind && !ReIdPattern().IsMatch(source.Ref))
                errors.Add($"'re' source '{source.Ref}' must be an id \"<build>/<slug>\" (e.g. 26300/start-show-suggested-files).");
        }
        if (tweak.Precondition is { } pre)
        {
            if (string.IsNullOrWhiteSpace(pre.Powershell)) errors.Add("'precondition' requires the 'powershell' field (script that prints True/False).");
            if (string.IsNullOrWhiteSpace(pre.Reason)) errors.Add("'precondition' requires the 'reason' field (why the tweak does not apply when it is False).");
        }
        if (tweak.Actions.Count == 0) errors.Add("tweak without actions.");

        for (int i = 0; i < tweak.Actions.Count; i++)
        {
            var action = tweak.Actions[i];
            var actionErrors = new List<string>();
            if (string.IsNullOrWhiteSpace(action.Type))
                actionErrors.Add($"field 'type' is required ({string.Join(" | ", ActionHandlers.KnownTypes)}).");
            else if (!ActionHandlers.TryGet(action.Type, out var handler))
                actionErrors.Add($"unknown action type '{action.Type}' (known: {string.Join(", ", ActionHandlers.KnownTypes)}).");
            else
            {
                handler.Validate(action, actionErrors);
                if (tweak.Reversible && !handler.CanRestore)
                    actionErrors.Add($"action '{action.Type}' cannot be reverted by the engine; mark the tweak with 'reversible: false'.");
                if (tweak.Reversible && action.Type == ActionSpec.PowerShell && string.IsNullOrWhiteSpace(action.Revert))
                    actionErrors.Add("'powershell' action without 'revert' in a reversible tweak; add 'revert' or use 'reversible: false'.");
            }
            errors.AddRange(actionErrors.Select(e => $"action {i + 1}: {e}"));
        }
        return errors;
    }
}
