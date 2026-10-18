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

/// <summary>A 'validate --reference' warning. Never an error: the catalog stays valid.</summary>
public sealed record ReferenceFinding(FindingSeverity Severity, string Code, string File, string TweakId, string Message)
{
    /// <summary>Indicates that the tweak probably does not work as described.</summary>
    public bool MakesQuestionable => Severity == FindingSeverity.Warning && Code is ReferenceValidator.AdmxValue or ReferenceValidator.CspInsider;

    /// <summary>Stable line (no line numbers) used as the CI baseline.</summary>
    public string BaselineKey => $"{(Severity == FindingSeverity.Warning ? "WARN" : "INFO")} {Code} {TweakId}: {Message}";

    public override string ToString() => $"{(Severity == FindingSeverity.Warning ? "WARN" : "info")} {File} [{TweakId}] {Code}: {Message}";
}

/// <summary>
/// Checks the catalog against the official references:
/// - ADMX: the written value/type is one of the states the policy defines; HKLM/HKCU match the class;
/// - Policy CSP: edition restrictions (e.g. AllowTelemetry=0 only applies on Enterprise/Education),
///   applicable OS (minBuild/maxBuild), Insider-only or deprecated policies;
/// - confidence declared in the YAML vs. found.
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
                add(FindingSeverity.Info, PolicyUnknown, $"{target}: policy key without a matching ADMX/CSP policy in the loaded references.");
            return;
        }

        var hiveOk = am.Admx.Where(m => m.HiveMatches).ToList();
        if (am.Admx.Count > 0 && hiveOk.Count == 0)
        {
            var p = am.Admx[0].Policy;
            add(FindingSeverity.Warning, AdmxHive,
                $"{target}: policy {p.QualifiedName} is of class {p.Class} ({(p.Class == AdmxClass.Machine ? "HKLM" : "HKCU")}); Windows does not read this value in the other hive.");
            return;
        }
        if (a.Delete || hiveOk.Count == 0) return; // delete = "not configured": always valid

        var verdicts = hiveOk.Select(m => Allowed(m, a)).ToList();
        if (verdicts.Any(v => v.Ok)) return;

        var policies = string.Join(", ", hiveOk.Select(m => m.Policy.QualifiedName).Distinct());
        if (verdicts.Any(v => v.DisabledByDelete))
        {
            add(FindingSeverity.Info, AdmxValueDelete,
                $"{target}={Show(a.Value)}: {policies} disables by deleting the value; {Show(a.Value)} is not a state defined by the ADMX (usually works as 'disabled').");
            return;
        }
        var kindProblem = verdicts.FirstOrDefault(v => v.KindMismatch is not null)?.KindMismatch;
        if (kindProblem is not null)
        {
            add(FindingSeverity.Warning, AdmxKind, $"{target}: {kindProblem} ({policies}).");
            return;
        }
        var expected = string.Join("; ", verdicts.Select(v => v.Expected).Where(e => e.Length > 0).Distinct());
        add(FindingSeverity.Warning, AdmxValue, $"{target}={Show(a.Value)} is not a value defined by {policies} (defined: {expected}).");
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
                return new Verdict(false, string.Join(" | ", candidates.Select(c => c.ToString())) + (byDelete ? " | (delete)" : ""),
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
                    return new Verdict(false, "", KindMismatch: $"type '{a.Kind}', the ADMX writes {expectKind}");
                if (!TryNumber(a.Value, out var n)) return new Verdict(false, $"number between {e.MinValue} and {e.MaxValue}");
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
                    : new Verdict(false, "", KindMismatch: $"type '{a.Kind}', the ADMX writes text");
            case "multiText":
                return KindIs(a, "multiString") ? new Verdict(true, "") : new Verdict(false, "", KindMismatch: $"type '{a.Kind}', the ADMX writes multiString");
            default:
                return new Verdict(true, "");
        }
    }

    private static string? KindIssue(AdmxValueKind expected, ActionSpec a) => expected switch
    {
        AdmxValueKind.Decimal when !KindIs(a, "dword") => $"type '{a.Kind}', the ADMX writes dword",
        AdmxValueKind.LongDecimal when !KindIs(a, "qword") => $"type '{a.Kind}', the ADMX writes qword",
        AdmxValueKind.String when !KindIs(a, "string") && !KindIs(a, "expandString") => $"type '{a.Kind}', the ADMX writes string",
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
            var editions = c.Editions.Count == 0 ? "no desktop edition" : string.Join(", ", c.Editions);
            add(tweakMentionsEdition ? FindingSeverity.Info : FindingSeverity.Warning, CspEdition,
                $"{c.Reference}: the documentation lists only {editions} (not Pro/Home); the effect on Home/Pro is not guaranteed.");
        }

        // Edition notes tied to a value ("[0] ...") or to the whole policy.
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
                        $"{c.Reference}={value}: {note[m.Length..]}{(tweakMentionsEdition ? " (the tweak already mentions the restriction)" : "")}");
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
                add(FindingSeverity.Info, CspInsider, $"{c.Reference}: the documentation still says 'Windows Insider only', but the loaded ADMX already defines the policy.");
            else
                add(FindingSeverity.Warning, CspInsider, $"{c.Reference}: documented only for Windows Insider builds.");
        }
        if (c.Deprecated)
            add(FindingSeverity.Warning, CspDeprecated, $"{c.Reference}: the documentation marks the policy as deprecated.");

        if (c.Win11MinBuild is { } min)
        {
            int tweakMin = tweak.MinBuild ?? Win11FirstBuild;
            if (tweak.MaxBuild is { } max && max < min)
                add(FindingSeverity.Warning, CspBuild, $"{c.Reference}: exists from build {min} on, but the tweak has maxBuild {max}.");
            else if (tweakMin < min)
                add(FindingSeverity.Warning, CspBuild,
                    $"{c.Reference}: applicable from build {min}{(c.Win11MinUbr is { } ubr ? $".{ubr}" : "")} on; the tweak does not set minBuild ≥ {min} (current: {tweak.MinBuild?.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
            else if (tweakMin == min && c.Win11MinUbr is { } ubr2)
                add(FindingSeverity.Info, CspBuild, $"{c.Reference}: requires build {min}.{ubr2} (with a cumulative update); minBuild cannot express the UBR.");
        }

        // CSP values: only when no ADMX is validating and the CSP writes the same value to the registry.
        if (c.AllowedValues is { Count: > 0 } allowed && !c.AdmxBacked && c.Format == "int")
        {
            foreach (var am in match.Actions.Where(a => a.Csp.Contains(c) && !a.Admx.Any(x => x.HiveMatches) && !a.Action.Delete))
            {
                var a = am.Action;
                bool ok = allowed.Any(v => TryNumber(a.Value, out var n) && ulong.TryParse(v.Value, out var e) && n == e);
                if (!ok && !LooksLikeRange(allowed))
                    add(FindingSeverity.Warning, CspValue,
                        $@"{a.Path}\{a.Name}={Show(a.Value)} is not among the documented values of {c.Reference} ({string.Join(", ", allowed.Select(v => v.Value))}).");
            }
        }
    }

    private static bool LooksLikeRange(List<CspAllowedValue> values) => values.Any(v => !ulong.TryParse(v.Value, out _));

    // ---------------- declared confidence/sources ----------------

    private static void CheckDeclared(Tweak tweak, TweakMatch match, List<ReferenceFinding> local,
        IReadOnlyCollection<AdmxPolicy>? admx, IReadOnlyCollection<CspPolicy>? csp, Action<FindingSeverity, string, string> add)
    {
        if (tweak.Confidence is { } declared)
        {
            var suggested = ReferenceMatcher.SuggestConfidence(match, local);
            bool missingRefs = (declared == Confidence.Official && csp is null) || (declared == Confidence.Admx && admx is null);
            if (declared != suggested && !missingRefs && !(declared == Confidence.Questionable))
                add(FindingSeverity.Info, ConfidenceMismatch, $"declared confidence '{Lower(declared)}', references suggest '{Lower(suggested)}'.");
        }

        foreach (var s in tweak.Sources)
        {
            if (s.Kind == SourceRef.MsDocs && csp is not null && !csp.Any(c => c.Reference.Equals(s.Ref, StringComparison.OrdinalIgnoreCase)))
                add(FindingSeverity.Warning, SourceUnknown, $"msdocs source '{s.Ref}' no longer exists in the Policy CSP reference.");
            if (s.Kind == SourceRef.AdmxKind && admx is not null && !admx.Any(p => p.QualifiedName.Equals(s.Ref, StringComparison.OrdinalIgnoreCase)))
                add(FindingSeverity.Info, SourceUnknown, $"admx source '{s.Ref}' not found in the loaded ADMX.");
        }
    }

    public static string Lower(Confidence c) => c.ToString().ToLowerInvariant();
}
