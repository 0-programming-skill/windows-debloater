using System.Text.RegularExpressions;
using Debloater.Core.Admx;
using Debloater.Core.Model;
using Debloater.Core.Platform;
using Debloater.Core.Reference;

namespace Debloater.Core.Policies;

public enum PolicyWarningLevel
{
    Info,
    Warning,
}

public sealed record PolicyWarning(PolicyWarningLevel Level, string Text)
{
    public override string ToString() => (Level == PolicyWarningLevel.Warning ? "Warning: " : "") + Text;
}

/// <summary>PC context for the warnings (injectable in tests).</summary>
public sealed record PolicyEnvironment(int Build, string EditionId, bool DomainJoined, IReadOnlyList<RegistryPolEntry> LocalGpoMachine, IReadOnlyList<RegistryPolEntry> LocalGpoUser)
{
    public static PolicyEnvironment Current(SystemInfo? system = null)
    {
        var s = system ?? SystemInfo.Current;
        return new PolicyEnvironment(s.Build, s.Edition, DomainInfo.IsDomainJoined,
            RegistryPolFile.TryRead(RegistryPolFile.MachinePath), RegistryPolFile.TryRead(RegistryPolFile.UserPath));
    }
}

/// <summary>
/// Risk and edition/build warnings for a policy, before applying it.
/// <para>
/// Risk (simple heuristic): <b>High</b> when the policy's file, category or key mentions
/// security, Windows Update or Defender (words: defender, update, security, bitlocker, firewall,
/// credential, smartscreen, device guard, kerberos, laps, applocker, tpm, uac, plus their Portuguese
/// equivalents); <b>Medium</b> otherwise. Policies are never Low: the program has not curated them.
/// </para>
/// </summary>
public static partial class PolicyAdvisor
{
    // Category names come from the ADML in the UI language (en-US or pt-BR), so the list keeps
    // Portuguese keywords ("atualiza", "segurança", "credenciais"...) next to the English ones.
    // They are matched against data, not shown to the user.
    private static readonly string[] s_highWords =
    [
        "defender", "update", "atualiza", "security", "segurança", "seguranca", "bitlocker", "firewall",
        "credential", "credenciais", "smartscreen", "device guard", "deviceguard", "kerberos", "laps",
        "applocker", "tpm", "uac", "credssp", "fve",
    ];

    public static RiskLevel Risk(AdmxPolicy policy)
    {
        var haystack = $"{policy.File} {string.Join(" ", policy.CategoryPath)} {policy.Key}";
        foreach (var w in s_highWords)
        {
            // Short words (laps, tpm, uac, fve) only as whole words: "laps" must not match "Collapse".
            var pattern = w.Length <= 4 ? $@"(?<!\p{{L}}){Regex.Escape(w)}(?!\p{{L}})" : Regex.Escape(w);
            if (Regex.IsMatch(haystack, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return RiskLevel.High;
        }
        return RiskLevel.Medium;
    }

    /// <summary>Edition family from the registry EditionID (Professional → Pro, Core → Home...).</summary>
    public static string EditionFamily(string editionId)
    {
        var e = editionId.Trim();
        if (e.StartsWith("Core", StringComparison.OrdinalIgnoreCase)) return "Home";
        if (e.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase)) return "IoT Enterprise";
        if (e.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)) return "Enterprise";
        if (e.StartsWith("Education", StringComparison.OrdinalIgnoreCase)) return "Education";
        if (e.StartsWith("Professional", StringComparison.OrdinalIgnoreCase)) return "Pro";
        if (e.StartsWith("Server", StringComparison.OrdinalIgnoreCase)) return "Server";
        return e;
    }

    // supportedOn text comes from the ADML in the UI language (en-US or pt-BR): keep the
    // Portuguese words ("somente", "apenas") next to "only".
    [GeneratedRegex(@"\b(only|somente|apenas)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnlyWord();

    [GeneratedRegex(@"Windows (10|11)|Windows Server (2016|2019|2022|2025)", RegexOptions.IgnoreCase)]
    private static partial Regex ModernWindows();

    [GeneratedRegex(@"Windows (XP|Vista|7|8|RT|10|11|2000)|Microsoft Windows (XP|Vista|7|8)", RegexOptions.IgnoreCase)]
    private static partial Regex ClientWindows();

    public static IReadOnlyList<PolicyWarning> Warnings(AdmxPolicy policy, CspPolicy? csp, PolicyEnvironment env, PolicyTargetState target, PolicyScope scope)
    {
        var list = new List<PolicyWarning>();
        void Warn(string text) => list.Add(new PolicyWarning(PolicyWarningLevel.Warning, text));
        void Info(string text) => list.Add(new PolicyWarning(PolicyWarningLevel.Info, text));

        var family = EditionFamily(env.EditionId);
        // Edition/build warnings only matter when something will be written (Not configured only deletes).
        bool writes = target != PolicyTargetState.NotConfigured;
        if (family == "Home" && writes)
            Warn("Windows Home does not officially process Group Policy: many policies work through the registry, others are ignored.");

        // supportedOn (ADML text).
        var sup = writes ? policy.SupportedOn ?? "" : "";
        if (sup.Length > 0)
        {
            if (OnlyWord().IsMatch(sup) && !ModernWindows().IsMatch(sup))
                Warn($"Supported on \"{sup}\": probably has no effect on this Windows (build {env.Build}).");
            else if (sup.Contains("Server", StringComparison.OrdinalIgnoreCase) && !ClientWindows().IsMatch(sup) && family != "Server")
                Warn($"Supported on \"{sup}\": Windows Server policy; probably has no effect on this PC.");
            if (sup.Contains("Internet Explorer", StringComparison.OrdinalIgnoreCase))
                Warn("Internet Explorer policy; IE is disabled on Windows 11, so it usually has no effect.");
            foreach (var ed in new[] { "Enterprise", "Education" })
            {
                if (sup.Contains(ed, StringComparison.OrdinalIgnoreCase) && !sup.Contains("Pro", StringComparison.OrdinalIgnoreCase)
                    && family is "Pro" or "Home")
                {
                    Warn($"Supported on \"{sup}\": this PC's edition ({env.EditionId}) may ignore this policy.");
                    break;
                }
            }
        }

        // Policy CSP (Microsoft Learn documentation).
        if (csp is not null && writes)
        {
            if (csp.Editions.Count > 0 && family is not ("Server" or "")
                && !csp.Editions.Any(e => e.StartsWith(family, StringComparison.OrdinalIgnoreCase)))
                Warn($"According to the documentation ({csp.Reference}), the policy only takes effect on: {string.Join(", ", csp.Editions)}. This PC is {family} ({env.EditionId}).");
            if (csp.Win11MinBuild is { } min && env.Build >= 22000 && env.Build < min)
                Warn($"According to the documentation, it requires build {min} or newer; this PC is on {env.Build}.");
            if (csp.Deprecated) Warn($"The documentation marks the policy as deprecated ({csp.Reference}).");
            if (csp.InsiderOnly) Warn("The documentation says the policy only exists in Windows Insider builds.");
            foreach (var note in (csp.EditionNotes ?? []).Take(2)) Info("Documentation: " + note);
        }

        if (env.DomainJoined)
            Warn("This PC is joined to a domain: domain GPOs take precedence and may overwrite the value written here on the next policy refresh.");

        // Local GPO (gpedit): if it also configures the policy, Windows reapplies its value.
        var gpo = scope == PolicyScope.Machine ? env.LocalGpoMachine : env.LocalGpoUser;
        if (gpo.Count > 0 && PolicyKeys(policy).Any(k => gpo.Any(e => RegistryPolFile.Touches(e, k.Key, k.Name))))
            Warn("The local GPO (gpedit.msc) also configures this policy: on the next policy refresh (gpupdate, ~90 min or at restart) Windows reapplies the gpedit value. Change it in gpedit or leave it as Not configured there.");

        if (target != PolicyTargetState.NotConfigured)
            Info("Written directly to the registry: gpedit.msc keeps showing \"Not configured\". To undo, use Revert or Not configured here.");
        if (scope == PolicyScope.User)
            Info("User scope: writes to the HKCU of the account running the program (when run as administrator, the elevated account).");
        return list;
    }

    /// <summary>Index "File.admx:Name" → CSP policy, from the documentation's ADMX mapping.</summary>
    public static Dictionary<string, CspPolicy> IndexCspByAdmx(IEnumerable<CspPolicy> csp)
    {
        var index = new Dictionary<string, CspPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in csp)
            foreach (var m in p.GpMappings ?? [])
                if (m.AdmxFile is not null && m.Name is not null)
                    index.TryAdd(m.AdmxFile + ":" + m.Name, p);
        return index;
    }

    /// <summary>(key, name) pairs the policy writes; null name = the whole key (list).</summary>
    public static IEnumerable<(string Key, string? Name)> PolicyKeys(AdmxPolicy policy)
    {
        foreach (var (key, name, _) in PolicyWriter.OwnedValues(policy)) yield return (key, name);
        foreach (var el in policy.Elements.Where(e => e.Type == "list")) yield return (el.Key, null);
    }
}
