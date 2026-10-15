using System.Text.Json;
using Debloater.Core.Engine;

namespace Debloater.App.Services;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>Configurações do app, em %LocalAppData%\Debloater\settings.json.</summary>
public sealed class AppSettings
{
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Debloater");

    public static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public static string DefaultCatalogDirectory => Path.Combine(AppContext.BaseDirectory, "catalog");

    /// <summary>Perfis do usuário (salvos e importados).</summary>
    public static string UserProfilesDirectory => Path.Combine(DataDirectory, "profiles");

    /// <summary>Pasta do catálogo; nulo = a que acompanha o programa.</summary>
    public string? CatalogDirectory { get; set; }

    /// <summary>Pasta dos backups; nulo = %ProgramData%\Debloater\backups.</summary>
    public string? BackupDirectory { get; set; }

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public bool CreateRestorePoint { get; set; } = true;

    public string Language { get; set; } = "pt-BR";

    public string EffectiveCatalogDirectory => string.IsNullOrWhiteSpace(CatalogDirectory) ? DefaultCatalogDirectory : CatalogDirectory!;

    public string EffectiveBackupDirectory => string.IsNullOrWhiteSpace(BackupDirectory) ? BackupStore.DefaultDirectory : BackupDirectory!;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Arquivo corrompido ou inacessível: volta ao padrão.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Não impede o uso do app.
        }
    }
}
