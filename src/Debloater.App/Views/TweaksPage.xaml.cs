using System.Collections.ObjectModel;
using System.ComponentModel;
using Debloater.App.Helpers;
using Debloater.App.ViewModels;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Debloater.App.Views;

/// <summary>Main page: every tweak with its real state, risk, confidence and the desired state.</summary>
public sealed partial class TweaksPage : Page
{
    // Filters persist across navigations.
    private static readonly HashSet<TweakState> States = [];
    private static readonly HashSet<RiskLevel> Risks = [];
    private static readonly HashSet<Confidence> Confidences = [];
    private static string _search = "";
    private static bool _onlyDiffering;

    private readonly AppState _app = App.State;
    private readonly ObservableCollection<TweakItemViewModel> _filtered = [];
    private string? _category;

    public TweaksPage()
    {
        InitializeComponent();
        List.ItemsSource = _filtered;
        BuildMenu(StateMenu, StateFilterButton, Loc.Get("TweaksPage_StateFilter"), Enum.GetValues<TweakState>(), States, Labels.State);
        BuildMenu(RiskMenu, RiskFilterButton, Loc.Get("TweaksPage_RiskFilter"), Enum.GetValues<RiskLevel>(), Risks, Labels.RiskShort);
        BuildMenu(ConfidenceMenu, ConfidenceFilterButton, Loc.Get("TweaksPage_ConfidenceFilter"), Enum.GetValues<Confidence>(), Confidences, c => Labels.Confidence(c));
        SearchBox.Text = _search;
        DiffToggle.IsChecked = _onlyDiffering;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _category = e.Parameter as string is { Length: > 0 } c ? c : null;
        TitleText.Text = _category is null ? Loc.Get("TweaksPage_AllTweaks") : _app.DisplayCategory(_category).Replace("/", " › ");
        _app.PropertyChanged += App_PropertyChanged;
        _app.ScanCompleted += App_Refreshed;
        _app.CatalogReloaded += App_Refreshed;
        _app.DesiredChanged += App_DesiredChanged;
        UpdateScan();
        ApplyFilter();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _app.PropertyChanged -= App_PropertyChanged;
        _app.ScanCompleted -= App_Refreshed;
        _app.CatalogReloaded -= App_Refreshed;
        _app.DesiredChanged -= App_DesiredChanged;
    }

    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.IsScanning) or nameof(AppState.ScanDone) or nameof(AppState.ScanTotal)) UpdateScan();
    }

    private void App_Refreshed(object? sender, EventArgs e) => ApplyFilter();

    private void App_DesiredChanged(object? sender, EventArgs e)
    {
        UpdateReviewButton();
        // With "only what differs" on, the list depends on the selection; without it, do not reorder on every click.
        if (_onlyDiffering) ApplyFilter();
    }

    private void UpdateScan()
    {
        ScanPanel.Visibility = _app.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.IsIndeterminate = _app.ScanTotal == 0;
        ScanProgress.Maximum = Math.Max(1, _app.ScanTotal);
        ScanProgress.Value = _app.ScanDone;
        ScanText.Text = Loc.Format("TweaksPage_Scanning", _app.ScanDone, _app.ScanTotal);
    }

    private void UpdateReviewButton()
    {
        int n = _app.PendingCount;
        ReviewText.Text = n == 0 ? Loc.Get("TweaksPage_Review") : Loc.Format("TweaksPage_ReviewCount", n);
        ClearButton.IsEnabled = _app.Desired.Count > 0;
    }

    private void ApplyFilter()
    {
        var query = new TweakQuery
        {
            Search = _search,
            Category = _category,
            States = States,
            Risks = Risks,
            Confidences = Confidences,
            OnlyDiffering = _onlyDiffering,
        };
        var items = _app.Tweaks.Where(t => query.Matches(t.Tweak, t.State, t.ExplicitDesired)).ToList();
        _filtered.Clear();
        foreach (var i in items) _filtered.Add(i);

        int total = _app.Tweaks.Count(t => _category is null || Core.Profiles.ProfileStore.CategoryMatches(t.Tweak.Category, _category));
        SubtitleText.Text = items.Count == total
            ? Loc.Format("TweaksPage_Count", total) + LastScanText()
            : Loc.Format("TweaksPage_FilteredCount", items.Count, total) + LastScanText();
        EmptyText.Visibility = items.Count == 0 && !_app.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        UpdateReviewButton();
    }

    private string LastScanText() => _app.LastScan is { } t ? " · " + Loc.Format("TweaksPage_LastScan", t.ToString("HH:mm:ss")) : "";

    private void BuildMenu<T>(MenuFlyout menu, DropDownButton button, string label, IEnumerable<T> values, HashSet<T> selected, Func<T, string> text)
    {
        void UpdateLabel() => button.Content = selected.Count == 0 ? label : $"{label} ({selected.Count})";
        foreach (var v in values)
        {
            var item = new ToggleMenuFlyoutItem { Text = text(v), IsChecked = selected.Contains(v) };
            item.Click += (_, _) =>
            {
                if (item.IsChecked) selected.Add(v); else selected.Remove(v);
                UpdateLabel();
                ApplyFilter();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = Loc.Get("TweaksPage_ClearFilter") };
        clear.Click += (_, _) =>
        {
            selected.Clear();
            foreach (var i in menu.Items.OfType<ToggleMenuFlyoutItem>()) i.IsChecked = false;
            UpdateLabel();
            ApplyFilter();
        };
        menu.Items.Add(clear);
        UpdateLabel();
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _search = sender.Text;
        ApplyFilter();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _onlyDiffering = DiffToggle.IsChecked == true;
        ApplyFilter();
    }

    /// <summary>Click on the "desired" box: becomes an explicit choice (applied or not applied).</summary>
    private void Desired_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string id } cb && _app.Find(id) is { } vm)
        {
            vm.DesiredChecked = cb.IsChecked == true;
            cb.IsChecked = vm.DesiredChecked;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _app.ScanAsync();

    private void Clear_Click(object sender, RoutedEventArgs e) => _app.ClearDesired();

    private void Review_Click(object sender, RoutedEventArgs e) => App.Window.Navigate("apply");

    /// <summary>Only used by the --screenshot mode: expands the first item to show its details.</summary>
    public void ExpandFirstForScreenshot()
    {
        if (List.TryGetElement(0) is Expander ex) ex.IsExpanded = true;
    }
}
