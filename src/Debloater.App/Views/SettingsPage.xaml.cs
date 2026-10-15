using Debloater.App.Services;
using Debloater.App.ViewModels;
using Debloater.Core.Engine;
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
        ThemeRadios.SelectedIndex = (int)_app.Settings.Theme;
        LogsText.Text = $"Cada sessão de aplicar/reverter grava um arquivo em {OperationLog.DefaultDirectory}. Erros inesperados do app vão para {Path.Combine(AppSettings.DataDirectory, "erros.log")}.";
        var version = typeof(App).Assembly.GetName().Version;
        AboutText.Text = $"Windows Debloater {version}\nConfigurações: {AppSettings.FilePath}\nPerfis do usuário: {AppSettings.UserProfilesDirectory}\n{_app.System}";
        _loading = false;
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
            $"Catálogo recarregado: {_app.Tweaks.Count} ajuste(s)" + (_app.CatalogErrors.Count > 0 ? $", {_app.CatalogErrors.Count} erro(s)." : "."));
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
        Show(InfoBarSeverity.Success, "Pasta de backups alterada. Reverter só funciona para ajustes com backup nesta pasta.");
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
}
