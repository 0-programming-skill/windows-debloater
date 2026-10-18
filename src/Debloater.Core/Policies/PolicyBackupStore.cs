using System.Text.Json;
using System.Text.Json.Serialization;
using Debloater.Core.Engine;

namespace Debloater.Core.Policies;

/// <summary>Original value of a registry value touched by a policy.</summary>
public sealed class PolicyValueBackup
{
    /// <summary>HKLM or HKCU.</summary>
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

/// <summary>A key the apply created (deleted on revert if left empty) or deleted (recreated).</summary>
public sealed class PolicyKeyBackup
{
    public string Root { get; set; } = "";
    public string Key { get; set; } = "";
}

/// <summary>Backup of a policy (%ProgramData%\Debloater\backups\policies\&lt;file&gt;__&lt;name&gt;.json).</summary>
public sealed class PolicyBackup
{
    /// <summary>"File.admx:Name".</summary>
    public string Policy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowsBuild { get; set; }

    /// <summary>ORIGINAL value of each touched value (the first one captured; re-applies do not overwrite it).</summary>
    public List<PolicyValueBackup> Values { get; set; } = [];

    /// <summary>Keys that did not exist (the topmost of each created path).</summary>
    public List<PolicyKeyBackup> CreatedKeys { get; set; } = [];

    /// <summary>List keys that existed and were deleted (empty) on "Not configured".</summary>
    public List<PolicyKeyBackup> DeletedKeys { get; set; } = [];

    /// <summary>Short history of what was applied (text), for auditing.</summary>
    public List<string> History { get; set; } = [];

    public PolicyValueBackup? Find(string root, string key, string name) => Values.FirstOrDefault(v =>
        v.Root.Equals(root, StringComparison.OrdinalIgnoreCase)
        && v.Key.Trim('\\').Equals(key.Trim('\\'), StringComparison.OrdinalIgnoreCase)
        && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Persists policy backups as JSON, in the "policies" subfolder of the tweak backup folder.</summary>
public sealed class PolicyBackupStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="backupRoot">Tweak backup folder (default: %ProgramData%\Debloater\backups).</param>
    public PolicyBackupStore(string? backupRoot = null) =>
        Directory = Path.Combine(backupRoot ?? BackupStore.DefaultDirectory, "policies");

    public string Directory { get; }

    /// <summary>"DataCollection.admx:AllowTelemetry" → DataCollection.admx__AllowTelemetry.json (invalid characters become "_").</summary>
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

    /// <summary>Qualified names of all policies with a backup.</summary>
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
