using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Debloater.App.Services;
using Debloater.App.Views;
using Debloater.Core.Planning;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;

namespace Debloater.App;

public sealed partial class MainWindow : Window
{
    private bool _screenshotTaken;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1360 * scale), (int)(880 * scale)));

        var state = App.State;
        ReadOnlyBar.IsOpen = !state.IsElevated;
        ModeText.Text = state.IsElevated ? "Administrador" : "Somente leitura";
        // O item de configurações só existe depois que o template é aplicado.
        Nav.Loaded += (_, _) =>
        {
            if (Nav.SettingsItem is NavigationViewItem settings) settings.Content = "Configurações";
        };

        state.CatalogReloaded += (_, _) => { BuildCategoryItems(); UpdateCatalogBar(); };
        state.DesiredChanged += (_, _) => UpdatePendingBadge();
        state.ScanCompleted += OnScanCompleted;

        Root.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        ApplyTheme(state.Settings.Theme);

        Navigate(App.StartPage);
        Root.Loaded += async (_, _) => await state.LoadCatalogAsync();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    // ---------------- navegação ----------------

    public void Navigate(string tag, object? parameter = null)
    {
        Type page = tag switch
        {
            "overview" => typeof(OverviewPage),
            "tweaks" => typeof(TweaksPage),
            "apply" => typeof(ApplyPage),
            "policies" => typeof(PoliciesPage),
            "inventory" => typeof(InventoryPage),
            "profiles" => typeof(ProfilesPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(OverviewPage),
        };
        if (tag == "tweaks" && parameter is null) parameter = "";
        ContentFrame.Navigate(page, parameter);
        SelectNavItem(tag, parameter as string);
    }

    private void SelectNavItem(string tag, string? category)
    {
        if (tag == "settings")
        {
            Nav.SelectedItem = Nav.SettingsItem;
            return;
        }
        var wanted = tag == "tweaks" && !string.IsNullOrEmpty(category) ? "cat:" + category : tag;
        var item = AllItems(Nav.MenuItems).FirstOrDefault(i => (i.Tag as string) == wanted);
        if (item is not null) Nav.SelectedItem = item;
    }

    private static IEnumerable<NavigationViewItem> AllItems(IList<object> items)
    {
        foreach (var o in items)
        {
            if (o is not NavigationViewItem item) continue;
            yield return item;
            foreach (var child in AllItems(item.MenuItems)) yield return child;
        }
    }

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            Navigate("settings");
            return;
        }
        if (args.InvokedItemContainer?.Tag is not string tag) return;
        if (tag.StartsWith("cat:", StringComparison.Ordinal)) Navigate("tweaks", tag[4..]);
        else Navigate(tag);
    }

    /// <summary>Itens de categoria sob "Ajustes", a partir dos caminhos "A/B" do catálogo.</summary>
    private void BuildCategoryItems()
    {
        TweaksItem.MenuItems.Clear();
        foreach (var node in App.State.Categories) TweaksItem.MenuItems.Add(MakeItem(node));
        TweaksItem.IsExpanded = true;

        static NavigationViewItem MakeItem(CategoryNode node)
        {
            var item = new NavigationViewItem { Content = $"{node.Name} ({node.Count})", Tag = "cat:" + node.Path };
            foreach (var child in node.Children) item.MenuItems.Add(MakeItem(child));
            return item;
        }
    }

    private void UpdatePendingBadge()
    {
        int n = App.State.PendingCount;
        PendingBadge.Value = n;
        PendingBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCatalogBar()
    {
        CatalogBar.Message = App.State.CatalogMessage ?? "";
        CatalogBar.IsOpen = App.State.CatalogMessage is not null;
    }

    public void ShowError(string message)
    {
        ErrorBar.Title = "Erro";
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    // ---------------- elevação ----------------

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (Elevation.TryRelaunchElevated(out var error)) Application.Current.Exit();
        else ShowError(error ?? "Não foi possível reiniciar como administrador.");
    }

    // ---------------- tema ----------------

    public void ApplyTheme(ThemeChoice theme)
    {
        Root.RequestedTheme = theme switch
        {
            ThemeChoice.Light => ElementTheme.Light,
            ThemeChoice.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        UpdateCaptionButtons();
    }

    private void UpdateCaptionButtons()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x20, 0, 0, 0);
    }

    // ---------------- captura de tela (--screenshot) ----------------

    private async void OnScanCompleted(object? sender, EventArgs e)
    {
        if (App.ScreenshotPath is not { } path || _screenshotTaken) return;
        _screenshotTaken = true;
        try
        {
            if (App.StartPage is "overview" or "tweaks")
            {
                Navigate("tweaks", "Privacidade");
                await Task.Delay(1500);
                if (ContentFrame.Content is TweaksPage page) page.ExpandFirstForScreenshot();
                await Task.Delay(1200);
            }
            else
            {
                // Outra página (--page): espera ela terminar de carregar os próprios dados.
                await Task.Delay(8000);
            }
            await SaveScreenshotAsync(path);
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".erro.txt", ex.ToString());
        }
        Application.Current.Exit();
    }

    private async Task SaveScreenshotAsync(string path)
    {
        // O Mica não aparece no RenderTargetBitmap: usa um fundo sólido do tema na captura.
        Root.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        await Task.Delay(300);
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(Root);
        var pixels = await rtb.GetPixelsAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        double dpi = 96 * (Root.XamlRoot?.RasterizationScale ?? 1);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, dpi, dpi, pixels.ToArray());
        await encoder.FlushAsync();
        stream.Seek(0);
        using var file = File.Create(path);
        await stream.AsStreamForRead().CopyToAsync(file);
    }
}
