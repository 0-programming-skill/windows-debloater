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
    public override string ToString() => (Level == PolicyWarningLevel.Warning ? "Atenção: " : "") + Text;
}

/// <summary>Contexto do PC para os avisos (injetável nos testes).</summary>
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
/// Risco e avisos de edição/build de uma política, antes de aplicar.
/// <para>
/// Risco (heurística simples): <b>Alto</b> quando o arquivo, a categoria ou a chave da política fala de
/// segurança, Windows Update ou Defender (palavras: defender, update, atualiza, security, segurança,
/// bitlocker, firewall, credential/credenciais, smartscreen, device guard, kerberos, laps, applocker,
/// tpm, uac); <b>Médio</b> nos demais casos. Políticas nunca são Baixo: o programa não as curou.
/// </para>
/// </summary>
public static partial class PolicyAdvisor
{
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
            // Palavras curtas (laps, tpm, uac, fve) só como palavra inteira: "laps" não casa com "Collapse".
            var pattern = w.Length <= 4 ? $@"(?<!\p{{L}}){Regex.Escape(w)}(?!\p{{L}})" : Regex.Escape(w);
            if (Regex.IsMatch(haystack, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return RiskLevel.High;
        }
        return RiskLevel.Medium;
    }

    /// <summary>Família da edição a partir do EditionID do registro (Professional → Pro, Core → Home...).</summary>
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
        if (family == "Home")
            Warn("Windows Home não processa oficialmente Políticas de Grupo: muitas funcionam pelo registro, outras são ignoradas.");

        // supportedOn (texto do ADML).
        var sup = policy.SupportedOn ?? "";
        if (sup.Length > 0)
        {
            if (OnlyWord().IsMatch(sup) && !ModernWindows().IsMatch(sup))
                Warn($"Com suporte em \"{sup}\": provavelmente sem efeito neste Windows (build {env.Build}).");
            else if (sup.Contains("Server", StringComparison.OrdinalIgnoreCase) && !ClientWindows().IsMatch(sup) && family != "Server")
                Warn($"Com suporte em \"{sup}\": política de Windows Server; provavelmente sem efeito neste PC.");
            if (sup.Contains("Internet Explorer", StringComparison.OrdinalIgnoreCase))
                Warn("Política do Internet Explorer, que foi desativado no Windows 11: em geral não tem efeito.");
            foreach (var ed in new[] { "Enterprise", "Education" })
            {
                if (sup.Contains(ed, StringComparison.OrdinalIgnoreCase) && !sup.Contains("Pro", StringComparison.OrdinalIgnoreCase)
                    && family is "Pro" or "Home")
                {
                    Warn($"Com suporte em \"{sup}\": a edição deste PC ({env.EditionId}) pode ignorar esta política.");
                    break;
                }
            }
        }

        // Policy CSP (documentação Microsoft Learn).
        if (csp is not null)
        {
            if (csp.Editions.Count > 0 && family is not ("Server" or "")
                && !csp.Editions.Any(e => e.StartsWith(family, StringComparison.OrdinalIgnoreCase)))
                Warn($"Pela documentação ({csp.Reference}), a política só tem efeito em: {string.Join(", ", csp.Editions)}. Este PC é {family} ({env.EditionId}).");
            if (csp.Win11MinBuild is { } min && env.Build >= 22000 && env.Build < min)
                Warn($"Pela documentação, exige o build {min} ou mais novo; este PC está no {env.Build}.");
            if (csp.Deprecated) Warn($"A documentação marca a política como obsoleta ({csp.Reference}).");
            if (csp.InsiderOnly) Warn("A documentação diz que a política só existe em builds do Windows Insider.");
            foreach (var note in (csp.EditionNotes ?? []).Take(2)) Info("Documentação: " + note);
        }

        if (env.DomainJoined)
            Warn("Este PC está num domínio: GPOs do domínio têm prioridade e podem sobrescrever o valor gravado aqui na próxima atualização de política.");

        // GPO local (gpedit): se ela também configura a política, o Windows reaplica o valor dela.
        var gpo = scope == PolicyScope.Machine ? env.LocalGpoMachine : env.LocalGpoUser;
        if (gpo.Count > 0 && PolicyKeys(policy).Any(k => gpo.Any(e => RegistryPolFile.Touches(e, k.Key, k.Name))))
            Warn("A GPO local (gpedit.msc) também configura esta política: ao atualizar a política (gpupdate, ~90 min ou ao reiniciar) o Windows reaplica o valor do gpedit. Ajuste-a no gpedit ou deixe-a como Não configurada lá.");

        if (target != PolicyTargetState.NotConfigured)
            Info("Gravação direta no registro: o gpedit.msc continua mostrando \"Não configurada\". Para desfazer, use Reverter ou Não configurada aqui.");
        if (scope == PolicyScope.User)
            Info("Escopo Usuário: grava no HKCU da conta que executa o programa (como administrador, a conta elevada).");
        return list;
    }

    /// <summary>(chave, nome) que a política grava; nome null = a chave inteira (list).</summary>
    public static IEnumerable<(string Key, string? Name)> PolicyKeys(AdmxPolicy policy)
    {
        foreach (var (key, name, _) in PolicyWriter.OwnedValues(policy)) yield return (key, name);
        foreach (var el in policy.Elements.Where(e => e.Type == "list")) yield return (el.Key, null);
    }
}
