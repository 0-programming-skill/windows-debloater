using Debloater.App.Services;
using Debloater.App.ViewModels;
using Microsoft.UI.Xaml;

namespace Debloater.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Estado global (catálogo, estados, seleção).</summary>
    public static AppState State { get; private set; } = null!;

    public static MainWindow Window => ((App)Current)._window!;

    /// <summary>--screenshot &lt;arquivo.png&gt;: renderiza a página Ajustes após a varredura, grava o PNG e fecha.</summary>
    public static string? ScreenshotPath { get; private set; }

    /// <summary>--page &lt;overview|tweaks|apply|policies|inventory|profiles|settings&gt;: página inicial.</summary>
    public static string StartPage { get; private set; } = "overview";

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();
        int i = Array.FindIndex(argv, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < argv.Length) ScreenshotPath = Path.GetFullPath(argv[i + 1]);
        int pg = Array.FindIndex(argv, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (pg >= 0 && pg + 1 < argv.Length) StartPage = argv[pg + 1];

        State = new AppState(AppSettings.Load());
        _window = new MainWindow();
        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.AppendAllText(Path.Combine(AppSettings.DataDirectory, "erros.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        e.Handled = true;
        (Current as App)?._window?.ShowError("Erro inesperado: " + e.Exception.Message);
    }
}
