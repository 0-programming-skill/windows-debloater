using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Debloater.Core.Admx;

/// <summary>
/// Lê uma pasta PolicyDefinitions (*.admx + &lt;idioma&gt;\*.adml) e produz o modelo de cada política.
/// Resolve $(string.X) e $(presentation.X) pelo ADML (preferindo pt-BR, depois en-US), categorias
/// com parentCategory entre arquivos (prefixos de namespace) e definições de supportedOn.
/// </summary>
public static class AdmxParser
{
    public const string PolicyDefinitionsNs = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    public static IReadOnlyList<string> DefaultLanguages { get; } = ["pt-BR", "en-US"];

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "PolicyDefinitions");

    private static readonly XNamespace Ns = PolicyDefinitionsNs;

    private sealed class FileData
    {
        public required string FileName { get; init; }
        public required XElement Root { get; init; }
        public required string TargetNamespace { get; init; }
        public required Dictionary<string, string> Prefixes { get; init; }
        public required Dictionary<string, string> Strings { get; init; }
        public required Dictionary<string, Dictionary<string, string>> Presentations { get; init; }
    }

    private sealed record CategoryDef(string DisplayName, (string Ns, string Name)? Parent);

    /// <summary>Lê todos os *.admx de <paramref name="directory"/>.</summary>
    public static AdmxCatalog ParseDirectory(string directory, IEnumerable<string>? languages = null)
    {
        var langs = (languages ?? DefaultLanguages).ToList();
        if (!langs.Contains("en-US", StringComparer.OrdinalIgnoreCase)) langs.Add("en-US");
        var catalog = new AdmxCatalog { Directory = directory };
        if (!Directory.Exists(directory))
        {
            catalog.Errors.Add($"pasta não encontrada: {directory}");
            return catalog;
        }

        var sources = new List<(string FileName, Func<string> Admx, Func<string?> Adml)>();
        var usedLangs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var admx in Directory.EnumerateFiles(directory, "*.admx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var baseName = Path.GetFileNameWithoutExtension(admx);
            string? admlPath = FindAdml(directory, baseName, langs, out var lang);
            if (lang is not null) usedLangs.Add(lang);
            sources.Add((Path.GetFileName(admx), () => File.ReadAllText(admx), () => admlPath is null ? null : File.ReadAllText(admlPath)));
        }
        var result = Parse(sources);
        result.Directory = directory;
        result.Languages = [.. usedLangs];
        return result;
    }

    private static string? FindAdml(string directory, string baseName, IReadOnlyList<string> langs, out string? lang)
    {
        foreach (var l in langs)
        {
            var p = Path.Combine(directory, l, baseName + ".adml");
            if (File.Exists(p)) { lang = l; return p; }
        }
        foreach (var sub in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var p = Path.Combine(sub, baseName + ".adml");
            if (File.Exists(p)) { lang = Path.GetFileName(sub); return p; }
        }
        lang = null;
        return null;
    }

    /// <summary>Lê a partir de pares (nome do .admx, conteúdo ADMX, conteúdo ADML ou null). Útil para testes.</summary>
    public static AdmxCatalog Parse(IEnumerable<(string FileName, Func<string> Admx, Func<string?> Adml)> sources)
    {
        var catalog = new AdmxCatalog();
        var files = new List<FileData>();
        var categories = new Dictionary<(string, string), CategoryDef>();
        var supported = new Dictionary<(string, string), string>();

        // 1ª passada: namespaces, textos, categorias e definições de supportedOn de todos os arquivos.
        foreach (var (fileName, readAdmx, readAdml) in sources)
        {
            catalog.FileCount++;
            try
            {
                var root = LoadXml(readAdmx());
                var nsEl = root.Element(Ns + "policyNamespaces");
                var target = nsEl?.Element(Ns + "target");
                if (target is null)
                {
                    catalog.Errors.Add($"{fileName}: sem <policyNamespaces><target>.");
                    continue;
                }
                var targetNs = (string?)target.Attribute("namespace") ?? "";
                var prefixes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [(string?)target.Attribute("prefix") ?? ""] = targetNs,
                };
                foreach (var u in nsEl!.Elements(Ns + "using"))
                    prefixes[(string?)u.Attribute("prefix") ?? ""] = (string?)u.Attribute("namespace") ?? "";

                var (strings, presentations) = ParseAdml(readAdml(), fileName, catalog.Errors);
                var file = new FileData
                {
                    FileName = fileName,
                    Root = root,
                    TargetNamespace = targetNs,
                    Prefixes = prefixes,
                    Strings = strings,
                    Presentations = presentations,
                };
                files.Add(file);

                foreach (var cat in root.Element(Ns + "categories")?.Elements(Ns + "category") ?? [])
                {
                    var name = (string?)cat.Attribute("name") ?? "";
                    var parentRef = (string?)cat.Element(Ns + "parentCategory")?.Attribute("ref");
                    categories[(targetNs, name)] = new CategoryDef(
                        Clean(ResolveString(file, (string?)cat.Attribute("displayName")) ?? name),
                        parentRef is null ? null : ResolveRef(file, parentRef));
                }
                foreach (var def in root.Element(Ns + "supportedOn")?.Element(Ns + "definitions")?.Elements(Ns + "definition") ?? [])
                {
                    var name = (string?)def.Attribute("name") ?? "";
                    supported[(targetNs, name)] = ResolveString(file, (string?)def.Attribute("displayName")) ?? name;
                }
            }
            catch (XmlException ex)
            {
                catalog.Errors.Add($"{fileName}: XML inválido: {ex.Message}");
            }
        }

        // 2ª passada: políticas.
        foreach (var file in files)
        {
            foreach (var p in file.Root.Element(Ns + "policies")?.Elements(Ns + "policy") ?? [])
            {
                try
                {
                    catalog.Policies.Add(ParsePolicy(file, p, categories, supported));
                }
                catch (Exception ex) when (ex is FormatException or XmlException or ArgumentException)
                {
                    catalog.Errors.Add($"{file.FileName}: política '{(string?)p.Attribute("name")}': {ex.Message}");
                }
            }
        }
        return catalog;
    }

    private static XElement LoadXml(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        var root = XDocument.Load(reader).Root ?? throw new XmlException("documento vazio");
        // Alguns ADMX (ex.: Camera.admx) não declaram o namespace padrão; normalizamos para o do esquema.
        // Outros usam um namespace diferente (GameDVR.admx: http://www.microsoft.com/GroupPolicy/PolicyDefinitions).
        var rootNs = root.Name.Namespace;
        if (rootNs != Ns)
            foreach (var el in root.DescendantsAndSelf().Where(e => e.Name.Namespace == rootNs))
                el.Name = Ns + el.Name.LocalName;
        return root;
    }

    private static (Dictionary<string, string>, Dictionary<string, Dictionary<string, string>>) ParseAdml(string? text, string fileName, List<string> errors)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var presentations = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (text is null)
        {
            errors.Add($"{fileName}: ADML não encontrado; textos ficarão como referências.");
            return (strings, presentations);
        }
        XElement root;
        try { root = LoadXml(text); }
        catch (XmlException ex)
        {
            errors.Add($"{fileName}: ADML inválido: {ex.Message}");
            return (strings, presentations);
        }
        // O ADML usa o mesmo namespace; ignoramos o namespace por robustez.
        var resources = root.Elements().FirstOrDefault(e => e.Name.LocalName == "resources");
        foreach (var s in resources?.Elements().FirstOrDefault(e => e.Name.LocalName == "stringTable")?.Elements() ?? [])
        {
            if ((string?)s.Attribute("id") is { } id) strings[id] = s.Value;
        }
        foreach (var pres in resources?.Elements().FirstOrDefault(e => e.Name.LocalName == "presentationTable")?.Elements() ?? [])
        {
            if ((string?)pres.Attribute("id") is not { } id) continue;
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var control in pres.Elements())
            {
                if ((string?)control.Attribute("refId") is not { } refId) continue;
                var label = control.Elements().FirstOrDefault(e => e.Name.LocalName == "label")?.Value
                    ?? (control.HasElements ? null : control.Value);
                if (!string.IsNullOrWhiteSpace(label)) labels[refId] = label.Trim();
            }
            presentations[id] = labels;
        }
        return (strings, presentations);
    }

    private static string? ResolveString(FileData file, string? reference)
    {
        if (reference is null) return null;
        const string prefix = "$(string.";
        if (reference.StartsWith(prefix, StringComparison.Ordinal) && reference.EndsWith(')'))
        {
            var id = reference[prefix.Length..^1];
            return file.Strings.TryGetValue(id, out var text) ? text : reference;
        }
        return reference;
    }

    private static (string Ns, string Name) ResolveRef(FileData file, string reference)
    {
        int idx = reference.IndexOf(':');
        if (idx < 0) return (file.TargetNamespace, reference);
        var prefix = reference[..idx];
        var ns = file.Prefixes.TryGetValue(prefix, out var n) ? n : prefix;
        return (ns, reference[(idx + 1)..]);
    }

    private static AdmxPolicy ParsePolicy(FileData file, XElement p,
        Dictionary<(string, string), CategoryDef> categories, Dictionary<(string, string), string> supported)
    {
        var key = (string?)p.Attribute("key") ?? "";
        var policy = new AdmxPolicy
        {
            File = file.FileName,
            Namespace = file.TargetNamespace,
            Name = (string?)p.Attribute("name") ?? "",
            Class = Enum.TryParse<AdmxClass>((string?)p.Attribute("class"), ignoreCase: true, out var c) ? c : AdmxClass.Machine,
            DisplayName = Clean(ResolveString(file, (string?)p.Attribute("displayName"))),
            ExplainText = ResolveString(file, (string?)p.Attribute("explainText")) ?? "",
            Key = key,
            ValueName = (string?)p.Attribute("valueName"),
        };

        if ((string?)p.Element(Ns + "parentCategory")?.Attribute("ref") is { } catRef)
        {
            var path = new List<string>();
            var current = ResolveRef(file, catRef);
            var guard = new HashSet<(string, string)>();
            while (guard.Add(current))
            {
                if (!categories.TryGetValue(current, out var def))
                {
                    path.Add(current.Name);
                    break;
                }
                path.Add(def.DisplayName);
                if (def.Parent is not { } parent) break;
                current = parent;
            }
            path.Reverse();
            policy.CategoryPath = path;
        }

        if ((string?)p.Element(Ns + "supportedOn")?.Attribute("ref") is { } supRef)
        {
            policy.SupportedOnRef = supRef;
            var resolved = ResolveRef(file, supRef);
            policy.SupportedOn = supported.TryGetValue(resolved, out var text) ? text : supRef;
        }

        policy.EnabledValue = ParseValueContainer(p.Element(Ns + "enabledValue"));
        policy.DisabledValue = ParseValueContainer(p.Element(Ns + "disabledValue"));
        policy.EnabledList = ParseList(p.Element(Ns + "enabledList"), key);
        policy.DisabledList = ParseList(p.Element(Ns + "disabledList"), key);

        Dictionary<string, string>? labels = null;
        var presRef = (string?)p.Attribute("presentation");
        if (presRef is not null && presRef.StartsWith("$(presentation.", StringComparison.Ordinal))
            file.Presentations.TryGetValue(presRef["$(presentation.".Length..^1], out labels);

        foreach (var e in p.Element(Ns + "elements")?.Elements() ?? [])
            policy.Elements.Add(ParseElement(file, e, key, labels));

        return policy;
    }

    private static AdmxElement ParseElement(FileData file, XElement e, string policyKey, Dictionary<string, string>? labels)
    {
        var id = (string?)e.Attribute("id") ?? "";
        var el = new AdmxElement
        {
            Type = e.Name.LocalName,
            Id = id,
            Key = (string?)e.Attribute("key") ?? policyKey,
            ValueName = (string?)e.Attribute("valueName"),
            Required = (bool?)Bool(e.Attribute("required")) ?? false,
            MinValue = ULong(e.Attribute("minValue")),
            MaxValue = ULong(e.Attribute("maxValue")),
            StoreAsText = Bool(e.Attribute("storeAsText")) ?? false,
            Expandable = Bool(e.Attribute("expandable")) ?? false,
            MaxLength = (int?)ULong(e.Attribute("maxLength")),
            ValuePrefix = (string?)e.Attribute("valuePrefix"),
            Additive = Bool(e.Attribute("additive")) ?? false,
            ExplicitValue = Bool(e.Attribute("explicitValue")) ?? false,
        };
        if (labels is not null && labels.TryGetValue(id, out var label)) el.Label = label;

        switch (el.Type)
        {
            case "decimal":
            case "longDecimal":
                // Sem minValue/maxValue o padrão do esquema é 0..9999 para decimal.
                if (el.Type == "decimal")
                {
                    el.MinValue ??= 0;
                    el.MaxValue ??= 9999;
                }
                break;
            case "boolean":
                el.TrueValue = ParseValueContainer(e.Element(Ns + "trueValue"));
                el.FalseValue = ParseValueContainer(e.Element(Ns + "falseValue"));
                el.TrueList = ParseList(e.Element(Ns + "trueList"), el.Key);
                el.FalseList = ParseList(e.Element(Ns + "falseList"), el.Key);
                break;
            case "enum":
                el.Items = [];
                foreach (var item in e.Elements(Ns + "item"))
                {
                    el.Items.Add(new AdmxEnumItem
                    {
                        DisplayName = Clean(ResolveString(file, (string?)item.Attribute("displayName"))),
                        Value = ParseValueContainer(item.Element(Ns + "value")) ?? new AdmxValue(AdmxValueKind.Delete, null),
                        ValueList = ParseList(item.Element(Ns + "valueList"), el.Key),
                    });
                }
                break;
        }
        return el;
    }

    /// <summary>Lê o filho de &lt;value&gt;, &lt;enabledValue&gt;, &lt;trueValue&gt;...</summary>
    private static AdmxValue? ParseValueContainer(XElement? container)
    {
        var child = container?.Elements().FirstOrDefault();
        if (child is null) return null;
        return child.Name.LocalName switch
        {
            "decimal" => new AdmxValue(AdmxValueKind.Decimal, (string?)child.Attribute("value") ?? "0"),
            "longDecimal" => new AdmxValue(AdmxValueKind.LongDecimal, (string?)child.Attribute("value") ?? "0"),
            "string" => new AdmxValue(AdmxValueKind.String, child.Value),
            "delete" => new AdmxValue(AdmxValueKind.Delete, null),
            var other => throw new FormatException($"tipo de valor desconhecido <{other}>"),
        };
    }

    private static List<AdmxListItem>? ParseList(XElement? list, string policyKey)
    {
        if (list is null) return null;
        var defaultKey = (string?)list.Attribute("defaultKey") ?? policyKey;
        var items = new List<AdmxListItem>();
        foreach (var item in list.Elements(Ns + "item"))
        {
            items.Add(new AdmxListItem(
                (string?)item.Attribute("key") ?? defaultKey,
                (string?)item.Attribute("valueName") ?? "",
                ParseValueContainer(item.Element(Ns + "value")) ?? new AdmxValue(AdmxValueKind.Delete, null)));
        }
        return items;
    }

    /// <summary>Nomes exibidos em uma linha (alguns ADML têm quebras e espaços extras).</summary>
    private static string Clean(string? text) =>
        text is null ? "" : string.Join(' ', text.Split(s_whitespace, StringSplitOptions.RemoveEmptyEntries));

    private static readonly char[] s_whitespace = [(char)13, (char)10, (char)9, ' '];

    private static bool? Bool(XAttribute? a) => a is null ? null : string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase);

    private static ulong? ULong(XAttribute? a) =>
        a is not null && ulong.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
