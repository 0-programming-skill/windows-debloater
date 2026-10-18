using System.ComponentModel;
using Debloater.App.Helpers;
using Debloater.App.ViewModels;
using Debloater.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Debloater.App.Views;

public sealed partial class OverviewPage : Page
{
    private readonly AppState _app = App.State;

    public OverviewPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var s = _app.System;
        ProductText.Text = $"{s.ProductName} {s.DisplayVersion}";
        BuildText.Text = $"Build {s.Build}.{s.Ubr} (UBR {s.Ubr})";
        EditionText.Text = Loc.Format("OverviewPage_Edition", s.Edition);
        ElevationIcon.Glyph = s.IsElevated ? "" : "";
        ElevationTitle.Text = s.IsElevated ? Loc.Get("Mode_Administrator") : Loc.Get("Mode_ReadOnly");
        ElevationText.Text = s.IsElevated
            ? Loc.Get("OverviewPage_ElevatedText")
            : Loc.Get("OverviewPage_ReadOnlyText");

        _app.ScanCompleted += App_Changed;
        _app.CatalogReloaded += App_Changed;
        _app.PropertyChanged += App_PropertyChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _app.ScanCompleted -= App_Changed;
        _app.CatalogReloaded -= App_Changed;
        _app.PropertyChanged -= App_PropertyChanged;
    }

    private void App_Changed(object? sender, EventArgs e) => Refresh();

    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.IsScanning) or nameof(AppState.ScanDone)) UpdateScanText();
    }

    private void UpdateScanText()
    {
        ScanRing.IsActive = _app.IsScanning;
        ScanText.Text = _app.IsScanning
            ? Loc.Format("OverviewPage_Scanning", _app.ScanDone, _app.ScanTotal)
            : _app.LastScan is { } t ? Loc.Format("OverviewPage_LastScan", _app.Tweaks.Count, t.ToString("HH:mm:ss")) : "";
    }

    private void Refresh()
    {
        UpdateScanText();
        StateTiles.Children.Clear();
        foreach (var st in Enum.GetValues<TweakState>())
            StateTiles.Children.Add(Tile(Labels.State(st), _app.IsScanning && _app.LastScan is null ? "…" : _app.CountByState(st).ToString(), Labels.StateColor(st)));
        RiskTiles.Children.Clear();
        foreach (var r in Enum.GetValues<RiskLevel>())
            RiskTiles.Children.Add(Tile(Labels.Risk(r), _app.CountByRisk(r).ToString(), Labels.RiskColor(r)));

        ProfileButtons.Children.Clear();
        foreach (var p in _app.Profiles.Where(p => p.IsBuiltIn))
        {
            var b = new Button { Content = p.DisplayName };
            ToolTipService.SetToolTip(b, p.DisplayDescription?.Trim());
            b.Click += async (_, _) =>
            {
                var msg = await _app.ApplyProfileToSelectionAsync(p);
                if (_app.PendingCount > 0)
                {
                    App.Window.Navigate("apply");
                }
                else
                {
                    ProfileInfo.Message = msg + " " + Loc.Get("OverviewPage_NothingToChange");
                    ProfileInfo.IsOpen = true;
                }
            };
            ProfileButtons.Children.Add(b);
        }
    }

    private static Border Tile(string label, string value, Color color)
    {
        var panel = new StackPanel { Spacing = 4 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Labels.Brush(color), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["Caption"] });
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock { Text = value, Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        return new Border { Style = (Style)Application.Current.Resources["Card"], Child = panel, Margin = new Thickness(0, 0, 12, 12) };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _app.ScanAsync();
}
