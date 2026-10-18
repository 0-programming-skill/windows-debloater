using Debloater.App.Helpers;
using Debloater.App.Services;
using Debloater.App.ViewModels;
using Debloater.Core.Engine;
using Debloater.Core.Localization;
using Debloater.Core.Updates;
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
        CatalogBox.Text = _app.ActiveCatalog?.CatalogDirectory ?? _app.Settings.EffectiveCatalogDirectory;
        AutoUpdateToggle.IsOn = _app.Settings.AutoCatalogUpdates;
        UpdateCatalogInfo();
        _app.CatalogReloaded += OnCatalogReloaded;
        _app.CatalogUpdateFinished += OnCatalogUpdateFinished;
        _app.PropertyChanged += OnAppPropertyChanged;
        Unloaded += (_, _) =>
        {
            _app.CatalogReloaded -= OnCatalogReloaded;
            _app.CatalogUpdateFinished -= OnCatalogUpdateFinished;
            _app.PropertyChanged -= OnAppPropertyChanged;
        };
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
        await _app.LoadCatalogAsync();
        Show(_app.CatalogErrors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            _app.CatalogErrors.Count > 0
                ? Loc.Format("SettingsPage_CatalogReloadedErrors", _app.Tweaks.Count, _app.CatalogErrors.Count)
                : Loc.Format("SettingsPage_CatalogReloaded", _app.Tweaks.Count));
    }

    private void OnCatalogReloaded(object? sender, EventArgs e)
    {
        CatalogBox.Text = _app.ActiveCatalog?.CatalogDirectory ?? _app.Settings.EffectiveCatalogDirectory;
        UpdateCatalogInfo();
    }

    private void OnCatalogUpdateFinished(object? sender, CatalogUpdateEventArgs e) => UpdateCatalogInfo();

    private void OnAppPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.IsCheckingCatalog)) UpdateCatalogInfo();
    }

    // ---------------- catalog updates ----------------

    private static string OutcomeText(string? outcome) => outcome switch
    {
        nameof(CatalogUpdateOutcome.UpToDate) => Loc.Get("CatalogOutcome_UpToDate"),
        nameof(CatalogUpdateOutcome.UpdateAvailable) => Loc.Get("CatalogOutcome_UpdateAvailable"),
        nameof(CatalogUpdateOutcome.Installed) => Loc.Get("CatalogOutcome_Installed"),
        nameof(CatalogUpdateOutcome.Incompatible) => Loc.Get("CatalogOutcome_Incompatible"),
        nameof(CatalogUpdateOutcome.Skipped) => Loc.Get("CatalogOutcome_Skipped"),
        _ => Loc.Get("CatalogOutcome_Failed"),
    };

    /// <summary>Active catalog (version and source), built-in version, last check; buttons follow the state.</summary>
    private void UpdateCatalogInfo()
    {
        var active = _app.ActiveCatalog;
        var source = active?.Source switch
        {
            CatalogSource.Updated => Loc.Get("SettingsPage_CatalogSourceUpdated"),
            CatalogSource.Custom => Loc.Get("SettingsPage_CatalogSourceCustom"),
            _ => Loc.Get("SettingsPage_CatalogSourceBuiltIn"),
        };
        CatalogVersionText.Text = Loc.Format("SettingsPage_CatalogActive", active?.Version?.Text ?? "-", source);

        var lines = new List<string> { Loc.Format("SettingsPage_CatalogBuiltInVersion", CatalogVersion.BuiltIn) };
        CatalogUpdateState state;
        try
        {
            state = new CatalogStore().ReadState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state = new CatalogUpdateState();
        }
        lines.Add(state.LastCheckUtc is { } last
            ? Loc.Format("SettingsPage_CatalogLastCheck", last.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture), OutcomeText(state.LastOutcome))
            : Loc.Get("SettingsPage_CatalogLastCheckNever"));
        if (active?.Warning is { } warning) lines.Add(Loc.Format("SettingsPage_CatalogWarning", warning));
        CatalogDetailsText.Text = string.Join(Environment.NewLine, lines);

        bool custom = active?.Source == CatalogSource.Custom;
        CheckNowButton.IsEnabled = !custom && !_app.IsCheckingCatalog;
        RollbackButton.IsEnabled = active?.Source == CatalogSource.Updated && !_app.IsCheckingCatalog;
        CheckProgress.IsActive = _app.IsCheckingCatalog;
    }

    private void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.AutoCatalogUpdates = AutoUpdateToggle.IsOn;
        _app.Settings.Save();
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = RollbackButton.IsEnabled = false;
        CheckProgress.IsActive = true;
        var result = await _app.CheckCatalogUpdateAsync(automatic: false);
        UpdateCatalogInfo();
        if (result is null) return;
        var latest = result.Latest?.Text ?? "";
        switch (result.Outcome)
        {
            case CatalogUpdateOutcome.UpToDate:
                Show(InfoBarSeverity.Success, Loc.Format("CatalogUpdate_UpToDate", result.Current));
                break;
            case CatalogUpdateOutcome.Installed:
                Show(InfoBarSeverity.Success, Loc.Format("CatalogUpdate_Installed", latest));
                break;
            case CatalogUpdateOutcome.Incompatible:
                Show(InfoBarSeverity.Warning, Loc.Format("CatalogUpdate_Incompatible", latest));
                break;
            case CatalogUpdateOutcome.Skipped:
                Show(InfoBarSeverity.Informational, Loc.Format("CatalogUpdate_Skipped", latest));
                break;
            default:
                Show(InfoBarSeverity.Warning, Loc.Format("CatalogUpdate_Failed", result.Message));
                break;
        }
    }

    private async void RollbackBuiltIn_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = RollbackButton.IsEnabled = false;
        try
        {
            await _app.RollbackCatalogToBuiltInAsync();
            Show(InfoBarSeverity.Success, Loc.Format("CatalogUpdate_RolledBack", CatalogVersion.BuiltIn));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show(InfoBarSeverity.Error, Loc.Format("CatalogUpdate_RollbackFailed", ex.Message));
        }
        UpdateCatalogInfo();
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
