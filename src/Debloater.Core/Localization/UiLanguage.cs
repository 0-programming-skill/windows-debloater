using System.Globalization;

namespace Debloater.Core.Localization;

/// <summary>
/// User interface language. English (en-US) is the base language; pt-BR is the only translation.
/// The language follows <see cref="CultureInfo.CurrentUICulture"/> (the Windows display language by
/// default); <see cref="Apply"/> overrides it for the process (CLI --lang, app setting).
/// </summary>
public static class UiLanguage
{
    public const string English = "en-US";
    public const string PortugueseBrazil = "pt-BR";

    /// <summary>Base language first.</summary>
    public static IReadOnlyList<string> Supported { get; } = [English, PortugueseBrazil];

    /// <summary>Supported language for a culture: exact match, then same neutral language (pt-PT → pt-BR), else English.</summary>
    public static string Resolve(CultureInfo culture)
    {
        foreach (var lang in Supported)
        {
            if (culture.Name.Equals(lang, StringComparison.OrdinalIgnoreCase)) return lang;
        }
        foreach (var lang in Supported)
        {
            if (CultureInfo.GetCultureInfo(lang).TwoLetterISOLanguageName.Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                return lang;
        }
        return English;
    }

    /// <summary>Supported language for the current UI culture.</summary>
    public static string Current => Resolve(CultureInfo.CurrentUICulture);

    /// <summary>True when the current UI language is the base language (no translation lookup needed).</summary>
    public static bool IsBase => Current == English;

    /// <summary>
    /// Normalizes a user-supplied language ("pt", "pt-br", "en", "auto"...). Null, empty or "auto" returns null
    /// (follow Windows). Throws <see cref="ArgumentException"/> for an unsupported language.
    /// </summary>
    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(language.Trim());
        }
        catch (CultureNotFoundException)
        {
            throw new ArgumentException($"Unsupported language '{language}' (expected: {string.Join(", ", Supported)} or auto).", nameof(language));
        }
        var resolved = Resolve(culture);
        if (!CultureInfo.GetCultureInfo(resolved).TwoLetterISOLanguageName.Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported language '{language}' (expected: {string.Join(", ", Supported)} or auto).", nameof(language));
        return resolved;
    }

    /// <summary>
    /// Sets the UI culture of the process (current thread and new threads). Null keeps the Windows display
    /// language. Only the UI culture changes; number and date formatting keep following the user's settings.
    /// </summary>
    public static void Apply(string? language)
    {
        var normalized = Normalize(language);
        if (normalized is null) return;
        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>ADML languages to try for the current UI language (the UI language first, then en-US).</summary>
    public static IReadOnlyList<string> AdmlLanguages =>
        Current == English ? [English] : [Current, English];

    /// <summary>
    /// Picks the text for the current UI language from a base (English) text and per-language overrides.
    /// Falls back to the base text when there is no non-empty override.
    /// </summary>
    public static string? Pick<T>(string? baseText, IReadOnlyDictionary<string, T>? translations, Func<T, string?> select)
    {
        if (translations is null || translations.Count == 0) return baseText;
        var lang = Current;
        if (lang == English) return baseText;
        foreach (var (key, value) in translations)
        {
            if (key.Equals(lang, StringComparison.OrdinalIgnoreCase) && value is not null)
            {
                var text = select(value);
                return string.IsNullOrWhiteSpace(text) ? baseText : text;
            }
        }
        return baseText;
    }
}
