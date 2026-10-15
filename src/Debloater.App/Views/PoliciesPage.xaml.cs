using Debloater.App.ViewModels;
using Debloater.Core.Planning;
using Debloater.Core.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Debloater.App.Views;

/// <summary>Navegador de todas as políticas ADMX com estado atual (somente leitura).</summary>
public sealed partial class PoliciesPage : Page
{
    private PolicyCatalog? _catalog;
    private string? _category;
    private string _search = "";
    private bool _onlyConfigured;

    private sealed class TreeLabel(CategoryNode node)
    {
        public CategoryNode Node { get; } = node;
        public override string ToString() => $"{Node.Name.Replace('∕', '/')} ({Node.Count})";
    }

    public PoliciesPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await LoadAsync(false);

    private async Task LoadAsync(bool refresh)
    {
        LoadingPanel.Visibility = Visibility.Visible;
        List.ItemsSource = null;
        try
        {
            _catalog = await PolicyCatalog.GetAsync(refresh);
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            SubtitleText.Text = "Não foi possível ler os modelos administrativos: " + ex.Message;
            return;
        }
        LoadingPanel.Visibility = Visibility.Collapsed;
        SubtitleText.Text = $"{_catalog.Items.Count} políticas de {_catalog.FileCount} arquivo(s) em {_catalog.Directory}, com o estado atual neste PC. Somente leitura nesta versão."
            + (_catalog.CspLoaded ? "" : " (Referência da Policy CSP não encontrada: sem links da documentação.)");

        Tree.RootNodes.Clear();
        var all = new TreeViewNode { Content = "Todas as políticas", IsExpanded = true };
        Tree.RootNodes.Add(all);
        foreach (var node in _catalog.Tree) Tree.RootNodes.Add(MakeNode(node));
        ApplyFilter();
    }

    private static TreeViewNode MakeNode(CategoryNode node)
    {
        var tvn = new TreeViewNode { Content = new TreeLabel(node) };
        foreach (var c in node.Children) tvn.Children.Add(MakeNode(c));
        return tvn;
    }

    private void ApplyFilter()
    {
        if (_catalog is null) return;
        IEnumerable<PolicyItem> items = _catalog.Items;
        if (_category is not null) items = items.Where(i => ProfileStore.CategoryMatches(PolicyCatalog.PathOf(i.Policy), _category));
        if (_onlyConfigured) items = items.Where(i => i.IsConfigured);
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            items = items.Where(i => words.All(w => TweakQuery.Has(i.SearchText, w)));
        }
        var list = items.ToList();
        List.ItemsSource = list;
        int configured = _catalog.Items.Count(i => i.IsConfigured);
        CountText.Text = $"{list.Count} exibida(s) · {configured} configurada(s) neste PC";
    }

    private void Tree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        _category = (args.InvokedItem as TreeViewNode)?.Content is TreeLabel label ? label.Node.Path : null;
        ApplyFilter();
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _search = sender.Text;
        ApplyFilter();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _onlyConfigured = ConfiguredToggle.IsChecked == true;
        ApplyFilter();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(true);

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not PolicyItem p)
        {
            Details.Visibility = Visibility.Collapsed;
            return;
        }
        Details.Visibility = Visibility.Visible;
        DName.Text = p.DisplayName;
        DState.Text = p.StateText;
        DStateBadge.Background = p.StateBrush;
        DState.Foreground = p.StateForeground;
        DScopes.Text = p.ScopesText;
        DRegistry.Text = p.RegistryText;
        DMeta.Text = $"Nome: {p.QualifiedName}\nClasse: {p.ClassText}\nCategoria: {p.CategoryText}\nCom suporte em: {p.SupportedOnText}";
        DCsp.Visibility = p.CspVisibility;
        DCsp.Content = "Documentação: " + p.CspText;
        DCsp.NavigateUri = p.CspUri;
        DExplain.Text = p.ExplainText;
    }
}
