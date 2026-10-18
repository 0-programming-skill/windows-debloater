using Debloater.App.Helpers;
using Debloater.App.Services;
using Debloater.App.ViewModels;
using Debloater.Core.Engine;
using Debloater.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Debloater.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly AppState _app = App.State;
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        CatalogBox.Text = _app.Settings.EffectiveCatalogDirectory;
        BackupBox.Text = _app.Settings.EffectiveBackupDirectory;

        ThemeRadios.Items.Add(Loc.Get("SettingsPage_ThemeSystem"));
        ThemeRadios.Items.Add(Loc.Get("SettingsPage_ThemeLight"));
        ThemeRadios.Items.Add(Loc.Get("SettingsPage_ThemeDark"));
        ThemeRadios.SelectedIndex = (int)_app.Settings.Theme;

        // Language names are shown in their own language; only "Automatic" is translated.
        AddLanguage(Loc.Get("SettingsPage_LanguageAuto"), AppSettings.AutoLanguage);
        AddLanguage("English", UiLanguage.English);
        AddLanguage("Português (Brasil)", UiLanguage.PortugueseBrazil);
        var current = NormalizedSetting(_app.Settings.UiLanguage);
        LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? LanguageBox.Items[0];
        RestartBar.IsOpen = App.LanguageOverride is null && App.ResolveSetting(current) != App.RunningLanguage;

        LogsText.Text = Loc.Format("SettingsPage_LogsText", OperationLog.DefaultDirectory, AppSettings.ErrorLogPath);
        var version = typeof(App).Assembly.GetName().Version;
        AboutText.Text = $"Windows Debloater {version}\n"
            + Loc.Format("SettingsPage_AboutSettings", AppSettings.FilePath) + "\n"
            + Loc.Format("SettingsPage_AboutProfiles", AppSettings.UserProfilesDirectory) + "\n"
            + _app.System;
        _loading = false;
    }

    private void AddLanguage(string text, string value) => LanguageBox.Items.Add(new ComboBoxItem { Content = text, Tag = value });

    /// <summary>"auto", "en-US" or "pt-BR" (anything unsupported counts as "auto").</summary>
    private static string NormalizedSetting(string? setting)
    {
        try
        {
            return UiLanguage.Normalize(setting) ?? AppSettings.AutoLanguage;
        }
        catch (ArgumentException)
        {
            return AppSettings.AutoLanguage;
        }
    }

    private void Show(InfoBarSeverity severity, string message)
    {
        Info.Severity = severity;
        Info.Message = message;
        Info.IsOpen = true;
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(App.Window.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    private async Task SetCatalogAsync(string? dir)
    {
        _app.Settings.CatalogDirectory = dir;
        _app.Settings.Save();
        CatalogBox.Text = _app.Settings.EffectiveCatalogDirectory;
        await _app.LoadCatalogAsync();
        Show(_app.CatalogErrors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            _app.CatalogErrors.Count > 0
                ? Loc.Format("SettingsPage_CatalogReloadedErrors", _app.Tweaks.Count, _app.CatalogErrors.Count)
                : Loc.Format("SettingsPage_CatalogReloaded", _app.Tweaks.Count));
    }

    private async void BrowseCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } dir) await SetCatalogAsync(dir);
    }

    private async void DefaultCatalog_Click(object sender, RoutedEventArgs e) => await SetCatalogAsync(null);

    private async void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } dir) SetBackup(dir);
    }

    private void DefaultBackup_Click(object sender, RoutedEventArgs e) => SetBackup(null);

    private void SetBackup(string? dir)
    {
        _app.Settings.BackupDirectory = dir;
        _app.Settings.Save();
        _app.ReloadEngine();
        BackupBox.Text = _app.Settings.EffectiveBackupDirectory;
        Show(InfoBarSeverity.Success, Loc.Get("SettingsPage_BackupChanged"));
    }

    private void OpenBackup_Click(object sender, RoutedEventArgs e) => Elevation.OpenFolder(_app.Settings.EffectiveBackupDirectory);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Elevation.OpenFolder(OperationLog.DefaultDirectory);

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeRadios.SelectedIndex < 0) return;
        _app.Settings.Theme = (ThemeChoice)ThemeRadios.SelectedIndex;
        _app.Settings.Save();
        App.Window.ApplyTheme(_app.Settings.Theme);
    }

    /// <summary>Saves the language; it applies on the next start (resources and XAML are loaded once).</summary>
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        _app.Settings.UiLanguage = value;
        _app.Settings.Save();
        RestartBar.IsOpen = App.ResolveSetting(value) != App.RunningLanguage;
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (Elevation.TryRestart(out var error)) Application.Current.Exit();
        else Show(InfoBarSeverity.Error, error ?? Loc.Get("SettingsPage_RestartFailed"));
    }
}
