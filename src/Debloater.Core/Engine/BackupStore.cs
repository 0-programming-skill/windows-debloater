using System.Text.Json;
using Debloater.Core.Model;

namespace Debloater.Core.Engine;

/// <summary>Persiste backups de tweaks como JSON, um arquivo por tweak.</summary>
public sealed class BackupStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public BackupStore(string directory) => Directory = directory;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Debloater", "backups");

    public string Directory { get; }

    public string PathFor(string tweakId) => Path.Combine(Directory, tweakId + ".json");

    public bool Exists(string tweakId) => File.Exists(PathFor(tweakId));

    public TweakBackup? Load(string tweakId)
    {
        var path = PathFor(tweakId);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<TweakBackup>(File.ReadAllText(path), s_json);
    }

    public void Save(TweakBackup backup)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(backup.TweakId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(backup, s_json));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string tweakId)
    {
        var path = PathFor(tweakId);
        if (File.Exists(path)) File.Delete(path);
    }
}
