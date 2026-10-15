using System.Text.Json.Serialization;

namespace Debloater.Core.Reference;

/// <summary>Um valor permitido documentado de uma política da Policy CSP.</summary>
public sealed class CspAllowedValue
{
    public string Value { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDefault { get; set; }

    /// <summary>Descrição curta (truncada) da documentação.</summary>
    public string Description { get; set; } = "";
}

/// <summary>Mapeamento para Política de Grupo ("Group policy mapping" / "ADMX mapping").</summary>
public sealed class CspGpMapping
{
    public string? Name { get; set; }
    public string? FriendlyName { get; set; }
    public string? Location { get; set; }
    public string? Path { get; set; }
    public string? RegistryKey { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegistryValueName { get; set; }

    public string? AdmxFile { get; set; }
}

/// <summary>Uma política documentada em learn.microsoft.com/windows/client-management/mdm/policy-csp-*.</summary>
public sealed class CspPolicy
{
    /// <summary>Área (ex.: System, Experience, ADMX_DataCollection).</summary>
    public string Area { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Página de origem (ex.: policy-csp-system).</summary>
    public string Page { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceUri { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserUri { get; set; }

    public bool ScopeDevice { get; set; }
    public bool ScopeUser { get; set; }

    /// <summary>Edições marcadas com ✅.</summary>
    public List<string> Editions { get; set; } = [];

    /// <summary>Edições marcadas com ❌.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? NotEditions { get; set; }

    /// <summary>Linhas de "Applicable OS" (✅), como na documentação.</summary>
    public List<string> Applicability { get; set; } = [];

    /// <summary>Build mínimo no Windows 11 (null = todo o Windows 11). Ver <see cref="CspMarkdownParser.ComputeWin11MinBuild"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Win11MinBuild { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Win11MinUbr { get; set; }

    /// <summary>Disponível só em builds do Windows Insider.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InsiderOnly { get; set; }

    /// <summary>A documentação diz que a política está obsoleta (deprecated).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deprecated { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AllowedRange { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CspAllowedValue>? AllowedValues { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CspGpMapping>? GpMappings { get; set; }

    /// <summary>Política "ADMX-backed" (configurada via SyncML com o ADMX).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AdmxBacked { get; set; }

    /// <summary>Frases da documentação que restringem edição (ex.: "only supported on Enterprise...").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? EditionNotes { get; set; }

    /// <summary>"Policy CSP - Area/Nome".</summary>
    [JsonIgnore]
    public string Reference => $"Policy CSP - {Area}/{Name}";

    [JsonIgnore]
    public string Url => $"https://learn.microsoft.com/windows/client-management/mdm/{Page}#{Name.ToLowerInvariant()}";

    public override string ToString() => Reference;
}

/// <summary>Uma página processada (para rastrear mudanças da documentação).</summary>
public sealed class CspPage
{
    public string Page { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MsDate { get; set; }

    public int PolicyCount { get; set; }
}

/// <summary>Arquivo reference/msdocs-policy-csp.json.</summary>
public sealed class CspReference
{
    public int SchemaVersion { get; set; } = 1;
    public string Source { get; set; } = "https://learn.microsoft.com/windows/client-management/mdm/ (Accept: text/markdown)";
    public string Note { get; set; } = "Gerado por tools/Debloater.RefGen. Não edite à mão.";
    public List<CspPage> Pages { get; set; } = [];
    public List<CspPolicy> Policies { get; set; } = [];
}
