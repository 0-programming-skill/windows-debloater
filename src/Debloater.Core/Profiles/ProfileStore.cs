using Debloater.Core.Admx;
using Debloater.Core.Catalog;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Policies;
using YamlDotNet.Core;

namespace Debloater.Core.Profiles;

/// <summary>Leitura, gravação e resolução de perfis (YAML).</summary>
public static class ProfileStore
{
    /// <summary>Lê um perfil de texto YAML. Lança <see cref="FormatException"/> com mensagem em pt-BR.</summary>
    public static Profile Parse(string yaml, string? sourceFile = null)
    {
        Profile? profile;
        try
        {
            profile = CatalogYaml.Deserializer.Deserialize<Profile>(yaml);
        }
        catch (YamlException ex)
        {
            var inner = ex.InnerException is null ? "" : $" ({ex.InnerException.Message})";
            throw new FormatException($"Perfil inválido na linha {ex.Start.Line}, coluna {ex.Start.Column}: {ex.Message}{inner}", ex);
        }
        if (profile is null) throw new FormatException("Perfil vazio.");
        var errors = Validate(profile);
        if (errors.Count > 0) throw new FormatException("Perfil inválido: " + string.Join(" ", errors));
        profile.SourceFile = sourceFile;
        return profile;
    }

    public static Profile Load(string file) => Parse(File.ReadAllText(file), file);

    public static List<string> Validate(Profile profile)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.Name)) errors.Add("campo 'name' é obrigatório.");
        if (profile.Rules is null && profile.Tweaks.Count == 0 && profile.Policies is not { Count: > 0 })
            errors.Add("o perfil precisa de 'rules', 'tweaks' ou 'policies'.");
        if (profile.Tweaks.Any(e => string.IsNullOrWhiteSpace(e.Id))) errors.Add("item de 'tweaks' sem 'id'.");
        var dup = profile.Tweaks.Where(e => !string.IsNullOrWhiteSpace(e.Id))
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) errors.Add($"id repetido em 'tweaks': {dup.Key}.");
        foreach (var p in profile.Policies ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.Policy) || !p.Policy.Contains(':'))
                errors.Add($"item de 'policies' com 'policy' inválido ('{p.Policy}'; use \"Arquivo.admx:Nome\").");
        }
        var dupPolicy = (profile.Policies ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Policy))
            .GroupBy(p => (p.Policy.Trim(), p.Scope), PolicyKeyComparer.Instance).FirstOrDefault(g => g.Count() > 1);
        if (dupPolicy is not null) errors.Add($"política repetida em 'policies': {dupPolicy.Key.Item1}.");
        return errors;
    }

    private sealed class PolicyKeyComparer : IEqualityComparer<(string, PolicyScope?)>
    {
        public static readonly PolicyKeyComparer Instance = new();
        public bool Equals((string, PolicyScope?) x, (string, PolicyScope?) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1) && x.Item2 == y.Item2;
        public int GetHashCode((string, PolicyScope?) obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1), obj.Item2);
    }

    /// <summary>
    /// Casa as políticas do perfil com as definições ADMX deste PC ("Arquivo.admx:Nome", sem diferenciar
    /// maiúsculas). Políticas que não existem aqui são devolvidas à parte.
    /// </summary>
    public static (List<PolicyRequest> Requests, List<string> Unknown) ResolvePolicies(Profile profile, IEnumerable<AdmxPolicy> admx)
    {
        var byName = new Dictionary<string, AdmxPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in admx) byName.TryAdd(p.QualifiedName, p);
        var requests = new List<PolicyRequest>();
        var unknown = new List<string>();
        foreach (var entry in profile.Policies ?? [])
        {
            var setting = entry.ToSetting();
            if (byName.TryGetValue(setting.Policy, out var policy))
            {
                setting.Policy = policy.QualifiedName;
                requests.Add(new PolicyRequest(policy, setting));
            }
            else unknown.Add(setting.Policy);
        }
        return (requests, unknown);
    }

    // Serializador próprio: enums em camelCase ("notApplied", "medium"), como se escreve à mão.
    private static readonly YamlDotNet.Serialization.ISerializer Serializer = new YamlDotNet.Serialization.SerializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .WithEnumNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(YamlDotNet.Serialization.DefaultValuesHandling.OmitNull)
        .WithQuotingNecessaryStrings()
        .Build();

    public static string Serialize(Profile profile) => Serializer.Serialize(profile);

    public static void Save(Profile profile, string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(file, Serialize(profile), new System.Text.UTF8Encoding(false));
        profile.SourceFile = file;
    }

    /// <summary>Carrega todos os perfis de uma pasta (não recursivo). Arquivos inválidos vão para <paramref name="errors"/>.</summary>
    public static List<Profile> LoadDirectory(string directory, bool builtIn, List<string>? errors = null)
    {
        var result = new List<Profile>();
        if (!Directory.Exists(directory)) return result;
        var files = Directory.EnumerateFiles(directory)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                var p = Load(file);
                p.IsBuiltIn = builtIn;
                result.Add(p);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                errors?.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// Resolve o perfil contra o catálogo: primeiro as regras (cada tweak que casa fica "aplicado"),
    /// depois a lista explícita, que prevalece. Ids que não existem no catálogo são devolvidos à parte.
    /// </summary>
    public static ProfileResolution Resolve(Profile profile, IEnumerable<Tweak> catalog)
    {
        var tweaks = catalog.ToList();
        var byId = tweaks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var desired = new Dictionary<string, DesiredState>(StringComparer.OrdinalIgnoreCase);

        if (profile.Rules is { } rules)
        {
            var excluded = new HashSet<string>(rules.ExcludeIds, StringComparer.OrdinalIgnoreCase);
            foreach (var t in tweaks)
            {
                if (t.Risk > rules.MaxRisk) continue;
                if (excluded.Contains(t.Id)) continue;
                if (!rules.IncludeIrreversible && !t.Reversible) continue;
                if (rules.Categories.Count > 0 && !rules.Categories.Any(c => CategoryMatches(t.Category, c))) continue;
                desired[t.Id] = DesiredState.Applied;
            }
        }

        var unknown = new List<string>();
        foreach (var e in profile.Tweaks)
        {
            if (!byId.TryGetValue(e.Id, out var t))
            {
                unknown.Add(e.Id);
                continue;
            }
            desired[t.Id] = e.Desired;
        }
        return new ProfileResolution(desired, unknown)
        {
            Policies = (profile.Policies ?? []).Select(p => p.ToSetting()).ToList(),
        };
    }

    /// <summary>"Privacidade" casa com "Privacidade" e "Privacidade/Telemetria", mas não com "PrivacidadeX".</summary>
    public static bool CategoryMatches(string category, string prefix)
    {
        prefix = prefix.Trim().TrimEnd('/');
        return category.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || category.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cria um perfil com lista explícita a partir de uma seleção (id → estado desejado).</summary>
    public static Profile FromSelection(string name, string? description, IEnumerable<KeyValuePair<string, DesiredState>> selection,
        IEnumerable<PolicySetting>? policies = null)
    {
        var policyList = (policies ?? [])
            .OrderBy(p => p.Policy, StringComparer.OrdinalIgnoreCase)
            .Select(ProfilePolicyEntry.FromSetting)
            .ToList();
        return new Profile
        {
            Name = name,
            Description = description,
            Tweaks = selection
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ProfileEntry { Id = kv.Key, Desired = kv.Value })
                .ToList(),
            Policies = policyList.Count > 0 ? policyList : null,
        };
    }

    /// <summary>Nome de arquivo seguro a partir do nome do perfil ("Meu perfil!" → "meu-perfil.yaml").</summary>
    public static string FileNameFor(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return (slug.Length == 0 ? "perfil" : slug) + ".yaml";
    }
}
