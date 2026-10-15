using System.Globalization;
using System.Text.RegularExpressions;
using Debloater.Core.Admx;
using Debloater.Core.Model;

namespace Debloater.Core.Reference;

public enum FindingSeverity
{
    Warning,
    Info,
}

/// <summary>Um aviso do 'validate --reference'. Nunca é erro: o catálogo continua válido.</summary>
public sealed record ReferenceFinding(FindingSeverity Severity, string Code, string File, string TweakId, string Message)
{
    /// <summary>Indica que o tweak provavelmente não funciona como descrito.</summary>
    public bool MakesQuestionable => Severity == FindingSeverity.Warning && Code is ReferenceValidator.AdmxValue or ReferenceValidator.CspInsider;

    /// <summary>Linha estável (sem números de linha) usada como baseline no CI.</summary>
    public string BaselineKey => $"{(Severity == FindingSeverity.Warning ? "WARN" : "INFO")} {Code} {TweakId}: {Message}";

    public override string ToString() => $"{(Severity == FindingSeverity.Warning ? "AVISO" : "info ")} {File} [{TweakId}] {Code}: {Message}";
}

/// <summary>
/// Confere o catálogo contra as referências oficiais:
/// - ADMX: o valor/tipo gravado é um dos estados que a política prevê; HKLM/HKCU batem com a classe;
/// - Policy CSP: restrições de edição (ex.: AllowTelemetry=0 só vale em Enterprise/Education),
///   SO aplicável (minBuild/maxBuild), políticas só do Insider ou obsoletas;
/// - confidence declarada no YAML x encontrada.
/// </summary>
public static partial class ReferenceValidator
{
    public const string AdmxValue = "ADMX_VALUE";
    public const string AdmxValueDelete = "ADMX_DISABLED_BY_DELETE";
    public const string AdmxKind = "ADMX_KIND";
    public const string AdmxHive = "ADMX_HIVE";
    public const string CspEdition = "CSP_EDITION";
    public const string CspEditionValue = "CSP_EDITION_VALUE";
    public const string CspEditionNote = "CSP_EDITION_NOTE";
    public const string CspBuild = "CSP_BUILD";
    public const string CspInsider = "CSP_INSIDER";
    public const string CspDeprecated = "CSP_DEPRECATED";
    public const string CspValue = "CSP_VALUE";
    public const string PolicyUnknown = "POLICY_UNKNOWN";
    public const string ConfidenceMismatch = "CONFIDENCE";
    public const string SourceUnknown = "SOURCE_UNKNOWN";

    private const int Win11FirstBuild = 22000;

    [GeneratedRegex(@"\\Policies\\", RegexOptions.IgnoreCase)]
    private static partial Regex PoliciesKey();

    [GeneratedRegex(@"^\[([^\]]+)\]\s*")]
    private static partial Regex NoteValue();

    public static List<ReferenceFinding> Validate(IEnumerable<Tweak> tweaks, ReferenceMatcher matcher,
        IReadOnlyCollection<AdmxPolicy>? admx = null, IReadOnlyCollection<CspPolicy>? csp = null)
    {
        var findings = new List<ReferenceFinding>();
        var matches = new List<TweakMatch>();
        foreach (var tweak in tweaks)
        {
            var match = matcher.Match(tweak);
            matches.Add(match);
            var local = new List<ReferenceFinding>();
            void Add(FindingSeverity sev, string code, string msg)
            {
                var f = new ReferenceFinding(sev, code, tweak.SourceFile ?? "?", tweak.Id, msg);
                if (!local.Contains(f)) local.Add(f);
            }

            foreach (var am in match.Actions)
                CheckAction(tweak, am, Add);

            foreach (var c in match.CspPolicies)
                CheckCsp(tweak, c, match, Add);

            CheckDeclared(tweak, match, local, admx, csp, Add);
            findings.AddRange(local);
        }
        return findings;
    }

    // ---------------- ADMX ----------------

    private static void CheckAction(Tweak tweak, ActionMatch am, Action<FindingSeverity, string, string> add)
    {
        var a = am.Action;
        if (a.Type != ActionSpec.Registry) return;
        var target = $@"{a.Path}\{a.Name}";

        if (!am.HasAny)
        {
            if (a.Path is not null && PoliciesKey().IsMatch(a.Path))
                add(FindingSeverity.Info, PolicyUnknown, $"{target}: chave de política sem política ADMX/CSP correspondente nas referências carregadas.");
            return;
        }

        var hiveOk = am.Admx.Where(m => m.HiveMatches).ToList();
        if (am.Admx.Count > 0 && hiveOk.Count == 0)
        {
            var p = am.Admx[0].Policy;
            add(FindingSeverity.Warning, AdmxHive,
                $"{target}: a política {p.QualifiedName} é da classe {p.Class} ({(p.Class == AdmxClass.Machine ? "HKLM" : "HKCU")}); o Windows não lê esse valor na outra raiz.");
            return;
        }
        if (a.Delete || hiveOk.Count == 0) return; // apagar = "não configurada": sempre válido

        var verdicts = hiveOk.Select(m => Allowed(m, a)).ToList();
        if (verdicts.Any(v => v.Ok)) return;

        var policies = string.Join(", ", hiveOk.Select(m => m.Policy.QualifiedName).Distinct());
        if (verdicts.Any(v => v.DisabledByDelete))
        {
            add(FindingSeverity.Info, AdmxValueDelete,
                $"{target}={Show(a.Value)}: {policies} desabilita apagando o valor; {Show(a.Value)} não é um estado previsto pelo ADMX (geralmente funciona como 'desabilitada').");
            return;
        }
        var kindProblem = verdicts.FirstOrDefault(v => v.KindMismatch is not null)?.KindMismatch;
        if (kindProblem is not null)
        {
            add(FindingSeverity.Warning, AdmxKind, $"{target}: {kindProblem} ({policies}).");
            return;
        }
        var expected = string.Join("; ", verdicts.Select(v => v.Expected).Where(e => e.Length > 0).Distinct());
        add(FindingSeverity.Warning, AdmxValue, $"{target}={Show(a.Value)} não é um valor previsto por {policies} (previstos: {expected}).");
    }

    private sealed record Verdict(bool Ok, string Expected, bool DisabledByDelete = false, string? KindMismatch = null);

    private static Verdict Allowed(AdmxMatch m, ActionSpec a)
    {
        var p = m.Policy;
        switch (m.Via)
        {
            case AdmxVia.PolicyValue:
            {
                var on = p.EnabledValue ?? new Admx.AdmxValue(AdmxValueKind.Decimal, "1");
                var candidates = new List<Admx.AdmxValue> { on };
                if (p.DisabledValue is { Kind: not AdmxValueKind.Delete } off) candidates.Add(off);
                var kindIssue = KindIssue(candidates[0].Kind, a);
                if (candidates.Any(c => ValueEquals(c, a))) return new Verdict(true, "");
                bool byDelete = p.DisabledValue is null or { Kind: AdmxValueKind.Delete };
                return new Verdict(false, string.Join(" | ", candidates.Select(c => c.ToString())) + (byDelete ? " | (apagar)" : ""),
                    byDelete && IsZero(a), kindIssue);
            }
            case AdmxVia.List:
            {
                var items = (p.EnabledList ?? []).Concat(p.DisabledList ?? [])
                    .Where(i => Same(i.Key, a) && i.ValueName.Equals(a.Name, StringComparison.OrdinalIgnoreCase)
                                && i.Value.Kind != AdmxValueKind.Delete)
                    .Select(i => i.Value).ToList();
                if (items.Count == 0 || items.Any(v => ValueEquals(v, a))) return new Verdict(true, "");
                return new Verdict(false, string.Join(" | ", items.Select(v => v.ToString())), KindMismatch: KindIssue(items[0].Kind, a));
            }
            case AdmxVia.ElementList:
            {
                var e = m.Element!;
                var items = (e.Items ?? []).SelectMany(i => i.ValueList ?? []).Concat(e.TrueList ?? []).Concat(e.FalseList ?? [])
                    .Where(i => Same(i.Key, a) && i.ValueName.Equals(a.Name, StringComparison.OrdinalIgnoreCase) && i.Value.Kind != AdmxValueKind.Delete)
                    .Select(i => i.Value).ToList();
                if (items.Count == 0 || items.Any(v => ValueEquals(v, a))) return new Verdict(true, "");
                return new Verdict(false, string.Join(" | ", items.Select(v => v.ToString())));
            }
            case AdmxVia.ListElement:
                return new Verdict(true, "");
            case AdmxVia.Element:
                return ElementAllows(m.Element!, a);
            default:
                return new Verdict(true, "");
        }
    }

    private static Verdict ElementAllows(AdmxElement e, ActionSpec a)
    {
        switch (e.Type)
        {
            case "decimal":
            case "longDecimal":
            {
                var expectKind = e.StoreAsText ? "string" : e.Type == "decimal" ? "dword" : "qword";
                if (!KindIs(a, expectKind))
                    return new Verdict(false, "", KindMismatch: $"tipo '{a.Kind}', o ADMX grava {expectKind}");
                if (!TryNumber(a.Value, out var n)) return new Verdict(false, $"número entre {e.MinValue} e {e.MaxValue}");
                bool ok = (e.MinValue is null || n >= e.MinValue) && (e.MaxValue is null || n <= e.MaxValue);
                return new Verdict(ok, $"{e.MinValue}..{e.MaxValue}");
            }
            case "enum":
            {
                var values = (e.Items ?? []).Select(i => i.Value).Where(v => v.Kind != AdmxValueKind.Delete).ToList();
                if (values.Any(v => ValueEquals(v, a))) return new Verdict(true, "");
                var desc = string.Join(" | ", (e.Items ?? []).Select(i => $"{i.Value} ({i.DisplayName})"));
                return new Verdict(false, desc, KindMismatch: values.Count > 0 ? KindIssue(values[0].Kind, a) : null);
            }
            case "boolean":
            {
                var t = e.TrueValue ?? new Admx.AdmxValue(AdmxValueKind.Decimal, "1");
                var f = e.FalseValue ?? new Admx.AdmxValue(AdmxValueKind.Decimal, "0");
                var cands = new[] { t, f }.Where(v => v.Kind != AdmxValueKind.Delete).ToList();
                if (cands.Any(v => ValueEquals(v, a))) return new Verdict(true, "");
                return new Verdict(false, string.Join(" | ", cands.Select(v => v.ToString())));
            }
            case "text":
                return KindIs(a, e.Expandable ? "expandString" : "string") || KindIs(a, "string")
                    ? new Verdict(true, "")
                    : new Verdict(false, "", KindMismatch: $"tipo '{a.Kind}', o ADMX grava texto");
            case "multiText":
                return KindIs(a, "multiString") ? new Verdict(true, "") : new Verdict(false, "", KindMismatch: $"tipo '{a.Kind}', o ADMX grava multiString");
            default:
                return new Verdict(true, "");
        }
    }

    private static string? KindIssue(AdmxValueKind expected, ActionSpec a) => expected switch
    {
        AdmxValueKind.Decimal when !KindIs(a, "dword") => $"tipo '{a.Kind}', o ADMX grava dword",
        AdmxValueKind.LongDecimal when !KindIs(a, "qword") => $"tipo '{a.Kind}', o ADMX grava qword",
        AdmxValueKind.String when !KindIs(a, "string") && !KindIs(a, "expandString") => $"tipo '{a.Kind}', o ADMX grava string",
        _ => null,
    };

    private static bool KindIs(ActionSpec a, string kind) => string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string key, ActionSpec a) =>
        a.Path is not null && Debloater.Core.Actions.RegistryActionHandler.TryParsePath(a.Path, out _, out var sub)
        && ReferenceMatcher.Norm(sub) == ReferenceMatcher.Norm(key);

    private static bool ValueEquals(Admx.AdmxValue v, ActionSpec a) => v.Kind switch
    {
        AdmxValueKind.Decimal => KindIs(a, "dword") && TryNumber(a.Value, out var n) && ulong.TryParse(v.Data, out var e) && n == e,
        AdmxValueKind.LongDecimal => KindIs(a, "qword") && TryNumber(a.Value, out var n2) && ulong.TryParse(v.Data, out var e2) && n2 == e2,
        AdmxValueKind.String => (KindIs(a, "string") || KindIs(a, "expandString")) && string.Equals(Convert.ToString(a.Value, CultureInfo.InvariantCulture), v.Data, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool IsZero(ActionSpec a) => TryNumber(a.Value, out var n) && n == 0;

    internal static bool TryNumber(object? value, out ulong n)
    {
        n = 0;
        var s = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n);
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            n = unchecked((ulong)l) & (l < 0 ? 0xFFFFFFFFUL : ulong.MaxValue);
            return true;
        }
        return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
    }

    private static string Show(object? v) => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";

    // ---------------- Policy CSP ----------------

    private static void CheckCsp(Tweak tweak, CspPolicy c, TweakMatch match, Action<FindingSeverity, string, string> add)
    {
        var text = $"{tweak.Description} {tweak.Breaks}";
        bool tweakMentionsEdition = text.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || text.Contains("Education", StringComparison.OrdinalIgnoreCase);

        if (!c.Editions.Contains("Pro", StringComparer.OrdinalIgnoreCase))
        {
            var editions = c.Editions.Count == 0 ? "nenhuma edição de desktop" : string.Join(", ", c.Editions);
            add(tweakMentionsEdition ? FindingSeverity.Info : FindingSeverity.Warning, CspEdition,
                $"{c.Reference}: a documentação lista só {editions} (não Pro/Home); o efeito no Home/Pro não é garantido.");
        }

        // Notas de edição ligadas a um valor ("[0] ...") ou à política inteira.
        var actions = match.Actions.Where(a => a.Csp.Contains(c)).Select(a => a.Action).ToList();
        foreach (var note in c.EditionNotes ?? [])
        {
            var m = NoteValue().Match(note);
            if (m.Success)
            {
                var value = m.Groups[1].Value;
                var hit = actions.FirstOrDefault(a => !a.Delete && Show(a.Value).Equals(value, StringComparison.OrdinalIgnoreCase)
                    || TryNumber(a.Value, out var n) && ulong.TryParse(value, out var v) && n == v);
                if (hit is not null)
                    add(tweakMentionsEdition ? FindingSeverity.Info : FindingSeverity.Warning, CspEditionValue,
                        $"{c.Reference}={value}: {note[m.Length..]}{(tweakMentionsEdition ? " (o tweak já menciona a restrição)" : "")}");
            }
            else
            {
                add(FindingSeverity.Info, CspEditionNote, $"{c.Reference}: {note}");
            }
        }

        if (c.InsiderOnly)
        {
            bool inLocalAdmx = actions.Count > 0 && match.Actions.Any(a => a.Csp.Contains(c) && a.Admx.Any(x => x.HiveMatches));
            if (inLocalAdmx)
                add(FindingSeverity.Info, CspInsider, $"{c.Reference}: a documentação ainda diz 'só Windows Insider', mas o ADMX carregado já define a política.");
            else
                add(FindingSeverity.Warning, CspInsider, $"{c.Reference}: documentada só para builds do Windows Insider.");
        }
        if (c.Deprecated)
            add(FindingSeverity.Warning, CspDeprecated, $"{c.Reference}: a documentação marca a política como obsoleta (deprecated).");

        if (c.Win11MinBuild is { } min)
        {
            int tweakMin = tweak.MinBuild ?? Win11FirstBuild;
            if (tweak.MaxBuild is { } max && max < min)
                add(FindingSeverity.Warning, CspBuild, $"{c.Reference}: existe a partir do build {min}, mas o tweak tem maxBuild {max}.");
            else if (tweakMin < min)
                add(FindingSeverity.Warning, CspBuild,
                    $"{c.Reference}: aplicável a partir do build {min}{(c.Win11MinUbr is { } ubr ? $".{ubr}" : "")}; o tweak não define minBuild ≥ {min} (atual: {tweak.MinBuild?.ToString(CultureInfo.InvariantCulture) ?? "nenhum"}).");
            else if (tweakMin == min && c.Win11MinUbr is { } ubr2)
                add(FindingSeverity.Info, CspBuild, $"{c.Reference}: exige o build {min}.{ubr2} (com atualização cumulativa); minBuild não expressa o UBR.");
        }

        // Valores da CSP: só quando não há ADMX validando e a CSP grava o mesmo valor no registro.
        if (c.AllowedValues is { Count: > 0 } allowed && !c.AdmxBacked && c.Format == "int")
        {
            foreach (var am in match.Actions.Where(a => a.Csp.Contains(c) && !a.Admx.Any(x => x.HiveMatches) && !a.Action.Delete))
            {
                var a = am.Action;
                bool ok = allowed.Any(v => TryNumber(a.Value, out var n) && ulong.TryParse(v.Value, out var e) && n == e);
                if (!ok && !LooksLikeRange(allowed))
                    add(FindingSeverity.Warning, CspValue,
                        $@"{a.Path}\{a.Name}={Show(a.Value)} não está entre os valores documentados de {c.Reference} ({string.Join(", ", allowed.Select(v => v.Value))}).");
            }
        }
    }

    private static bool LooksLikeRange(List<CspAllowedValue> values) => values.Any(v => !ulong.TryParse(v.Value, out _));

    // ---------------- confidence/sources declarados ----------------

    private static void CheckDeclared(Tweak tweak, TweakMatch match, List<ReferenceFinding> local,
        IReadOnlyCollection<AdmxPolicy>? admx, IReadOnlyCollection<CspPolicy>? csp, Action<FindingSeverity, string, string> add)
    {
        if (tweak.Confidence is { } declared)
        {
            var suggested = ReferenceMatcher.SuggestConfidence(match, local);
            bool missingRefs = (declared == Confidence.Official && csp is null) || (declared == Confidence.Admx && admx is null);
            if (declared != suggested && !missingRefs && !(declared == Confidence.Questionable))
                add(FindingSeverity.Info, ConfidenceMismatch, $"confidence declarado '{Lower(declared)}', referências sugerem '{Lower(suggested)}'.");
        }

        foreach (var s in tweak.Sources)
        {
            if (s.Kind == SourceRef.MsDocs && csp is not null && !csp.Any(c => c.Reference.Equals(s.Ref, StringComparison.OrdinalIgnoreCase)))
                add(FindingSeverity.Warning, SourceUnknown, $"fonte msdocs '{s.Ref}' não existe mais na referência da Policy CSP.");
            if (s.Kind == SourceRef.AdmxKind && admx is not null && !admx.Any(p => p.QualifiedName.Equals(s.Ref, StringComparison.OrdinalIgnoreCase)))
                add(FindingSeverity.Info, SourceUnknown, $"fonte admx '{s.Ref}' não encontrada nos ADMX carregados.");
        }
    }

    public static string Lower(Confidence c) => c.ToString().ToLowerInvariant();
}
