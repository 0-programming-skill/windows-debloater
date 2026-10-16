using System.Buffers;
using System.Text.RegularExpressions;

namespace Debloater.Core.Fingerprint;

/// <summary>Tipo de uma cadeia "interessante" encontrada num binário.</summary>
public enum StringKind
{
    None,

    /// <summary>Caminho de registro sob ...\Policies\... (política de Grupo/MDM).</summary>
    PolicyPath,

    /// <summary>Outro caminho de registro (Software\..., SYSTEM\...).</summary>
    RegistryPath,

    /// <summary>Caminho de CSP (./Device/Vendor/MSFT/Policy/Config/...).</summary>
    CspPath,

    /// <summary>SubscribedContent-NNNNNEnabled (ContentDeliveryManager: sugestões e anúncios).</summary>
    SubscribedContent,

    /// <summary>Nome de valor de registro que casa com um padrão (Disable*, Allow*, *Enabled, Taskbar*...).</summary>
    ValueName,

    /// <summary>Nome de estado WNF (WNF_XXX_...).</summary>
    Wnf,

    /// <summary>Nome de feature (Feature_...).</summary>
    Feature,

    /// <summary>Identificador perto de um caminho de registro, sem casar com os padrões (NearNames).</summary>
    NearName,
}

/// <summary>
/// Decide se uma cadeia extraída de um binário interessa para debloat e de que tipo ela é.
/// Usado na varredura (o que guardar) e no diff (pontuação).
/// </summary>
public static partial class StringClassifier
{
    public const int MinRegistryPathLength = 10;
    public const int MaxStringLength = 400;

    // Início de um caminho de registro (sem diferenciar maiúsculas).
    private static readonly string[] RegistryRootTokens =
        ["software\\", "system\\", "policies\\", "currentversion\\", "currentcontrolset\\"];

    private static readonly HashSet<string> SourceOrFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "cc", "cpp", "cxx", "h", "hpp", "inl", "rs", "cs", "js", "ts", "mojom", "idl", "json", "xml", "txt", "log",
        "dll", "exe", "sys", "mui", "inf", "cat", "etl", "pdb", "obj", "lib",
    };

    private static readonly SearchValues<string> RegistryRootSearch =
        SearchValues.Create(RegistryRootTokens, StringComparison.OrdinalIgnoreCase);

    // Prefixos de nomes de valor (o caractere seguinte precisa ser maiúscula, dígito ou '_').
    private static readonly string[] ValuePrefixes =
        ["Disable", "Allow", "Enable", "Show", "Hide", "Taskbar", "TurnOff", "TurnOn", "Prevent", "DoNot", "Block", "Start_"];

    // Palavras que tornam qualquer identificador relevante, onde quer que apareçam.
    internal static readonly string[] HotWords =
        ["Copilot", "Recall", "Widget", "Advertis", "Suggest", "Recommend", "Spotlight", "Telemetry", "Bing", "AIData", "WindowsAI"];

    [GeneratedRegex(@"^SubscribedContent-\d+Enabled$")]
    private static partial Regex SubscribedContentRegex();

    [GeneratedRegex(@"^WNF_[A-Z0-9]{2,5}_[A-Z0-9_]{3,}$")]
    private static partial Regex WnfRegex();

    [GeneratedRegex(@"^Feature_[A-Za-z0-9_]{4,}$")]
    private static partial Regex FeatureRegex();

    /// <summary>Classifica uma cadeia já recortada (veja <see cref="ExtractRegistryPath"/>).</summary>
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

    /// <summary>Caminho de registro plausível: tem uma raiz conhecida, não é caminho de arquivo nem texto.</summary>
    public static bool IsRegistryPath(ReadOnlySpan<char> s)
    {
        if (s.Length < MinRegistryPathLength || s.Length > MaxStringLength) return false;
        if (s.IndexOfAny(RegistryRootSearch) < 0) return false;
        if (s.Contains(":\\", StringComparison.Ordinal)) return false;          // C:\..., caminho de arquivo
        if (s.IndexOfAny("<>\"|\t\r\n") >= 0) return false;
        if (s.Contains("\\\\", StringComparison.Ordinal)) return false;          // \\servidor ou escape duplo
        // Texto corrido ("Software\Microsoft é ..."): no máximo alguns espaços.
        int spaces = s.Count(' ');
        if (spaces > 6) return false;
        // Caminho de código-fonte ou de arquivo (chrome\browser\...\policies\x.cc, ...\x.dll).
        int lastSep = s.LastIndexOf('\\');
        int dot = s.LastIndexOf('.');
        if (dot > lastSep && SourceOrFileExtensions.Contains(s[(dot + 1)..].ToString())) return false;
        // Precisa ter ao menos dois segmentos não vazios.
        int segments = 0;
        foreach (var range in s.Split('\\'))
            if (range.End.Value > range.Start.Value) segments++;
        return segments >= 2;
    }

    /// <summary>
    /// Recorta o caminho de registro dentro de uma sequência de caracteres (bytes soltos antes do
    /// caminho são comuns em ASCII): começa na raiz conhecida mais à esquerda, incluindo os segmentos
    /// imediatamente anteriores (HKEY_LOCAL_MACHINE\, \Registry\Machine\). null se não houver caminho.
    /// </summary>
    public static string? ExtractRegistryPath(ReadOnlySpan<char> run)
    {
        int t = run.IndexOfAny(RegistryRootSearch);
        if (t < 0) return null;
        if (run[..t].Contains(":\\", StringComparison.Ordinal)) return null;   // C:\...\Software\x.dll: caminho de arquivo
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

    /// <summary>Identificador (letra, depois letras/dígitos/_), 5 a 80 caracteres.</summary>
    public static bool IsIdentifier(ReadOnlySpan<char> s)
    {
        if (s.Length < 5 || s.Length > 80 || !char.IsAsciiLetter(s[0])) return false;
        foreach (var c in s)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    /// <summary>Nome de valor que casa com os padrões de debloat.</summary>
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
        // Palavra quente em qualquer lugar: só sem "_" (Account_Copilot_Title, CopilotCard_Footer_X... são
        // chaves de recursos de texto, não valores de registro).
        if (char.IsAsciiLetterUpper(s[0]) && HasLower(s) && !s.Contains('_'))
            foreach (var w in HotWords)
                if (s.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Identificador com cara de nome de valor (PascalCase, 6 a 64 caracteres), usado para os
    /// nomes perto de um caminho de registro.
    /// </summary>
    public static bool IsNearNameCandidate(ReadOnlySpan<char> s)
    {
        if (s.Length < 6 || s.Length > 64 || !IsIdentifier(s) || !char.IsAsciiLetterUpper(s[0])) return false;
        if (!HasLower(s)) return false;
        // Sequências longas de consoantes/hex costumam ser lixo binário.
        int upper = 0;
        foreach (var c in s) if (char.IsAsciiLetterUpper(c)) upper++;
        return upper * 2 <= s.Length + 2;
    }

    private static bool HasLower(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (char.IsAsciiLetterLower(c)) return true;
        return false;
    }

    /// <summary>Ordem de prioridade quando o limite por arquivo corta o resultado (menor = mais importante).</summary>
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
