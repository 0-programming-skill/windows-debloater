using System.Text.RegularExpressions;
using Debloater.Core.Actions;
using Debloater.Core.Model;
using YamlDotNet.Core;

namespace Debloater.Core.Catalog;

/// <summary>Erro de catálogo, sempre com o arquivo de origem.</summary>
public sealed record CatalogError(string File, string? TweakId, string Message)
{
    public override string ToString() =>
        TweakId is null ? $"{File}: {Message}" : $"{File} [{TweakId}]: {Message}";
}

/// <summary>Resultado do carregamento: tweaks válidos + lista de erros.</summary>
public sealed class CatalogLoadResult
{
    public required IReadOnlyList<Tweak> Tweaks { get; init; }
    public required IReadOnlyList<CatalogError> Errors { get; init; }
    public required int FileCount { get; init; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Carrega e valida todos os *.yaml/*.yml de um diretório (recursivo).</summary>
public static partial class CatalogLoader
{
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$")]
    private static partial Regex IdPattern();

    /// <summary>Subpasta do catálogo com os perfis (YAML de outro formato); não é lida como catálogo.</summary>
    public const string ProfilesFolder = "profiles";

    public static CatalogLoadResult LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new CatalogLoadResult
            {
                Tweaks = [],
                Errors = [new CatalogError(directory, null, "diretório do catálogo não encontrado.")],
                FileCount = 0,
            };
        }

        var profilesDir = Path.Combine(Path.GetFullPath(directory), ProfilesFolder) + Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFullPath(f).StartsWith(profilesDir, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sources = files.Select(f => (Path.GetRelativePath(directory, f), (Func<string>)(() => File.ReadAllText(f))));
        return Load(sources);
    }

    /// <summary>Carrega a partir de pares (nome do arquivo, leitor de conteúdo). Útil para testes.</summary>
    public static CatalogLoadResult Load(IEnumerable<(string FileName, Func<string> ReadContent)> sources)
    {
        var tweaks = new List<Tweak>();
        var errors = new List<CatalogError>();
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
                errors.Add(new CatalogError(fileName, null, $"YAML inválido na linha {ex.Start.Line}, coluna {ex.Start.Column}: {ex.Message}{inner}"));
                continue;
            }
            catch (IOException ex)
            {
                errors.Add(new CatalogError(fileName, null, $"não foi possível ler: {ex.Message}"));
                continue;
            }

            if (file is null || file.Tweaks.Count == 0)
            {
                errors.Add(new CatalogError(fileName, null, "arquivo sem tweaks (esperado: 'tweaks:' com ao menos um item)."));
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
                        tweakErrors.Add($"id duplicado (já definido em {other}).");
                    else
                        seen[tweak.Id] = fileName;
                }

                if (tweakErrors.Count == 0) tweaks.Add(tweak);
                else errors.AddRange(tweakErrors.Select(e => new CatalogError(fileName, label, e)));
            }
        }

        return new CatalogLoadResult { Tweaks = tweaks, Errors = errors, FileCount = fileCount };
    }

    /// <summary>Valida um tweak isolado (sem checar unicidade de id).</summary>
    public static List<string> Validate(Tweak tweak)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(tweak.Id)) errors.Add("campo 'id' é obrigatório.");
        else if (!IdPattern().IsMatch(tweak.Id))
            errors.Add($"id '{tweak.Id}' inválido: use minúsculas, dígitos e hífens, separados por pontos (ex.: privacy.telemetry.allow-telemetry).");
        if (string.IsNullOrWhiteSpace(tweak.Name)) errors.Add("campo 'name' é obrigatório.");
        if (string.IsNullOrWhiteSpace(tweak.Description)) errors.Add("campo 'description' é obrigatório.");
        if (string.IsNullOrWhiteSpace(tweak.Category)) errors.Add("campo 'category' é obrigatório (no tweak ou no topo do arquivo).");
        else if (tweak.Category.StartsWith('/') || tweak.Category.EndsWith('/') || tweak.Category.Contains("//"))
            errors.Add($"categoria '{tweak.Category}' malformada (use 'A/B').");
        if (!Enum.IsDefined(tweak.Risk)) errors.Add($"risk inválido '{tweak.Risk}'.");
        if (tweak.MinBuild is { } min && tweak.MaxBuild is { } max && min > max)
            errors.Add($"minBuild ({min}) maior que maxBuild ({max}).");
        foreach (var url in tweak.References)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                errors.Add($"referência não é uma URL http(s): '{url}'.");
        }
        if (tweak.Confidence is { } conf && !Enum.IsDefined(conf)) errors.Add($"confidence inválido '{conf}'.");
        foreach (var source in tweak.Sources)
        {
            if (!SourceRef.KnownKinds.Contains(source.Kind))
                errors.Add($"fonte com kind inválido '{source.Kind}' (esperado: {string.Join(" | ", SourceRef.KnownKinds)}).");
            if (string.IsNullOrWhiteSpace(source.Ref))
                errors.Add("fonte sem 'ref'.");
        }
        if (tweak.Actions.Count == 0) errors.Add("tweak sem ações.");

        for (int i = 0; i < tweak.Actions.Count; i++)
        {
            var action = tweak.Actions[i];
            var actionErrors = new List<string>();
            if (string.IsNullOrWhiteSpace(action.Type))
                actionErrors.Add($"campo 'type' é obrigatório ({string.Join(" | ", ActionHandlers.KnownTypes)}).");
            else if (!ActionHandlers.TryGet(action.Type, out var handler))
                actionErrors.Add($"tipo de ação desconhecido '{action.Type}' (conhecidos: {string.Join(", ", ActionHandlers.KnownTypes)}).");
            else
            {
                handler.Validate(action, actionErrors);
                if (tweak.Reversible && !handler.CanRestore)
                    actionErrors.Add($"ação '{action.Type}' não é reversível pelo engine; marque o tweak com 'reversible: false'.");
                if (tweak.Reversible && action.Type == ActionSpec.PowerShell && string.IsNullOrWhiteSpace(action.Revert))
                    actionErrors.Add("ação 'powershell' sem 'revert' num tweak reversível; adicione 'revert' ou use 'reversible: false'.");
            }
            errors.AddRange(actionErrors.Select(e => $"ação {i + 1}: {e}"));
        }
        return errors;
    }
}
