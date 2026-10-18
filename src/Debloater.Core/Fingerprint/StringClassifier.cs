using System.Buffers;
using System.Text.RegularExpressions;

namespace Debloater.Core.Fingerprint;

/// <summary>Kind of an "interesting" string found in a binary.</summary>
public enum StringKind
{
    None,

    /// <summary>Registry path under ...\Policies\... (Group Policy/MDM).</summary>
    PolicyPath,

    /// <summary>Other registry path (Software\..., SYSTEM\...).</summary>
    RegistryPath,

    /// <summary>CSP path (./Device/Vendor/MSFT/Policy/Config/...).</summary>
    CspPath,

    /// <summary>SubscribedContent-NNNNNEnabled (ContentDeliveryManager: suggestions and ads).</summary>
    SubscribedContent,

    /// <summary>Registry value name that matches a pattern (Disable*, Allow*, *Enabled, Taskbar*...).</summary>
    ValueName,

    /// <summary>WNF state name (WNF_XXX_...).</summary>
    Wnf,

    /// <summary>Feature name (Feature_...).</summary>
    Feature,

    /// <summary>Identifier near a registry path that does not match the patterns (NearNames).</summary>
    NearName,
}

/// <summary>
/// Decides whether a string extracted from a binary matters for debloating and what kind it is.
/// Used by the scan (what to keep) and by the diff (scoring).
/// </summary>
public static partial class StringClassifier
{
    public const int MinRegistryPathLength = 10;
    public const int MaxStringLength = 400;

    // Start of a registry path (case-insensitive).
    private static readonly string[] RegistryRootTokens =
        ["software\\", "system\\", "policies\\", "currentversion\\", "currentcontrolset\\"];

    private static readonly HashSet<string> SourceOrFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "cc", "cpp", "cxx", "h", "hpp", "inl", "rs", "cs", "js", "ts", "mojom", "idl", "json", "xml", "txt", "log",
        "dll", "exe", "sys", "mui", "inf", "cat", "etl", "pdb", "obj", "lib",
    };

    private static readonly SearchValues<string> RegistryRootSearch =
        SearchValues.Create(RegistryRootTokens, StringComparison.OrdinalIgnoreCase);

    // Value name prefixes (the next character must be uppercase, a digit or '_').
    private static readonly string[] ValuePrefixes =
        ["Disable", "Allow", "Enable", "Show", "Hide", "Taskbar", "TurnOff", "TurnOn", "Prevent", "DoNot", "Block", "Start_"];

    // Words that make any identifier relevant, wherever they appear.
    internal static readonly string[] HotWords =
        ["Copilot", "Recall", "Widget", "Advertis", "Suggest", "Recommend", "Spotlight", "Telemetry", "Bing", "AIData", "WindowsAI"];

    [GeneratedRegex(@"^SubscribedContent-\d+Enabled$")]
    private static partial Regex SubscribedContentRegex();

    [GeneratedRegex(@"^WNF_[A-Z0-9]{2,5}_[A-Z0-9_]{3,}$")]
    private static partial Regex WnfRegex();

    [GeneratedRegex(@"^Feature_[A-Za-z0-9_]{4,}$")]
    private static partial Regex FeatureRegex();

    /// <summary>Classifies an already trimmed string (see <see cref="ExtractRegistryPath"/>).</summary>
    public static StringKind Classify(string s)
    {
        if (s.Length < 5 || s.Length > MaxStringLength) return StringKind.None;
        if (IsCspPath(s)) return StringKind.CspPath;
        if (s.Contains('\\'))
        {
            if (!IsRegistryPath(s)) return StringKind.None;
            return IsPolicyPath(s) ? StringKind.PolicyPath : StringKind.RegistryPath;
        }
        if (s.StartsWith("SubscribedContent-", StringComparison.Ordinal))
            return SubscribedContentRegex().IsMatch(s) ? StringKind.SubscribedContent : StringKind.None;
        if (s.StartsWith("WNF_", StringComparison.Ordinal))
            return WnfRegex().IsMatch(s) ? StringKind.Wnf : StringKind.None;
        if (s.StartsWith("Feature_", StringComparison.Ordinal))
            return FeatureRegex().IsMatch(s) ? StringKind.Feature : StringKind.None;
        return IsValueNameLike(s) ? StringKind.ValueName : StringKind.None;
    }

    public static bool IsPolicyPath(string s) =>
        s.Contains("\\Policies\\", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Policies\\", StringComparison.OrdinalIgnoreCase);

    public static bool IsCspPath(ReadOnlySpan<char> s) =>
        s.Contains("/Vendor/MSFT/", StringComparison.OrdinalIgnoreCase) && s.IndexOfAny(" \t\"<>|\\") < 0;

    /// <summary>Plausible registry path: has a known root and is neither a file path nor prose.</summary>
    public static bool IsRegistryPath(ReadOnlySpan<char> s)
    {
        if (s.Length < MinRegistryPathLength || s.Length > MaxStringLength) return false;
        if (s.IndexOfAny(RegistryRootSearch) < 0) return false;
        if (s.Contains(":\\", StringComparison.Ordinal)) return false;          // C:\..., file path
        if (s.IndexOfAny("<>\"|\t\r\n") >= 0) return false;
        if (s.Contains("\\\\", StringComparison.Ordinal)) return false;          // \\server or double escape
        // Prose ("Software\Microsoft is ..."): at most a few spaces.
        int spaces = s.Count(' ');
        if (spaces > 6) return false;
        // Source-code or file path (chrome\browser\...\policies\x.cc, ...\x.dll).
        int lastSep = s.LastIndexOf('\\');
        int dot = s.LastIndexOf('.');
        if (dot > lastSep && SourceOrFileExtensions.Contains(s[(dot + 1)..].ToString())) return false;
        // Must have at least two non-empty segments.
        int segments = 0;
        foreach (var range in s.Split('\\'))
            if (range.End.Value > range.Start.Value) segments++;
        return segments >= 2;
    }

    /// <summary>
    /// Cuts the registry path out of a character run (stray bytes before the path are common in
    /// ASCII): starts at the leftmost known root, including the segments immediately before it
    /// (HKEY_LOCAL_MACHINE\, \Registry\Machine\). null if there is no path.
    /// </summary>
    public static string? ExtractRegistryPath(ReadOnlySpan<char> run)
    {
        int t = run.IndexOfAny(RegistryRootSearch);
        if (t < 0) return null;
        if (run[..t].Contains(":\\", StringComparison.Ordinal)) return null;   // C:\...\Software\x.dll: file path
        int start = t;
        while (start > 0 && run[start - 1] == '\\')
        {
            int j = start - 1;
            while (j > 0 && (char.IsAsciiLetterOrDigit(run[j - 1]) || run[j - 1] == '_')) j--;
            if (j == start - 1) break;
            start = j;
        }
        var candidate = run[start..].Trim().TrimEnd("\\'\",;:");
        return IsRegistryPath(candidate) ? candidate.ToString() : null;
    }

    /// <summary>Identifier (a letter, then letters/digits/_), 5 to 80 characters.</summary>
    public static bool IsIdentifier(ReadOnlySpan<char> s)
    {
        if (s.Length < 5 || s.Length > 80 || !char.IsAsciiLetter(s[0])) return false;
        foreach (var c in s)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    /// <summary>Value name that matches the debloat patterns.</summary>
    public static bool IsValueNameLike(ReadOnlySpan<char> s)
    {
        if (!IsIdentifier(s)) return false;
        foreach (var p in ValuePrefixes)
        {
            if (s.Length > p.Length && s.StartsWith(p, StringComparison.Ordinal))
            {
                char next = s[p.Length];
                if (char.IsAsciiLetterUpper(next) || char.IsAsciiDigit(next) || next == '_' || p.EndsWith('_')) return true;
            }
        }
        if (s.Length > 9 && char.IsAsciiLetterUpper(s[0]) && s.EndsWith("Enabled", StringComparison.Ordinal)) return true;
        // Hot word anywhere: only without "_" (Account_Copilot_Title, CopilotCard_Footer_X... are
        // string resource keys, not registry values).
        if (char.IsAsciiLetterUpper(s[0]) && HasLower(s) && !s.Contains('_'))
            foreach (var w in HotWords)
                if (s.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Identifier that looks like a value name (PascalCase, 6 to 64 characters), used for the
    /// names near a registry path.
    /// </summary>
    public static bool IsNearNameCandidate(ReadOnlySpan<char> s)
    {
        if (s.Length < 6 || s.Length > 64 || !IsIdentifier(s) || !char.IsAsciiLetterUpper(s[0])) return false;
        if (!HasLower(s)) return false;
        // Long runs of consonants/hex are usually binary garbage.
        int upper = 0;
        foreach (var c in s) if (char.IsAsciiLetterUpper(c)) upper++;
        return upper * 2 <= s.Length + 2;
    }

    private static bool HasLower(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (char.IsAsciiLetterLower(c)) return true;
        return false;
    }

    /// <summary>Priority order when the per-file limit cuts the result (lower = more important).</summary>
    public static int Priority(StringKind kind) => kind switch
    {
        StringKind.PolicyPath => 0,
        StringKind.CspPath => 1,
        StringKind.SubscribedContent => 2,
        StringKind.RegistryPath => 3,
        StringKind.ValueName => 4,
        StringKind.Wnf => 5,
        StringKind.Feature => 6,
        _ => 9,
    };
}
