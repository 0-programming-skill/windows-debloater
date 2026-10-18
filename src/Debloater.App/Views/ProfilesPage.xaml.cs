using Debloater.App.Services;
using Debloater.App.ViewModels;
using Debloater.Core.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;

namespace Debloater.App.Views;

/// <summary>Perfis embutidos e do usuário: carregar, salvar, importar e exportar.</summary>
public sealed partial class ProfilesPage : Page
{
    private readonly AppState _app = App.State;

    public ProfilesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _app.LoadProfiles();
        Rebuild();
        if (_app.ProfileErrors.Count > 0) Show(InfoBarSeverity.Warning, "Perfis com erro foram ignorados", string.Join(Environment.NewLine, _app.ProfileErrors));
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        Info.Severity = severity;
        Info.Title = title;
        Info.Message = message;
        Info.IsOpen = true;
    }

    private void Rebuild()
    {
        ProfileList.Children.Clear();
        foreach (var p in _app.Profiles) ProfileList.Children.Add(BuildCard(p));
    }

    private Border BuildCard(Profile p)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 2 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = p.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        title.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["BadgeBorder"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new TextBlock { Text = p.IsBuiltIn ? "Embutido" : "Meu perfil", Style = (Style)Application.Current.Resources["BadgeText"] },
        });
        info.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(p.Description))
            info.Children.Add(new TextBlock { Text = p.Description.Trim(), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Caption"] });
        var resolution = ProfileStore.Resolve(p, _app.Tweaks.Select(t => t.Tweak));
        var rules = p.Rules is { } r
            ? $"Regras: risco até {Helpers.Labels.RiskShort(r.MaxRisk).ToLowerInvariant()}" +
              (r.Categories.Count > 0 ? $", categorias {string.Join(", ", r.Categories)}" : ", todas as categorias") +
              (r.IncludeIrreversible ? "" : ", sem ajustes irreversíveis") + ". "
            : "";
        info.Children.Add(new TextBlock
        {
            Text = $"{rules}{resolution.Desired.Count} ajuste(s)"
                + (p.Policies is { Count: > 0 } pol ? $" e {pol.Count} política(s) ADMX" : "")
                + $" no perfil · {p.SourceFile}",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["Caption"],
        });
        grid.Children.Add(info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var load = new Button { Content = "Carregar", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        load.Click += async (_, _) =>
        {
            var msg = await _app.ApplyProfileToSelectionAsync(p);
            Show(InfoBarSeverity.Success, "Seleção atualizada", msg);
            if (_app.PendingCount > 0)
            {
                var review = new Button { Content = "Revisar mudanças" };
                review.Click += (_, _) => App.Window.Navigate("apply");
                Info.ActionButton = review;
            }
            else Info.ActionButton = null;
        };
        var export = new Button { Content = "Exportar..." };
        export.Click += async (_, _) => await ExportAsync(p);
        buttons.Children.Add(load);
        buttons.Children.Add(export);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return new Border { Style = (Style)Application.Current.Resources["Card"], Child = grid };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Show(InfoBarSeverity.Warning, "Informe um nome", "Dê um nome ao perfil antes de salvar.");
            return;
        }
        var selection = _app.EffectiveSelection();
        var policies = await _app.EffectivePolicySettingsAsync();
        if (selection.Count == 0 && policies.Count == 0)
        {
            Show(InfoBarSeverity.Warning, "Seleção vazia", "Nenhum ajuste nem política aplicado ou escolhido; não há o que salvar.");
            return;
        }
        var profile = ProfileStore.FromSelection(name, string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(), selection, policies);
        var file = Path.Combine(AppSettings.UserProfilesDirectory, ProfileStore.FileNameFor(name));
        try
        {
            ProfileStore.Save(profile, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show(InfoBarSeverity.Error, "Falha ao salvar", ex.Message);
            return;
        }
        _app.LoadProfiles();
        Rebuild();
        Info.ActionButton = null;
        Show(InfoBarSeverity.Success, "Perfil salvo", $"{selection.Count} ajuste(s) e {policies.Count} política(s) gravados em {file}.");
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(App.Window.AppWindow.Id);
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");
        var result = await picker.PickSingleFileAsync();
        if (result is null) return;
        Info.ActionButton = null;
        try
        {
            var profile = ProfileStore.Load(result.Path);
            var dest = Path.Combine(AppSettings.UserProfilesDirectory, ProfileStore.FileNameFor(profile.Name));
            Directory.CreateDirectory(AppSettings.UserProfilesDirectory);
            File.Copy(result.Path, dest, overwrite: true);
            _app.LoadProfiles();
            Rebuild();
            Show(InfoBarSeverity.Success, "Perfil importado", $"\"{profile.Name}\" copiado para {dest}. Use \"Carregar\" para aplicá-lo à seleção.");
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Show(InfoBarSeverity.Error, "Não foi possível importar", ex.Message);
        }
    }

    private async Task ExportAsync(Profile p)
    {
        var picker = new FileSavePicker(App.Window.AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(ProfileStore.FileNameFor(p.Name)),
        };
        picker.FileTypeChoices.Add("Perfil YAML", [".yaml"]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;
        Info.ActionButton = null;
        try
        {
            if (p.SourceFile is not null && File.Exists(p.SourceFile)) File.Copy(p.SourceFile, result.Path, overwrite: true);
            else ProfileStore.Save(p, result.Path);
            Show(InfoBarSeverity.Success, "Perfil exportado", result.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show(InfoBarSeverity.Error, "Falha ao exportar", ex.Message);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Elevation.OpenFolder(AppSettings.UserProfilesDirectory);
}
