using System.Globalization;
using System.Resources;

namespace Debloater.Cli;

/// <summary>
/// Localized CLI strings (Strings.resx = English, Strings.pt-BR.resx = Portuguese), looked up in
/// <see cref="CultureInfo.CurrentUICulture"/> (the Windows display language, or the --lang option).
/// </summary>
internal static class L
{
    private static readonly ResourceManager s_resources = new("Debloater.Cli.Strings", typeof(L).Assembly);

    /// <summary>Text of <paramref name="key"/> in the current UI language (the key itself if missing).</summary>
    public static string T(string key) => s_resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Formatted text of <paramref name="key"/>; numbers and dates follow the current culture.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
}
