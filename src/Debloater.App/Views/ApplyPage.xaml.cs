using Debloater.App.Helpers;
using Debloater.App.Services;
using Debloater.App.ViewModels;
using Debloater.Core.Engine;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Platform;
using Debloater.Core.Policies;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Debloater.App.Views;

/// <summary>Review of the pending changes, confirmations by risk and execution with a log.</summary>
public sealed partial class ApplyPage : Page
{
    /// <summary>Word the user must type to apply critical-risk tweaks (CONFIRM / CONFIRMO).</summary>
    private static string CriticalWord => Loc.Get("ApplyPage_CriticalWord");

    private readonly AppState _app = App.State;
    private IReadOnlyList<PlannedChange> _plan = [];
    private IReadOnlyList<PlannedPolicyChange> _policyPlan = [];
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
    private IEnumerable<PlannedPolicyChange> ExecutablePolicies => _policyPlan.Where(c => !c.IsBlocked);
    private int ExecutableCount => Executable.Count() + ExecutablePolicies.Count();
    private bool HasHigh => Executable.Any(c => c.Tweak.Risk == RiskLevel.High) || ExecutablePolicies.Any(c => c.Risk == RiskLevel.High);

    private void Rebuild()
    {
        _plan = _app.PlanChanges();
        _policyPlan = _app.PlanPolicyChanges();
        int apply = _plan.Count(c => c.Kind == ChangeKind.Apply);
        int revert = _plan.Count(c => c.Kind == ChangeKind.Revert && !c.IsBlocked);
        int blocked = _plan.Count(c => c.IsBlocked) + _policyPlan.Count(c => c.IsBlocked);
        int policies = ExecutablePolicies.Count();
        bool empty = _plan.Count == 0 && _policyPlan.Count == 0;

        SummaryText.Text = _app.IsScanning && _app.LastScan is null
            ? Loc.Get("ApplyPage_WaitingScan")
            : Loc.Format("ApplyPage_Summary", apply, revert)
              + (policies > 0 ? Loc.Format("ApplyPage_SummaryPolicies", policies) : "")
              + (blocked > 0 ? Loc.Format("ApplyPage_SummaryBlocked", blocked) : ".");
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        Groups.Children.Clear();
        foreach (var group in _plan.GroupBy(c => c.Tweak.Risk).OrderByDescending(g => g.Key))
            Groups.Children.Add(BuildGroup(group.Key, group.ToList()));
        if (_policyPlan.Count > 0) Groups.Children.Add(BuildPolicyGroup(_policyPlan));

        bool hasHigh = HasHigh;
        bool hasCritical = Executable.Any(c => c.Tweak.Risk == RiskLevel.Critical);
        HighConfirm.Visibility = hasHigh ? Visibility.Visible : Visibility.Collapsed;
        if (!hasHigh) HighConfirm.IsChecked = false;
        CriticalNote.Visibility = hasCritical ? Visibility.Visible : Visibility.Collapsed;
        ElevationBar.IsOpen = !_app.IsElevated;
        ApplyText.Text = Loc.Format("ApplyPage_ApplyCount", ExecutableCount);
        UpdateApplyEnabled();
    }

    private void UpdateApplyEnabled()
    {
        bool highOk = !HasHigh || HighConfirm.IsChecked == true;
        ApplyButton.IsEnabled = _app.IsElevated && !_running && !_app.IsScanning && ExecutableCount > 0 && highOk;
    }

    /// <summary>"Policies (ADMX)" group: each change with risk, warnings and the exact registry preview.</summary>
    private static UIElement BuildPolicyGroup(IReadOnlyList<PlannedPolicyChange> changes)
    {
        var res = Application.Current.Resources;
        var panel = new StackPanel { Spacing = 10 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock { Text = Loc.Get("ApplyPage_PoliciesGroup"), Style = (Style)res["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = Loc.Format("ApplyPage_PoliciesGroupText", changes.Count), Style = (Style)res["Caption"], VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);

        foreach (var c in changes)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new FontIcon
            {
                Glyph = c.IsBlocked ? "" : c.IsRevert ? "" : "",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
            });
            var text = new StackPanel { Spacing = 2 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var color = Labels.RiskColor(c.Risk);
            title.Children.Add(new Border
            {
                Style = (Style)res["BadgeBorder"],
                Background = Labels.Brush(color),
                Child = new TextBlock { Text = Labels.RiskShort(c.Risk), Style = (Style)res["BadgeText"], Foreground = Labels.ForegroundOn(color) },
            });
            title.Children.Add(new TextBlock { Text = c.Title, Style = (Style)res["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap, Opacity = c.IsBlocked ? 0.6 : 1 });
            text.Children.Add(title);
            text.Children.Add(new TextBlock { Text = $"{c.Policy.QualifiedName} · {string.Join(" › ", c.Policy.CategoryPath)}", Style = (Style)res["Caption"] });
            if (c.BlockedReason is not null)
                text.Children.Add(new TextBlock { Text = Loc.Format("ApplyPage_Blocked", c.BlockedReason), Style = (Style)res["Caption"], Foreground = (Microsoft.UI.Xaml.Media.Brush)res["SystemFillColorCautionBrush"] });
            foreach (var w in c.Warnings.Where(w => w.Level == PolicyWarningLevel.Warning))
                text.Children.Add(new TextBlock { Text = Loc.Format("ApplyPage_Warning", w.Text), Style = (Style)res["Caption"], Foreground = (Microsoft.UI.Xaml.Media.Brush)res["SystemFillColorCautionBrush"] });
            if (c.Preview.Count > 0)
                text.Children.Add(new TextBlock
                {
                    Text = string.Join(Environment.NewLine, c.Preview),
                    FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)res["MonoFont"],
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return new Border { Style = (Style)res["Card"], Child = panel };
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
        header.Children.Add(new TextBlock { Text = Loc.Format("ApplyPage_ChangeCount", changes.Count), Style = (Style)Application.Current.Resources["Caption"], VerticalAlignment = VerticalAlignment.Center });
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
            var kind = c.Kind == ChangeKind.Apply ? Loc.Get("ApplyPage_KindApply") : Loc.Get("ApplyPage_KindRevert");
            text.Children.Add(new TextBlock
            {
                Text = $"{kind}: {c.Tweak.DisplayName}",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
                Opacity = c.IsBlocked ? 0.6 : 1,
            });
            text.Children.Add(new TextBlock
            {
                Text = Loc.Format("ApplyPage_CurrentState", Labels.State(c.CurrentState)) + $" · {App.State.DisplayCategory(c.Tweak.Category)} · {c.Tweak.Id}",
                Style = (Style)Application.Current.Resources["Caption"],
            });
            if (c.BlockedReason is not null)
                text.Children.Add(new TextBlock { Text = Loc.Format("ApplyPage_Blocked", c.BlockedReason), Style = (Style)Application.Current.Resources["Caption"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"] });
            if (c.Kind == ChangeKind.Apply && !string.IsNullOrWhiteSpace(c.Tweak.Breaks))
                text.Children.Add(new TextBlock { Text = Loc.Format("ApplyPage_MayBreak", c.Tweak.DisplayBreaks!.Trim()), Style = (Style)Application.Current.Resources["Caption"] });
            if (c.Kind == ChangeKind.Apply && !c.Tweak.Reversible)
                text.Children.Add(new TextBlock { Text = Loc.Get("ApplyPage_NotReversible"), Style = (Style)Application.Current.Resources["Caption"] });
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

    // ---------------- execution ----------------

    private async Task<bool> ConfirmCriticalAsync(List<PlannedChange> critical)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("ApplyPage_CriticalIntro"),
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var c in critical)
        {
            body.Children.Add(new TextBlock { Text = $"{(c.Kind == ChangeKind.Apply ? Loc.Get("ApplyPage_KindApply") : Loc.Get("ApplyPage_KindRevert"))}: {c.Tweak.DisplayName}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock
            {
                Text = Loc.Format("ApplyPage_MayBreak", string.IsNullOrWhiteSpace(c.Tweak.Breaks) ? Loc.Get("ApplyPage_BreaksNotSpecified") : c.Tweak.DisplayBreaks!.Trim()),
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["Caption"],
            });
        }
        var word = CriticalWord;
        var box = new TextBox { PlaceholderText = word, Header = Loc.Format("ApplyPage_TypeToContinue", word) };
        body.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("ApplyPage_CriticalTitle"),
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            PrimaryButtonText = Loc.Get("ApplyPage_ApplyAnyway"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            RequestedTheme = App.Window.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Default,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim() == word;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var changes = Executable.ToList();
        var policyChanges = ExecutablePolicies.ToList();
        int total = changes.Count + policyChanges.Count;
        if (total == 0 || !_app.IsElevated) return;

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
        RunProgress.Maximum = total;
        RunProgress.Value = 0;

        using var log = new OperationLog();
        void Log(string line) => DispatcherQueue.TryEnqueue(() => LogBox.Text += line + Environment.NewLine);
        log.LineWritten += Log;
        log.Write(Loc.Format("ApplyLog_SessionStarted", changes.Count, policyChanges.Count, _app.System));
        if (log.Error is not null) LogBox.Text += Loc.Format("ApplyLog_FileError", log.Error) + Environment.NewLine;

        try
        {
            if (RestorePointToggle.IsOn)
            {
                RunText.Text = Loc.Get("ApplyLog_CreatingRestorePoint");
                log.Write(Loc.Get("ApplyLog_CreatingRestorePoint"));
                var rp = await RestorePoint.CreateAsync(Loc.Get("ApplyPage_RestorePointName"));
                log.Write(rp.Message);
                AddResult(rp.Created ? "" : "", Loc.Get("ApplyPage_RestorePoint"), rp.Message);
            }

            var engine = _app.Engine;
            int done = 0, ok = 0, failed = 0;
            foreach (var change in changes)
            {
                if (_cts.IsCancellationRequested)
                {
                    log.Write(Loc.Get("ApplyLog_Interrupted"));
                    break;
                }
                bool applying = change.Kind == ChangeKind.Apply;
                RunText.Text = Loc.Format(applying ? "ApplyPage_RunApplying" : "ApplyPage_RunReverting", done + 1, total, change.Tweak.DisplayName);
                log.Write(Loc.Format(applying ? "ApplyLog_Applying" : "ApplyLog_Reverting", change.Tweak.Id, change.Tweak.Risk));

                OperationResult result;
                try
                {
                    var tweak = change.Tweak;
                    // The engine runs in the background: some handlers start processes (DISM, PowerShell).
                    result = change.Kind == ChangeKind.Apply
                        ? await Task.Run(() => engine.ApplyAsync(tweak, RiskLevel.Critical))
                        : await Task.Run(() => engine.RevertAsync(tweak));
                }
                catch (Exception ex)
                {
                    result = new OperationResult(change.Tweak.Id, false, ex.Message, [ex.Message]);
                }

                foreach (var err in result.Errors) log.Write("  " + Loc.Format("ApplyLog_Error", err));
                foreach (var note in result.Notes) log.Write("  " + Loc.Format("ApplyLog_Note", note));
                log.Write($"  {(result.Success ? Loc.Get("ApplyLog_Ok") : Loc.Get("ApplyLog_Failed"))}: {result.Message}");
                if (result.StatusAfter is { } after && _app.Find(change.Tweak.Id) is { } vm) vm.Status = after;
                if (result.Success) ok++; else failed++;
                AddResult(result.Success ? "" : "", change.Tweak.DisplayName, result.Message + (result.Errors.Count > 0 ? " " + string.Join(" ", result.Errors) : "")
                    + (result.Notes.Count > 0 ? " " + Loc.Format("ApplyPage_ResultNote", string.Join(" ", result.Notes)) : ""));
                RunProgress.Value = ++done;
            }

            // ADMX policies: written directly to the registry, with a backup of the original (backups\policies).
            var policyEngine = _app.PolicyEngine;
            foreach (var change in policyChanges)
            {
                if (_cts.IsCancellationRequested)
                {
                    log.Write(Loc.Get("ApplyLog_Interrupted"));
                    break;
                }
                var name = change.Policy.QualifiedName;
                RunText.Text = Loc.Format(change.IsRevert ? "ApplyPage_RunRevertingPolicy" : "ApplyPage_RunConfiguringPolicy", done + 1, total, change.Policy.DisplayName);
                log.Write(change.IsRevert
                    ? Loc.Format("ApplyLog_RevertingPolicy", name)
                    : Loc.Format("ApplyLog_ConfiguringPolicy", change.Request.Setting, change.Risk));

                PolicyOperationResult result;
                try
                {
                    var policy = change.Policy;
                    var setting = change.Request.Setting;
                    result = setting is null
                        ? await Task.Run(() => policyEngine.Revert(name, policy, log))
                        : await Task.Run(() => policyEngine.Apply(policy, setting, log));
                }
                catch (Exception ex)
                {
                    result = new PolicyOperationResult(name, false, ex.Message, [ex.Message]);
                }

                foreach (var err in result.Errors) log.Write("  " + Loc.Format("ApplyLog_Error", err));
                log.Write($"  {(result.Success ? Loc.Get("ApplyLog_Ok") : Loc.Get("ApplyLog_Failed"))}: {result.Message}");
                if (result.Success)
                {
                    ok++;
                    _app.SetPolicyRequest(name, null);
                }
                else failed++;
                PolicyCatalog.Loaded?.Find(name)?.Refresh();
                AddResult(result.Success ? "" : "", change.Policy.DisplayName,
                    result.Message + (result.Errors.Count > 0 ? " " + string.Join(" ", result.Errors) : ""));
                RunProgress.Value = ++done;
            }
            log.Write(Loc.Format("ApplyLog_Done", ok, failed, total - done));
            RunText.Text = Loc.Format("ApplyPage_Done", ok, failed, log.FilePath);
        }
        finally
        {
            log.LineWritten -= Log;
            _running = false;
            CancelButton.Visibility = Visibility.Collapsed;
        }

        // Re-read every state (other tweaks may have been affected) and recompute what is pending.
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
