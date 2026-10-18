using System.Diagnostics;
using System.Globalization;
using Debloater.Core.Localization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Debloater.App.Helpers;

/// <summary>
/// Localized strings from Strings/&lt;language&gt;/Resources.resw (MRT Core, works unpackaged). The language is
/// the one resolved at startup (<see cref="UiLanguage.Current"/>), set explicitly on the resource context so
/// code strings always match the XAML (x:Uid) strings and the catalog text.
/// </summary>
public static class Loc
{
    private static ResourceManager? _manager;
    private static ResourceMap? _map;
    private static ResourceContext? _context;

    private static void EnsureLoaded()
    {
        if (_map is not null) return;
        _manager = new ResourceManager();
        _map = _manager.MainResourceMap.GetSubtree("Resources");
        _context = _manager.CreateResourceContext();
        _context.QualifierValues["Language"] = UiLanguage.Current;
    }

    /// <summary>String for <paramref name="key"/>; a missing key returns the key itself (and is logged in debug output).</summary>
    public static string Get(string key)
    {
        try
        {
            EnsureLoaded();
            var candidate = _map!.TryGetValue(key, _context);
            if (candidate is not null) return candidate.ValueAsString;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loc: failed to read resource '{key}': {ex.Message}");
            return key;
        }
        Debug.WriteLine($"Loc: missing resource '{key}'");
        return key;
    }

    /// <summary><see cref="string.Format(IFormatProvider, string, object[])"/> over the string for <paramref name="key"/>.</summary>
    public static string Format(string key, params object?[] args)
    {
        var format = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            Debug.WriteLine($"Loc: invalid format string for '{key}'");
            return format;
        }
    }
}
