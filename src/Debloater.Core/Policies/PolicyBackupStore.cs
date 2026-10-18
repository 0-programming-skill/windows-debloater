using System.Text.Json;
using System.Text.Json.Serialization;
using Debloater.Core.Engine;

namespace Debloater.Core.Policies;

/// <summary>Valor original de um valor de registro tocado por uma política.</summary>
public sealed class PolicyValueBackup
{
    /// <summary>HKLM ou HKCU.</summary>
    public string Root { get; set; } = "";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MultiData { get; set; }
}

/// <summary>Uma chave que a aplicação criou (apagada na reversão se ficar vazia) ou apagou (recriada).</summary>
public sealed class PolicyKeyBackup
{
    public string Root { get; set; } = "";
    public string Key { get; set; } = "";
}

/// <summary>Backup de uma política (%ProgramData%\Debloater\backups\policies\&lt;arquivo&gt;__&lt;nome&gt;.json).</summary>
public sealed class PolicyBackup
{
    /// <summary>"Arquivo.admx:Nome".</summary>
    public string Policy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowsBuild { get; set; }

    /// <summary>Valor ORIGINAL de cada valor tocado (o primeiro capturado; re-aplicações não sobrescrevem).</summary>
    public List<PolicyValueBackup> Values { get; set; } = [];

    /// <summary>Chaves que não existiam (a mais alta de cada caminho criado).</summary>
    public List<PolicyKeyBackup> CreatedKeys { get; set; } = [];

    /// <summary>Chaves de lista que existiam e foram apagadas (vazias) em "Não configurada".</summary>
    public List<PolicyKeyBackup> DeletedKeys { get; set; } = [];

    /// <summary>Histórico curto do que foi aplicado (texto), para auditoria.</summary>
    public List<string> History { get; set; } = [];

    public PolicyValueBackup? Find(string root, string key, string name) => Values.FirstOrDefault(v =>
        v.Root.Equals(root, StringComparison.OrdinalIgnoreCase)
        && v.Key.Trim('\\').Equals(key.Trim('\\'), StringComparison.OrdinalIgnoreCase)
        && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Persiste backups de políticas como JSON, na subpasta "policies" da pasta de backups dos tweaks.</summary>
public sealed class PolicyBackupStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="backupRoot">Pasta de backups dos tweaks (padrão: %ProgramData%\Debloater\backups).</param>
    public PolicyBackupStore(string? backupRoot = null) =>
        Directory = Path.Combine(backupRoot ?? BackupStore.DefaultDirectory, "policies");

    public string Directory { get; }

    /// <summary>"DataCollection.admx:AllowTelemetry" → DataCollection.admx__AllowTelemetry.json (caracteres inválidos viram "_").</summary>
    public string PathFor(string qualifiedName)
    {
        var name = qualifiedName.Replace(":", "__");
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(Directory, name + ".json");
    }

    public bool Exists(string qualifiedName) => File.Exists(PathFor(qualifiedName));

    public PolicyBackup? Load(string qualifiedName)
    {
        var path = PathFor(qualifiedName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PolicyBackup>(File.ReadAllText(path), s_json);
    }

    public void Save(PolicyBackup backup)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(backup.Policy);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(backup, s_json));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string qualifiedName)
    {
        var path = PathFor(qualifiedName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Nomes qualificados de todas as políticas com backup.</summary>
    public IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var result = new List<string>();
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<PolicyBackup>(File.ReadAllText(f), s_json) is { Policy.Length: > 0 } b) result.Add(b.Policy);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return result;
    }
}
