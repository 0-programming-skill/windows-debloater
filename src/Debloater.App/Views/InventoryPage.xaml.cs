using Debloater.App.ViewModels;
using Debloater.Core.Inventory;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Debloater.App.Views;

/// <summary>Inventário por tipo, com busca e filtro "não catalogado".</summary>
public sealed partial class InventoryPage : Page
{
    private static string _kind = InventoryKinds.Services;
    private InventorySnapshot? _snapshot;
    private string _search = "";
    private bool _onlyUncatalogued;

    public InventoryPage()
    {
        InitializeComponent();
        foreach (var kind in InventoryKinds.All)
        {
            var item = new SelectorBarItem { Text = InventoryData.KindLabel(kind), Tag = kind };
            Kinds.Items.Add(item);
            if (kind == _kind) Kinds.SelectedItem = item;
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        App.State.CatalogReloaded += Catalog_Reloaded;
        await LoadAsync(false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.State.CatalogReloaded -= Catalog_Reloaded;

    // O catálogo pode chegar depois do inventário: refaz o cruzamento (sem coletar de novo).
    private async void Catalog_Reloaded(object? sender, EventArgs e) => await LoadAsync(false);

    private async Task LoadAsync(bool refresh)
    {
        LoadingPanel.Visibility = Visibility.Visible;
        List.ItemsSource = null;
        try
        {
            _snapshot = await InventoryData.GetAsync(App.State.Tweaks.Select(t => t.Tweak).ToList(), refresh);
        }
        catch (Exception ex)
        {
            CountText.Text = "Falha ao coletar o inventário: " + ex.Message;
            LoadingPanel.Visibility = Visibility.Collapsed;
            return;
        }
        LoadingPanel.Visibility = Visibility.Collapsed;
        foreach (var item in Kinds.Items)
        {
            var section = _snapshot.Sections.FirstOrDefault(s => s.Kind == (string)item.Tag);
            item.Text = $"{InventoryData.KindLabel((string)item.Tag)} ({section?.Items.Count ?? 0})";
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_snapshot is null) return;
        var section = _snapshot.Sections.FirstOrDefault(s => s.Kind == _kind);
        if (section is null)
        {
            List.ItemsSource = null;
            CountText.Text = "";
            return;
        }
        var rows = section.Items.Select(i => new InventoryRow(i))
            .Where(r => !_onlyUncatalogued || !r.IsCatalogued)
            .Where(r => r.Matches(_search))
            .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        List.ItemsSource = rows;
        int catalogued = section.Items.Count(i => i.IsCatalogued);
        var text = $"{rows.Count} exibido(s) · {section.Items.Count} no total · {catalogued} catalogado(s)";
        if (!section.Available) text += " · indisponível";
        if (!string.IsNullOrWhiteSpace(section.Note)) text += " · " + section.Note;
        CountText.Text = text;
    }

    private void Kinds_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string kind)
        {
            _kind = kind;
            ApplyFilter();
        }
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _search = sender.Text;
        ApplyFilter();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _onlyUncatalogued = UncataloguedToggle.IsChecked == true;
        ApplyFilter();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(true);
}
