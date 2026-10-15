using Debloater.Core.Actions;
using Debloater.Core.Admx;
using Debloater.Core.Model;
using Microsoft.Win32;

namespace Debloater.Core.Reference;

/// <summary>Como uma ação de registro corresponde a uma política ADMX.</summary>
public enum AdmxVia
{
    /// <summary>key + valueName da própria política.</summary>
    PolicyValue,
    /// <summary>Item de enabledList/disabledList.</summary>
    List,
    /// <summary>Elemento (decimal, enum, boolean, text...).</summary>
    Element,
    /// <summary>Item de valueList de uma opção de enum, ou trueList/falseList de boolean.</summary>
    ElementList,
    /// <summary>Elemento list (nomes de valor livres sob a chave).</summary>
    ListElement,
}

public sealed record AdmxMatch(AdmxPolicy Policy, AdmxVia Via, AdmxElement? Element, bool HiveMatches);

public sealed record ActionMatch(ActionSpec Action, IReadOnlyList<AdmxMatch> Admx, IReadOnlyList<CspPolicy> Csp)
{
    public bool HasAny => Admx.Count > 0 || Csp.Count > 0;
}

public sealed record TweakMatch(Tweak Tweak, IReadOnlyList<ActionMatch> Actions)
{
    public IEnumerable<AdmxPolicy> AdmxPolicies => Actions.SelectMany(a => a.Admx.Where(m => m.HiveMatches).Select(m => m.Policy)).Distinct();
    public IEnumerable<CspPolicy> CspPolicies => Actions.SelectMany(a => a.Csp).Distinct();
}

/// <summary>
/// Casa as ações de registro do catálogo com políticas ADMX (pela chave + nome do valor, respeitando
/// a classe Machine/User) e com a Policy CSP (pelo mapeamento GP da documentação: arquivo ADMX +
/// nome da política, ou chave + nome do valor).
/// </summary>
public sealed class ReferenceMatcher
{
    private readonly Dictionary<(string Key, string Value), List<(AdmxPolicy, AdmxVia, AdmxElement?)>> _admxByValue = [];
    private readonly Dictionary<string, List<(AdmxPolicy, AdmxElement)>> _admxListKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string File, string Name), List<CspPolicy>> _cspByGp = [];
    private readonly Dictionary<(string Key, string Value), List<CspPolicy>> _cspByValue = [];

    public ReferenceMatcher(IEnumerable<AdmxPolicy>? admx, IEnumerable<CspPolicy>? csp)
    {
        foreach (var p in admx ?? [])
        {
            if (p.ValueName is not null) AddAdmx(p.Key, p.ValueName, p, AdmxVia.PolicyValue, null);
            foreach (var i in (p.EnabledList ?? []).Concat(p.DisabledList ?? [])) AddAdmx(i.Key, i.ValueName, p, AdmxVia.List, null);
            foreach (var e in p.Elements)
            {
                if (e.Type == "list")
                {
                    if (!_admxListKeys.TryGetValue(Norm(e.Key), out var l)) _admxListKeys[Norm(e.Key)] = l = [];
                    l.Add((p, e));
                    continue;
                }
                if (e.ValueName is not null) AddAdmx(e.Key, e.ValueName, p, AdmxVia.Element, e);
                foreach (var item in e.Items ?? [])
                    foreach (var li in item.ValueList ?? []) AddAdmx(li.Key, li.ValueName, p, AdmxVia.ElementList, e);
                foreach (var li in (e.TrueList ?? []).Concat(e.FalseList ?? [])) AddAdmx(li.Key, li.ValueName, p, AdmxVia.ElementList, e);
            }
        }
        foreach (var c in csp ?? [])
        {
            foreach (var m in c.GpMappings ?? [])
            {
                if (m.AdmxFile is not null && m.Name is not null) Add(_cspByGp, (Norm(m.AdmxFile), Norm(m.Name)), c);
                if (m.RegistryKey is not null && m.RegistryValueName is not null) Add(_cspByValue, (Norm(m.RegistryKey), Norm(m.RegistryValueName)), c);
            }
        }
    }

    private void AddAdmx(string key, string valueName, AdmxPolicy p, AdmxVia via, AdmxElement? e)
    {
        var k = (Norm(key), Norm(valueName));
        if (!_admxByValue.TryGetValue(k, out var list)) _admxByValue[k] = list = [];
        if (!list.Any(x => x.Item1 == p && x.Item2 == via && x.Item3 == e)) list.Add((p, via, e));
    }

    private static void Add<TKey>(Dictionary<TKey, List<CspPolicy>> dict, TKey key, CspPolicy c) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var list)) dict[key] = list = [];
        if (!list.Contains(c)) list.Add(c);
    }

    /// <summary>Chave normalizada: sem barras sobrando, maiúsculas (o registro não diferencia).</summary>
    internal static string Norm(string s) => s.Trim().Trim('\\').ToUpperInvariant();

    public TweakMatch Match(Tweak tweak) =>
        new(tweak, tweak.Actions.Select(MatchAction).ToList());

    public ActionMatch MatchAction(ActionSpec action)
    {
        if (action.Type != ActionSpec.Registry || action.Path is null || action.Name is null
            || !RegistryActionHandler.TryParsePath(action.Path, out var hive, out var subKey))
            return new ActionMatch(action, [], []);

        var admx = new List<AdmxMatch>();
        if (_admxByValue.TryGetValue((Norm(subKey), Norm(action.Name)), out var hits))
            admx.AddRange(hits.Select(h => new AdmxMatch(h.Item1, h.Item2, h.Item3, HiveMatches(h.Item1.Class, hive))));
        if (_admxListKeys.TryGetValue(Norm(subKey), out var listHits))
            admx.AddRange(listHits.Select(h => new AdmxMatch(h.Item1, AdmxVia.ListElement, h.Item2, HiveMatches(h.Item1.Class, hive))));

        admx = KeepMostSpecific(admx);

        var csp = new List<CspPolicy>();
        foreach (var m in admx.Where(m => m.HiveMatches))
            if (_cspByGp.TryGetValue((Norm(m.Policy.File), Norm(m.Policy.Name)), out var byGp))
                foreach (var c in Disambiguate(byGp, action.Name)) if (!csp.Contains(c)) csp.Add(c);
        if (_cspByValue.TryGetValue((Norm(subKey), Norm(action.Name)), out var byValue))
            foreach (var c in byValue.Where(c => CspHiveMatches(c, hive))) if (!csp.Contains(c)) csp.Add(c);

        return new ActionMatch(action, admx, csp);
    }

    private static int Rank(AdmxVia via) => via switch
    {
        AdmxVia.PolicyValue or AdmxVia.Element => 0,
        AdmxVia.ElementList or AdmxVia.ListElement => 1,
        _ => 2,
    };

    /// <summary>
    /// Fica só com as correspondências mais específicas: se o valor é o valueName de uma política
    /// (ou de um elemento), descarta políticas "guarda-chuva" que só o citam numa lista
    /// (ex.: ICM.admx:InternetManagement_RestrictCommunication_2 grava dezenas de valores).
    /// </summary>
    private static List<AdmxMatch> KeepMostSpecific(List<AdmxMatch> matches)
    {
        var ok = matches.Where(m => m.HiveMatches).ToList();
        if (ok.Count == 0) return matches;
        int best = ok.Min(m => Rank(m.Via));
        return ok.Where(m => Rank(m.Via) == best).ToList();
    }

    /// <summary>
    /// Uma política ADMX pode corresponder a várias entradas da CSP (uma por elemento, ex.:
    /// AutoUpdateCfg → AllowAutoUpdate, ScheduledInstallDay...). Preferimos a de nome igual ao valor;
    /// senão a de nome mais parecido (substring comum ≥ 5); com muitas candidatas sem pista, nenhuma.
    /// </summary>
    internal static IEnumerable<CspPolicy> Disambiguate(List<CspPolicy> candidates, string valueName)
    {
        if (candidates.Count <= 1) return candidates;
        var exact = candidates.Where(c => c.Name.Equals(valueName, StringComparison.OrdinalIgnoreCase)
            || (c.GpMappings ?? []).Any(m => string.Equals(m.RegistryValueName, valueName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count > 0) return exact;
        var scored = candidates.Select(c => (c, Score: LongestCommonSubstring(c.Name, valueName))).ToList();
        int max = scored.Max(x => x.Score);
        if (max >= 5) return scored.Where(x => x.Score == max).Select(x => x.c);
        return candidates.Count <= 3 ? candidates : [];
    }

    private static int LongestCommonSubstring(string a, string b)
    {
        a = a.ToUpperInvariant();
        b = b.ToUpperInvariant();
        int best = 0;
        var prev = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            for (int j = 1; j <= b.Length; j++)
                if (a[i - 1] == b[j - 1]) best = Math.Max(best, cur[j] = prev[j - 1] + 1);
            prev = cur;
        }
        return best;
    }

    private static bool HiveMatches(AdmxClass cls, RegistryHive hive) => cls switch
    {
        AdmxClass.Machine => hive == RegistryHive.LocalMachine,
        AdmxClass.User => hive == RegistryHive.CurrentUser,
        _ => hive is RegistryHive.LocalMachine or RegistryHive.CurrentUser,
    };

    /// <summary>"Computer Configuration" → HKLM, "User Configuration" → HKCU, "Computer and User" → ambos.</summary>
    private static bool CspHiveMatches(CspPolicy c, RegistryHive hive)
    {
        var loc = c.GpMappings?.FirstOrDefault()?.Location ?? "";
        bool computer = loc.Contains("Computer", StringComparison.OrdinalIgnoreCase);
        bool user = loc.Contains("User", StringComparison.OrdinalIgnoreCase);
        if (!computer && !user) return true;
        return hive == RegistryHive.LocalMachine ? computer : user;
    }

    // ---------------- confiança e fontes ----------------

    /// <summary>
    /// Confiança sugerida: a melhor fonte encontrada para alguma ação do tweak
    /// (official = Policy CSP; admx = só ADMX; community = nenhuma). Se a validação achar um
    /// problema grave (valor fora do que a política permite, HKLM/HKCU trocados), questionable.
    /// </summary>
    public static Confidence SuggestConfidence(TweakMatch match, IEnumerable<ReferenceFinding> findings)
    {
        var mine = findings.Where(f => f.TweakId == match.Tweak.Id).ToList();
        if (mine.Any(f => f.MakesQuestionable)) return Confidence.Questionable;
        // HKLM/HKCU trocados só tornam o tweak duvidoso se nenhuma outra ação acertar a política.
        if (mine.Any(f => f.Code == ReferenceValidator.AdmxHive) && !match.AdmxPolicies.Any()) return Confidence.Questionable;
        if (match.CspPolicies.Any()) return Confidence.Official;
        if (match.AdmxPolicies.Any()) return Confidence.Admx;
        return Confidence.Community;
    }

    public static List<SourceRef> SuggestSources(TweakMatch match)
    {
        var sources = new List<SourceRef>();
        foreach (var c in match.CspPolicies.OrderBy(c => c.Reference, StringComparer.Ordinal))
            sources.Add(new SourceRef { Kind = SourceRef.MsDocs, Ref = c.Reference });
        foreach (var p in match.AdmxPolicies.OrderBy(p => p.QualifiedName, StringComparer.Ordinal))
            sources.Add(new SourceRef { Kind = SourceRef.AdmxKind, Ref = p.QualifiedName });
        return sources.DistinctBy(s => (s.Kind, s.Ref)).ToList();
    }
}
