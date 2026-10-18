using System.Text.Json;
using Debloater.Core.Engine;

namespace Debloater.App.Services;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>App settings, in %LocalAppData%\Debloater\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Value of <see cref="UiLanguage"/> that follows the Windows display language.</summary>
    public const string AutoLanguage = "auto";

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Debloater");

    public static string FilePath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Unexpected app errors are appended here.</summary>
    public static string ErrorLogPath => Path.Combine(DataDirectory, "errors.log");

    public static string DefaultCatalogDirectory => Path.Combine(AppContext.BaseDirectory, "catalog");

    /// <summary>User profiles (saved and imported).</summary>
    public static string UserProfilesDirectory => Path.Combine(DataDirectory, "profiles");

    /// <summary>Catalog folder; null = the one shipped with the program.</summary>
    public string? CatalogDirectory { get; set; }

    /// <summary>Backup folder; null = %ProgramData%\Debloater\backups.</summary>
    public string? BackupDirectory { get; set; }

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public bool CreateRestorePoint { get; set; } = true;

    /// <summary>
    /// Interface language: "auto" (follow the Windows display language), "en-US" or "pt-BR". Applied at
    /// startup, so a change takes effect after a restart. (The old "Language" property is ignored.)
    /// </summary>
    public string UiLanguage { get; set; } = AutoLanguage;

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
            // Corrupted or inaccessible file: fall back to the defaults.
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
            // Does not prevent using the app.
        }
    }
}
