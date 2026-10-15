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

/// <summary>Página principal: todos os ajustes com estado real, risco, confiança e o estado desejado.</summary>
public sealed partial class TweaksPage : Page
{
    // Filtros persistem entre navegações.
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
        BuildMenu(StateMenu, StateFilterButton, "Estado", Enum.GetValues<TweakState>(), States, Labels.State);
        BuildMenu(RiskMenu, RiskFilterButton, "Risco", Enum.GetValues<RiskLevel>(), Risks, Labels.RiskShort);
        BuildMenu(ConfidenceMenu, ConfidenceFilterButton, "Confiança", Enum.GetValues<Confidence>(), Confidences, c => Labels.Confidence(c));
        SearchBox.Text = _search;
        DiffToggle.IsChecked = _onlyDiffering;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _category = e.Parameter as string is { Length: > 0 } c ? c : null;
        TitleText.Text = _category is null ? "Ajustes" : _category.Replace("/", " › ");
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
        // Com "só o que difere" ligado, a lista depende da seleção; sem ele, não reordena a cada clique.
        if (_onlyDiffering) ApplyFilter();
    }

    private void UpdateScan()
    {
        ScanPanel.Visibility = _app.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.IsIndeterminate = _app.ScanTotal == 0;
        ScanProgress.Maximum = Math.Max(1, _app.ScanTotal);
        ScanProgress.Value = _app.ScanDone;
        ScanText.Text = $"Lendo o estado atual neste PC... {_app.ScanDone} de {_app.ScanTotal}";
    }

    private void UpdateReviewButton()
    {
        int n = _app.PendingCount;
        ReviewText.Text = n == 0 ? "Revisar mudanças" : $"Revisar {n} mudança(s)";
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

        int total = _app.Tweaks.Count(t => _category is null || Core.Profiles.ProfileStore.CategoryMatches(t.Category, _category));
        SubtitleText.Text = items.Count == total
            ? $"{total} ajuste(s){LastScanText()}"
            : $"{items.Count} de {total} ajuste(s) com os filtros atuais{LastScanText()}";
        EmptyText.Visibility = items.Count == 0 && !_app.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        UpdateReviewButton();
    }

    private string LastScanText() => _app.LastScan is { } t ? $" · estado lido às {t:HH:mm:ss}" : "";

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
        var clear = new MenuFlyoutItem { Text = "Limpar filtro" };
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

    /// <summary>Clique na caixa "desejado": vira uma escolha explícita (aplicado ou não aplicado).</summary>
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

    /// <summary>Usado só pelo modo --screenshot: abre o primeiro item para mostrar os detalhes.</summary>
    public void ExpandFirstForScreenshot()
    {
        if (List.TryGetElement(0) is Expander ex) ex.IsExpanded = true;
    }
}
