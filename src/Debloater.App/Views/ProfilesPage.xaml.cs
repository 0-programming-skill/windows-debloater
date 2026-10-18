using Debloater.App.Helpers;
using Debloater.App.Services;
using Debloater.App.ViewModels;
using Debloater.Core.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;

namespace Debloater.App.Views;

/// <summary>Built-in and user profiles: load, save, import and export.</summary>
public sealed partial class ProfilesPage : Page
{
    private readonly AppState _app = App.State;

    public ProfilesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _app.CatalogReloaded += Catalog_Reloaded;
        _app.LoadProfiles();
        Rebuild();
        if (_app.ProfileErrors.Count > 0) Show(InfoBarSeverity.Warning, Loc.Get("ProfilesPage_ErrorsTitle"), string.Join(Environment.NewLine, _app.ProfileErrors));
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _app.CatalogReloaded -= Catalog_Reloaded;

    // The catalog may load after this page opens: tweak counts and category names depend on it.
    private void Catalog_Reloaded(object? sender, EventArgs e) => Rebuild();

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
        title.Children.Add(new TextBlock { Text = p.DisplayName, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        title.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["BadgeBorder"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new TextBlock { Text = p.IsBuiltIn ? Loc.Get("ProfilesPage_BuiltIn") : Loc.Get("ProfilesPage_Mine"), Style = (Style)Application.Current.Resources["BadgeText"] },
        });
        info.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(p.DisplayDescription))
            info.Children.Add(new TextBlock { Text = p.DisplayDescription.Trim(), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Caption"] });
        var resolution = ProfileStore.Resolve(p, _app.Tweaks.Select(t => t.Tweak));
        var rules = p.Rules is { } r
            ? Loc.Format("ProfilesPage_RulesRisk", Labels.RiskShort(r.MaxRisk).ToLower(System.Globalization.CultureInfo.CurrentCulture)) +
              (r.Categories.Count > 0
                  ? Loc.Format("ProfilesPage_RulesCategories", string.Join(", ", r.Categories.Select(_app.DisplayCategory)))
                  : Loc.Get("ProfilesPage_RulesAllCategories")) +
              (r.IncludeIrreversible ? "" : Loc.Get("ProfilesPage_RulesNoIrreversible")) + ". "
            : "";
        info.Children.Add(new TextBlock
        {
            Text = rules
                + (p.Policies is { Count: > 0 } pol
                    ? Loc.Format("ProfilesPage_ContentsWithPolicies", resolution.Desired.Count, pol.Count)
                    : Loc.Format("ProfilesPage_Contents", resolution.Desired.Count))
                + $" · {p.SourceFile}",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["Caption"],
        });
        grid.Children.Add(info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var load = new Button { Content = Loc.Get("ProfilesPage_Load"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(load, "LoadProfile_" + p.Name);
        load.Click += async (_, _) =>
        {
            var msg = await _app.ApplyProfileToSelectionAsync(p);
            Show(InfoBarSeverity.Success, Loc.Get("ProfilesPage_SelectionUpdated"), msg);
            if (_app.PendingCount > 0)
            {
                var review = new Button { Content = Loc.Get("TweaksPage_Review") };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(review, "ProfilesReviewButton");
                review.Click += (_, _) => App.Window.Navigate("apply");
                Info.ActionButton = review;
            }
            else Info.ActionButton = null;
        };
        var export = new Button { Content = Loc.Get("ProfilesPage_Export") };
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
            Show(InfoBarSeverity.Warning, Loc.Get("ProfilesPage_NameMissingTitle"), Loc.Get("ProfilesPage_NameMissing"));
            return;
        }
        var selection = _app.EffectiveSelection();
        var policies = await _app.EffectivePolicySettingsAsync();
        if (selection.Count == 0 && policies.Count == 0)
        {
            Show(InfoBarSeverity.Warning, Loc.Get("ProfilesPage_EmptyTitle"), Loc.Get("ProfilesPage_Empty"));
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
            Show(InfoBarSeverity.Error, Loc.Get("ProfilesPage_SaveFailed"), ex.Message);
            return;
        }
        _app.LoadProfiles();
        Rebuild();
        Info.ActionButton = null;
        Show(InfoBarSeverity.Success, Loc.Get("ProfilesPage_Saved"), Loc.Format("ProfilesPage_SavedText", selection.Count, policies.Count, file));
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
            Show(InfoBarSeverity.Success, Loc.Get("ProfilesPage_Imported"), Loc.Format("ProfilesPage_ImportedText", profile.DisplayName, dest));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Show(InfoBarSeverity.Error, Loc.Get("ProfilesPage_ImportFailed"), ex.Message);
        }
    }

    private async Task ExportAsync(Profile p)
    {
        var picker = new FileSavePicker(App.Window.AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(ProfileStore.FileNameFor(p.Name)),
        };
        picker.FileTypeChoices.Add(Loc.Get("ProfilesPage_YamlFileType"), [".yaml"]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;
        Info.ActionButton = null;
        try
        {
            if (p.SourceFile is not null && File.Exists(p.SourceFile)) File.Copy(p.SourceFile, result.Path, overwrite: true);
            else ProfileStore.Save(p, result.Path);
            Show(InfoBarSeverity.Success, Loc.Get("ProfilesPage_Exported"), result.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show(InfoBarSeverity.Error, Loc.Get("ProfilesPage_ExportFailed"), ex.Message);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Elevation.OpenFolder(AppSettings.UserProfilesDirectory);
}
