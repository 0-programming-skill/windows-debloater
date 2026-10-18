using Debloater.App.Helpers;
using Debloater.App.ViewModels;
using Debloater.Core.Admx;
using Debloater.Core.Planning;
using Debloater.Core.Policies;
using Debloater.Core.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Debloater.App.Views;

/// <summary>
/// Navegador de todas as políticas ADMX com estado atual e editor (como o gpedit): estado desejado,
/// valores dos elementos, prévia exata do registro, avisos de edição/build e "adicionar às pendentes".
/// </summary>
public sealed partial class PoliciesPage : Page
{
    private readonly AppState _app = App.State;
    private PolicyCatalog? _catalog;
    private string? _category;
    private string _search = "";
    private bool _onlyConfigured;
    private bool _onlyPending;

    private PolicyItem? _current;
    private PolicyEditor? _editor;
    private bool _loadingEditor;

    private sealed class TreeLabel(CategoryNode node)
    {
        public CategoryNode Node { get; } = node;
        public override string ToString() => $"{Node.Name.Replace('∕', '/')} ({Node.Count})";
    }

    public PoliciesPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _app.DesiredChanged += App_DesiredChanged;
        await LoadAsync(false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _app.DesiredChanged -= App_DesiredChanged;

    private void App_DesiredChanged(object? sender, EventArgs e)
    {
        // Pendências mudaram (aqui, no Aplicar ou por um perfil): atualiza os textos sem perder a seleção.
        if (_current is not null) UpdatePendingInfo();
    }

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
        SubtitleText.Text = $"{_catalog.Items.Count} políticas de {_catalog.FileCount} arquivo(s) em {_catalog.Directory}, com o estado atual neste PC. "
            + "Escolha o estado e os valores e adicione às mudanças pendentes. A gravação é direta no registro (como WinUtil/O&O): "
            + "o gpedit.msc não mostra essas mudanças, e uma GPO de domínio pode sobrescrevê-las."
            + (_catalog.CspLoaded ? "" : " (Referência da Policy CSP não encontrada: sem links da documentação nem avisos de edição.)");

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

    private string PendingTextFor(PolicyItem item) => _app.GetPolicyRequest(item.QualifiedName) switch
    {
        null => "",
        { Setting: null } => "Pendente: reverter ao original",
        { Setting: { } s } => $"Pendente: {PolicySetting.StateText(s.State)}" + (s.Scope is { } sc && item.Policy.Class == AdmxClass.Both ? $" ({PolicySetting.ScopeText(sc)})" : ""),
    };

    private void ApplyFilter()
    {
        if (_catalog is null) return;
        IEnumerable<PolicyItem> items = _catalog.Items;
        if (_category is not null) items = items.Where(i => ProfileStore.CategoryMatches(PolicyCatalog.PathOf(i.Policy), _category));
        if (_onlyConfigured) items = items.Where(i => i.IsConfigured);
        if (_onlyPending) items = items.Where(i => _app.GetPolicyRequest(i.QualifiedName) is not null);
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            items = items.Where(i => words.All(w => TweakQuery.Has(i.SearchText, w)));
        }
        var list = items.ToList();
        foreach (var i in list) i.PendingText = PendingTextFor(i);
        var selected = _current;
        List.ItemsSource = list;
        if (selected is not null && list.Contains(selected))
        {
            List.SelectedItem = selected;
            List.ScrollIntoView(selected);
        }
        int configured = _catalog.Items.Count(i => i.IsConfigured);
        CountText.Text = $"{list.Count} exibida(s) · {configured} configurada(s) neste PC · {_app.PolicyRequests.Count} pendente(s)";
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
        _onlyPending = PendingToggle.IsChecked == true;
        ApplyFilter();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _current = null;
        await LoadAsync(true);
    }

    // ---------------- detalhes e editor ----------------

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not PolicyItem p)
        {
            if (List.ItemsSource is null) Details.Visibility = Visibility.Collapsed;
            return;
        }
        if (ReferenceEquals(p, _current) && Details.Visibility == Visibility.Visible) return;
        _current = p;
        Details.Visibility = Visibility.Visible;
        ShowStatic(p);
        LoadEditor(p);
    }

    private void ShowStatic(PolicyItem p)
    {
        DName.Text = p.DisplayName;
        DState.Text = p.StateText;
        DStateBadge.Background = p.StateBrush;
        DState.Foreground = p.StateForeground;
        var riskColor = Labels.RiskColor(p.Risk);
        DRisk.Text = Labels.Risk(p.Risk);
        DRiskBadge.Background = Labels.Brush(riskColor);
        DRisk.Foreground = Labels.ForegroundOn(riskColor);
        DScopes.Text = p.ScopesText;
        DRegistry.Text = p.RegistryText;
        DMeta.Text = $"Nome: {p.QualifiedName}\nClasse: {p.ClassText}\nCategoria: {p.CategoryText}\nCom suporte em: {p.SupportedOnText}";
        DCsp.Visibility = p.CspVisibility;
        DCsp.Content = "Documentação: " + p.CspText;
        DCsp.NavigateUri = p.CspUri;
        DExplain.Text = p.ExplainText;
    }

    private void LoadEditor(PolicyItem p)
    {
        _loadingEditor = true;
        try
        {
            var policy = p.Policy;
            var pending = _app.GetPolicyRequest(p.QualifiedName)?.Setting;
            PolicySetting initial;
            if (pending is not null) initial = pending.Clone();
            else
            {
                var (state, current) = _app.PolicyEngine.ReadCurrent(policy, PolicySetting.DefaultScope(policy.Class));
                initial = current;
                // Classe Both: se só o HKCU estiver configurado, o editor começa por ele. ("Valores próprios" viram Habilitada.)
                if (state == AdmxPolicyState.NotConfigured && policy.Class == AdmxClass.Both)
                {
                    var (userState, userCurrent) = _app.PolicyEngine.ReadCurrent(policy, PolicyScope.User);
                    if (userState != AdmxPolicyState.NotConfigured) initial = userCurrent;
                }
            }

            StateRadios.SelectedIndex = initial.State switch
            {
                PolicyTargetState.Enabled => 1,
                PolicyTargetState.Disabled => 2,
                _ => 0,
            };
            ScopeBox.Visibility = policy.Class == AdmxClass.Both ? Visibility.Visible : Visibility.Collapsed;
            ScopeBox.SelectedIndex = initial.EffectiveScope(policy.Class) == PolicyScope.User ? 1 : 0;

            _editor = new PolicyEditor(policy, initial);
            _editor.Changed += UpdatePreview;
            ElementsHost.Content = _editor.HasElements ? _editor.View : null;
        }
        finally
        {
            _loadingEditor = false;
        }
        UpdatePreview();
    }

    private PolicySetting? CurrentSetting()
    {
        if (_current is null || _editor is null) return null;
        var policy = _current.Policy;
        var state = StateRadios.SelectedIndex switch
        {
            1 => PolicyTargetState.Enabled,
            2 => PolicyTargetState.Disabled,
            _ => PolicyTargetState.NotConfigured,
        };
        var setting = new PolicySetting
        {
            Policy = policy.QualifiedName,
            State = state,
            Scope = policy.Class == AdmxClass.Both ? (ScopeBox.SelectedIndex == 1 ? PolicyScope.User : PolicyScope.Machine) : null,
        };
        if (state == PolicyTargetState.Enabled)
            foreach (var (id, values) in _editor.Values()) setting.Values[id] = values;
        return setting;
    }

    private void State_Changed(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void Scope_Changed(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_loadingEditor || _current is null || _editor is null) return;
        var setting = CurrentSetting()!;
        _editor.IsEnabled = setting.State == PolicyTargetState.Enabled;

        PolicyWritePlan plan;
        try
        {
            plan = _app.PolicyEngine.Plan(_current.Policy, setting);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            PreviewText.Text = "Não foi possível ler o registro: " + ex.Message;
            AddPendingButton.IsEnabled = false;
            return;
        }

        if (plan.IsValid)
        {
            var lines = plan.Describe().ToList();
            PreviewText.Text = lines.Count == 0 ? "(nada a mudar: o registro já está assim)" : string.Join(Environment.NewLine, lines);
        }
        else PreviewText.Text = "—";
        ErrorsText.Visibility = plan.IsValid ? Visibility.Collapsed : Visibility.Visible;
        ErrorsText.Text = string.Join(Environment.NewLine, plan.Errors.Select(e => "• " + e));

        WarningsHost.Children.Clear();
        foreach (var n in plan.Notes) WarningsHost.Children.Add(Note(n, warning: false));
        foreach (var w in _app.PolicyWarnings(_current.Policy, setting.State, setting.EffectiveScope(_current.Policy.Class)))
            WarningsHost.Children.Add(Note(w.Text, w.Level == PolicyWarningLevel.Warning));

        var pending = _app.GetPolicyRequest(_current.QualifiedName);
        bool samePending = pending?.Setting is { } ps && ps.SameAs(setting, _current.Policy.Class);
        AddPendingButton.IsEnabled = plan.IsValid && plan.HasEffect && !samePending;
        AddPendingButton.Content = pending is null ? "Adicionar às mudanças pendentes" : "Atualizar mudança pendente";
        UpdatePendingInfo();
    }

    private static UIElement Note(string text, bool warning)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon
        {
            Glyph = warning ? "" : "",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[warning ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"],
        };
        grid.Children.Add(icon);
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Caption"] };
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);
        return grid;
    }

    private void UpdatePendingInfo()
    {
        if (_current is null) return;
        var pending = _app.GetPolicyRequest(_current.QualifiedName);
        bool hasBackup = _app.PolicyEngine.Backups.Exists(_current.QualifiedName);
        RemovePendingButton.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
        RevertButton.Visibility = hasBackup ? Visibility.Visible : Visibility.Collapsed;
        RevertButton.IsEnabled = pending is not { Setting: null };
        PendingInfo.Text = pending switch
        {
            null when hasBackup => "Esta política foi alterada por este programa: há backup do valor original.",
            null => "",
            { Setting: null } => "Pendente: reverter ao valor original (backup).",
            _ => "Mudança pendente: " + PolicySetting.StateText(pending.Setting!.State) + ".",
        };
        GoApplyLink.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
        _current.PendingText = PendingTextFor(_current);
    }

    private void RefreshListRow()
    {
        // As linhas usam x:Bind de uma vez; reatribui a lista para mostrar o texto de pendência atualizado.
        ApplyFilter();
    }

    private void AddPending_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || CurrentSetting() is not { } setting) return;
        _app.SetPolicyRequest(_current.QualifiedName, new PolicyRequest(_current.Policy, setting));
        UpdatePreview();
        RefreshListRow();
    }

    private void RemovePending_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        _app.SetPolicyRequest(_current.QualifiedName, null);
        LoadEditor(_current);
        RefreshListRow();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        _app.SetPolicyRequest(_current.QualifiedName, new PolicyRequest(_current.Policy, null));
        UpdatePreview();
        RefreshListRow();
    }

    private void GoApply_Click(object sender, RoutedEventArgs e) => App.Window.Navigate("apply");

    /// <summary>--screenshot: mostra o editor de uma política com elementos (Coleta de Dados), sem gravar nada.</summary>
    internal async Task SelectForScreenshotAsync()
    {
        if (_catalog is null) _catalog = await PolicyCatalog.GetAsync();
        SearchBox.Text = "AllowTelemetry";
        _search = "AllowTelemetry";
        ApplyFilter();
        if (List.ItemsSource is List<PolicyItem> { Count: > 0 } list)
            List.SelectedItem = list.FirstOrDefault(i => i.QualifiedName.Equals("DataCollection.admx:AllowTelemetry", StringComparison.OrdinalIgnoreCase)) ?? list[0];
    }
}
