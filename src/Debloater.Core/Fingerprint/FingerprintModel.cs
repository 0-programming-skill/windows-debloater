using System.Text.Json.Serialization;
using Debloater.Core.Inventory;

namespace Debloater.Core.Fingerprint;

/// <summary>Modo da varredura de binários.</summary>
public static class BinaryScanModes
{
    /// <summary>Lista curada de binários relevantes para debloat (shell, barra, Iniciar, busca, IA...).</summary>
    public const string Default = "default";

    /// <summary>Todo PE sob as raízes.</summary>
    public const string All = "all";

    /// <summary>Sem varredura de binários.</summary>
    public const string None = "none";

    public static bool IsKnown(string mode) => mode is Default or All or None;
}

/// <summary>
/// "Impressão digital" de uma instalação do Windows: o que existe (inventário, políticas ADMX) e,
/// para cada binário relevante, os nomes de registro/políticas/CSP que ele contém. Gravada em
/// JSON compactado (gzip) por 'fingerprint create' e comparada por 'fingerprint diff'.
/// Contém só nomes, caminhos, versões e hashes; nunca conteúdo dos binários além das cadeias
/// que casam com os padrões de <see cref="StringClassifier"/>.
/// </summary>
public sealed class WindowsFingerprint
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }

    /// <summary>Raiz lida (ex.: C:\ ou a pasta de uma imagem montada).</summary>
    public string Root { get; set; } = "";

    /// <summary>true quando a raiz é o Windows em execução (inventário e registro disponíveis).</summary>
    public bool Live { get; set; }

    public bool Elevated { get; set; }
    public string? MachineName { get; set; }

    public FingerprintOs Os { get; set; } = new();

    /// <summary>Inventário (serviços, tarefas, appx...). Offline: só o que dá para ler de arquivos.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InventorySnapshot? Inventory { get; set; }

    public List<FingerprintPolicy> Policies { get; set; } = [];

    public BinaryScanInfo Binaries { get; set; } = new();

    /// <summary>Observações da coleta (o que não pôde ser lido e por quê).</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>Tempo de cada fase, em milissegundos.</summary>
    public SortedDictionary<string, long> TimingsMs { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string Label => $"{Os.FullBuild} ({Os.Edition ?? "?"}{(MachineName is null ? "" : $", {MachineName}")})";
}

public sealed class FingerprintOs
{
    public int Build { get; set; }
    public int Ubr { get; set; }
    public string? Edition { get; set; }
    public string? DisplayVersion { get; set; }
    public string? ProductName { get; set; }

    /// <summary>De onde veio (registro do sistema em execução ou versão do ntoskrnl.exe).</summary>
    public string Source { get; set; } = "";

    [JsonIgnore]
    public string FullBuild => $"{Build}.{Ubr}";
}

/// <summary>Política ADMX resumida: o que importa para comparar builds.</summary>
public sealed class FingerprintPolicy
{
    public string File { get; set; } = "";
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Key { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupportedOn { get; set; }

    /// <summary>Referência crua do supportedOn (ex.: windows:SUPPORTED_Windows_10_0_2004), independente do idioma do ADML.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupportedOnRef { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    /// <summary>Todos os valores gravados (chave\valor), inclusive de elementos e listas.</summary>
    public List<string> Values { get; set; } = [];

    [JsonIgnore]
    public string QualifiedName => $"{File}:{Name}";
}

public sealed class BinaryScanInfo
{
    public string Mode { get; set; } = BinaryScanModes.None;

    /// <summary>Pastas varridas, relativas à raiz.</summary>
    public List<string> Roots { get; set; } = [];

    public int FilesConsidered { get; set; }
    public int Errors { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>Limites usados (cadeias por arquivo).</summary>
    public int MaxStringsPerFile { get; set; }

    public List<BinaryEntry> Files { get; set; } = [];

    /// <summary>Primeiros erros de leitura (arquivo: motivo).</summary>
    public List<string> ErrorSamples { get; set; } = [];
}

/// <summary>Um binário PE e as cadeias "interessantes" encontradas nele.</summary>
public sealed class BinaryEntry
{
    /// <summary>Caminho relativo à raiz (ex.: Windows\System32\shell32.dll).</summary>
    public string Path { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    public long Size { get; set; }
    public string Sha256 { get; set; } = "";

    /// <summary>Está na lista curada (relevante para debloat)?</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Curated { get; set; }

    /// <summary>Cadeias que casam com os padrões (caminhos de registro, nomes de valores, CSP, WNF...).</summary>
    public List<string> Strings { get; set; } = [];

    /// <summary>
    /// Identificadores que não casam com os padrões, mas estão perto (no arquivo) de um caminho de
    /// registro: prováveis nomes de valores lidos daquela chave.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? NearNames { get; set; }

    /// <summary>Nome de valor → caminho de registro mais próximo no arquivo (contexto para a camada 2).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SortedDictionary<string, string>? Context { get; set; }

    /// <summary>true quando o limite de cadeias por arquivo cortou o resultado.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }

    public override string ToString() => $"{Path} {Version}";
}
