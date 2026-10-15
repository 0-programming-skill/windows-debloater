using Debloater.App.Helpers;
using Debloater.App.Services;
using Debloater.App.ViewModels;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Debloater.App.Views;

/// <summary>Revisão das mudanças pendentes, confirmações por risco e execução com log.</summary>
public sealed partial class ApplyPage : Page
{
    private const string CriticalWord = "CONFIRMO";

    private readonly AppState _app = App.State;
    private IReadOnlyList<PlannedChange> _plan = [];
    private bool _running;
    private CancellationTokenSource? _cts;

    public ApplyPage()
    {
        InitializeComponent();
        RestorePointToggle.IsOn = _app.Settings.CreateRestorePoint;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _app.DesiredChanged += App_Changed;
        _app.ScanCompleted += App_Changed;
        Rebuild();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _app.DesiredChanged -= App_Changed;
        _app.ScanCompleted -= App_Changed;
    }

    private void App_Changed(object? sender, EventArgs e)
    {
        if (!_running) Rebuild();
    }

    private IEnumerable<PlannedChange> Executable => _plan.Where(c => !c.IsBlocked);

    private void Rebuild()
    {
        _plan = _app.PlanChanges();
        int apply = _plan.Count(c => c.Kind == ChangeKind.Apply);
        int revert = _plan.Count(c => c.Kind == ChangeKind.Revert && !c.IsBlocked);
        int blocked = _plan.Count(c => c.IsBlocked);

        SummaryText.Text = _app.IsScanning && _app.LastScan is null
            ? "Aguardando a leitura do estado atual..."
            : $"{apply} para aplicar, {revert} para reverter" + (blocked > 0 ? $", {blocked} que não podem ser revertidos (veja o motivo)." : ".");
        EmptyPanel.Visibility = _plan.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.Visibility = _plan.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        Groups.Children.Clear();
        foreach (var group in _plan.GroupBy(c => c.Tweak.Risk).OrderByDescending(g => g.Key))
            Groups.Children.Add(BuildGroup(group.Key, group.ToList()));

        bool hasHigh = Executable.Any(c => c.Tweak.Risk == RiskLevel.High);
        bool hasCritical = Executable.Any(c => c.Tweak.Risk == RiskLevel.Critical);
        HighConfirm.Visibility = hasHigh ? Visibility.Visible : Visibility.Collapsed;
        if (!hasHigh) HighConfirm.IsChecked = false;
        CriticalNote.Visibility = hasCritical ? Visibility.Visible : Visibility.Collapsed;
        ElevationBar.IsOpen = !_app.IsElevated;
        ApplyText.Text = $"Aplicar {Executable.Count()} mudança(s)";
        UpdateApplyEnabled();
    }

    private void UpdateApplyEnabled()
    {
        bool highOk = !Executable.Any(c => c.Tweak.Risk == RiskLevel.High) || HighConfirm.IsChecked == true;
        ApplyButton.IsEnabled = _app.IsElevated && !_running && !_app.IsScanning && Executable.Any() && highOk;
    }

    private static UIElement BuildGroup(RiskLevel risk, List<PlannedChange> changes)
    {
        var panel = new StackPanel { Spacing = 6 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var color = Labels.RiskColor(risk);
        header.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["BadgeBorder"],
            Background = Labels.Brush(color),
            Child = new TextBlock { Text = Labels.Risk(risk), Style = (Style)Application.Current.Resources["BadgeText"], Foreground = Labels.ForegroundOn(color) },
        });
        header.Children.Add(new TextBlock { Text = $"{changes.Count} mudança(s)", Style = (Style)Application.Current.Resources["Caption"], VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);

        foreach (var c in changes)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new FontIcon
            {
                Glyph = c.IsBlocked ? "" : c.Kind == ChangeKind.Apply ? "" : "",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
            };
            row.Children.Add(icon);
            var text = new StackPanel();
            var kind = c.Kind == ChangeKind.Apply ? "Aplicar" : "Reverter";
            text.Children.Add(new TextBlock
            {
                Text = $"{kind}: {c.Tweak.Name}",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
                Opacity = c.IsBlocked ? 0.6 : 1,
            });
            text.Children.Add(new TextBlock
            {
                Text = $"Estado atual: {Labels.State(c.CurrentState)} · {c.Tweak.Category} · {c.Tweak.Id}",
                Style = (Style)Application.Current.Resources["Caption"],
            });
            if (c.BlockedReason is not null)
                text.Children.Add(new TextBlock { Text = "Não será executado: " + c.BlockedReason, Style = (Style)Application.Current.Resources["Caption"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"] });
            if (c.Kind == ChangeKind.Apply && !string.IsNullOrWhiteSpace(c.Tweak.Breaks))
                text.Children.Add(new TextBlock { Text = "Pode quebrar: " + c.Tweak.Breaks!.Trim(), Style = (Style)Application.Current.Resources["Caption"] });
            if (c.Kind == ChangeKind.Apply && !c.Tweak.Reversible)
                text.Children.Add(new TextBlock { Text = "Não reversível pelo programa.", Style = (Style)Application.Current.Resources["Caption"] });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return new Border { Style = (Style)Application.Current.Resources["Card"], Child = panel };
    }

    private void Confirm_Changed(object sender, RoutedEventArgs e) => UpdateApplyEnabled();

    private void RestorePoint_Toggled(object sender, RoutedEventArgs e)
    {
        _app.Settings.CreateRestorePoint = RestorePointToggle.IsOn;
        _app.Settings.Save();
    }

    private void GoTweaks_Click(object sender, RoutedEventArgs e) => App.Window.Navigate("tweaks");

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Elevation.OpenFolder(OperationLog.DefaultDirectory);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
    }

    // ---------------- execução ----------------

    private async Task<bool> ConfirmCriticalAsync(List<PlannedChange> critical)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = "Os ajustes abaixo têm risco CRÍTICO e podem deixar funções essenciais do Windows inoperantes:",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var c in critical)
        {
            body.Children.Add(new TextBlock { Text = $"{(c.Kind == ChangeKind.Apply ? "Aplicar" : "Reverter")}: {c.Tweak.Name}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock
            {
                Text = "Pode quebrar: " + (string.IsNullOrWhiteSpace(c.Tweak.Breaks) ? "(não informado no catálogo)" : c.Tweak.Breaks!.Trim()),
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["Caption"],
            });
        }
        var box = new TextBox { PlaceholderText = CriticalWord, Header = $"Para continuar, digite {CriticalWord}:" };
        body.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Confirmar ajustes de risco crítico",
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            PrimaryButtonText = "Aplicar mesmo assim",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            RequestedTheme = App.Window.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Default,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim() == CriticalWord;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var changes = Executable.ToList();
        if (changes.Count == 0 || !_app.IsElevated) return;

        var critical = changes.Where(c => c.Tweak.Risk == RiskLevel.Critical).ToList();
        if (critical.Count > 0 && !await ConfirmCriticalAsync(critical)) return;

        _running = true;
        _cts = new CancellationTokenSource();
        UpdateApplyEnabled();
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        RunPanel.Visibility = Visibility.Visible;
        Results.Children.Clear();
        LogBox.Text = "";
        RunProgress.Maximum = changes.Count;
        RunProgress.Value = 0;

        using var log = new OperationLog();
        void Log(string line) => DispatcherQueue.TryEnqueue(() => LogBox.Text += line + Environment.NewLine);
        log.LineWritten += Log;
        log.Write($"Sessão iniciada: {changes.Count} mudança(s). {_app.System}");
        if (log.Error is not null) LogBox.Text += $"(não foi possível gravar o arquivo de log: {log.Error}){Environment.NewLine}";

        try
        {
            if (RestorePointToggle.IsOn)
            {
                RunText.Text = "Criando ponto de restauração...";
                log.Write("Criando ponto de restauração...");
                var rp = await RestorePoint.CreateAsync("Windows Debloater: antes de aplicar ajustes");
                log.Write(rp.Message);
                AddResult(rp.Created ? "" : "", "Ponto de restauração", rp.Message);
            }

            var engine = _app.Engine;
            int done = 0, ok = 0, failed = 0;
            foreach (var change in changes)
            {
                if (_cts.IsCancellationRequested)
                {
                    log.Write("Interrompido pelo usuário.");
                    break;
                }
                var verb = change.Kind == ChangeKind.Apply ? "Aplicando" : "Revertendo";
                RunText.Text = $"{verb} {done + 1} de {changes.Count}: {change.Tweak.Name}";
                log.Write($"{verb} {change.Tweak.Id} (risco {change.Tweak.Risk})...");

                OperationResult result;
                try
                {
                    var tweak = change.Tweak;
                    // O engine roda em segundo plano: alguns handlers chamam processos (DISM, PowerShell).
                    result = change.Kind == ChangeKind.Apply
                        ? await Task.Run(() => engine.ApplyAsync(tweak, RiskLevel.Critical))
                        : await Task.Run(() => engine.RevertAsync(tweak));
                }
                catch (Exception ex)
                {
                    result = new OperationResult(change.Tweak.Id, false, ex.Message, [ex.Message]);
                }

                foreach (var err in result.Errors) log.Write("  erro: " + err);
                log.Write($"  {(result.Success ? "OK" : "FALHA")}: {result.Message}");
                if (result.StatusAfter is { } after && _app.Find(change.Tweak.Id) is { } vm) vm.Status = after;
                if (result.Success) ok++; else failed++;
                AddResult(result.Success ? "" : "", change.Tweak.Name, result.Message + (result.Errors.Count > 0 ? " " + string.Join(" ", result.Errors) : ""));
                RunProgress.Value = ++done;
            }
            log.Write($"Concluído: {ok} com sucesso, {failed} com falha, {changes.Count - done} não executado(s).");
            RunText.Text = $"Concluído: {ok} com sucesso, {failed} com falha. Log: {log.FilePath}";
        }
        finally
        {
            log.LineWritten -= Log;
            _running = false;
            CancelButton.Visibility = Visibility.Collapsed;
        }

        // Relê todos os estados (outros ajustes podem ter sido afetados) e recalcula o pendente.
        await _app.ScanAsync();
        Rebuild();
    }

    private void AddResult(string glyph, string title, string message)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = title + ": ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = message });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Results.Children.Add(row);
    }
}
