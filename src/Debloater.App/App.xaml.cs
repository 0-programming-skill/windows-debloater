using System.Diagnostics;
using System.Globalization;
using Debloater.App.Helpers;
using Debloater.App.Services;
using Debloater.App.ViewModels;
using Debloater.Core.Localization;
using Microsoft.UI.Xaml;

namespace Debloater.App;

public partial class App : Application
{
    private MainWindow? _window;
    private readonly AppSettings _settings;

    public App()
    {
        // The language must be set before any resource or XAML is loaded (InitializeComponent loads App.xaml).
        WindowsUiCulture = CultureInfo.CurrentUICulture;
        _settings = AppSettings.Load();
        ParseArguments(Environment.GetCommandLineArgs());
        ApplyLanguage(LanguageOverride ?? _settings.UiLanguage);

        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Global state (catalog, states, selection).</summary>
    public static AppState State { get; private set; } = null!;

    public static MainWindow Window => ((App)Current)._window!;

    /// <summary>
    /// --screenshot [file.png]: renders the start page after the scan, saves the PNG and exits. Without a file,
    /// saves to &lt;repo&gt;/.claude/docs/screenshots/&lt;page&gt;-&lt;language&gt;.png.
    /// </summary>
    public static string? ScreenshotPath { get; private set; }

    /// <summary>--page &lt;overview|tweaks|apply|policies|inventory|profiles|settings&gt;: start page.</summary>
    public static string StartPage { get; private set; } = "overview";

    /// <summary>--lang &lt;en-US|pt-BR|auto&gt;: language for this run only (overrides the setting; not saved).</summary>
    public static string? LanguageOverride { get; private set; }

    /// <summary>Windows display language (UI culture before any override).</summary>
    public static CultureInfo WindowsUiCulture { get; private set; } = CultureInfo.InvariantCulture;

    /// <summary>Language the interface is running in (en-US or pt-BR), fixed at startup.</summary>
    public static string RunningLanguage { get; private set; } = UiLanguage.English;

    /// <summary>Language a setting value would resolve to ("auto" = the Windows display language).</summary>
    public static string ResolveSetting(string? setting)
    {
        try
        {
            return UiLanguage.Normalize(setting) ?? UiLanguage.Resolve(WindowsUiCulture);
        }
        catch (ArgumentException)
        {
            return UiLanguage.Resolve(WindowsUiCulture);
        }
    }

    private static void ParseArguments(string[] argv)
    {
        int lang = Array.FindIndex(argv, a => a.Equals("--lang", StringComparison.OrdinalIgnoreCase));
        if (lang >= 0 && lang + 1 < argv.Length) LanguageOverride = argv[lang + 1];
        int pg = Array.FindIndex(argv, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (pg >= 0 && pg + 1 < argv.Length) StartPage = argv[pg + 1];
        int i = Array.FindIndex(argv, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            ScreenshotPath = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? Path.GetFullPath(argv[i + 1])
                : ""; // resolved after the language is known
        }
    }

    /// <summary>
    /// Sets the UI culture (catalog text, Core) and the MRT Core language override (x:Uid strings, WinUI's own
    /// control strings) to the same resolved language, so the whole interface agrees.
    /// </summary>
    private static void ApplyLanguage(string? setting)
    {
        try
        {
            UiLanguage.Apply(setting);
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Ignoring language '{setting}': {ex.Message}");
        }
        RunningLanguage = UiLanguage.Current;
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = RunningLanguage;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PrimaryLanguageOverride failed: {ex.Message}");
        }
        if (ScreenshotPath == "")
            ScreenshotPath = Path.Combine(RepositoryRoot(), ".claude", "docs", "screenshots", $"{StartPage}-{RunningLanguage}.png");
    }

    /// <summary>First ancestor of the program folder that contains Debloater.slnx (else the current directory).</summary>
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Debloater.slnx"))) return dir.FullName;
        }
        return Environment.CurrentDirectory;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        State = new AppState(_settings);
        _window = new MainWindow();
        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.AppendAllText(AppSettings.ErrorLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        e.Handled = true;
        (Current as App)?._window?.ShowError(Loc.Format("Error_Unexpected", e.Exception.Message));
    }
}
