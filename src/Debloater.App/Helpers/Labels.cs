using Debloater.Core.Admx;
using Debloater.Core.Model;
using Debloater.Core.Policies;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Debloater.App.Helpers;

/// <summary>Texts (from resources) and colors of the state, risk and confidence badges.</summary>
public static class Labels
{
    public static string State(TweakState s) => s switch
    {
        TweakState.Applied => Loc.Get("State_Applied"),
        TweakState.NotApplied => Loc.Get("State_NotApplied"),
        TweakState.Partial => Loc.Get("State_Partial"),
        TweakState.NotApplicable => Loc.Get("State_NotApplicable"),
        TweakState.Error => Loc.Get("State_Error"),
        _ => s.ToString(),
    };

    public static string ActionState(ActionState s) => s switch
    {
        Core.Model.ActionState.Desired => Loc.Get("ActionState_Desired"),
        Core.Model.ActionState.NotDesired => Loc.Get("ActionState_NotDesired"),
        Core.Model.ActionState.NotApplicable => Loc.Get("State_NotApplicable"),
        Core.Model.ActionState.Error => Loc.Get("State_Error"),
        _ => s.ToString(),
    };

    public static string Risk(RiskLevel r) => r switch
    {
        RiskLevel.Low => Loc.Get("Risk_Low"),
        RiskLevel.Medium => Loc.Get("Risk_Medium"),
        RiskLevel.High => Loc.Get("Risk_High"),
        RiskLevel.Critical => Loc.Get("Risk_Critical"),
        _ => r.ToString(),
    };

    public static string RiskShort(RiskLevel r) => r switch
    {
        RiskLevel.Low => Loc.Get("RiskShort_Low"),
        RiskLevel.Medium => Loc.Get("RiskShort_Medium"),
        RiskLevel.High => Loc.Get("RiskShort_High"),
        RiskLevel.Critical => Loc.Get("RiskShort_Critical"),
        _ => r.ToString(),
    };

    public static string Confidence(Confidence? c) => c switch
    {
        Core.Model.Confidence.Official => Loc.Get("Confidence_Official"),
        Core.Model.Confidence.Admx => Loc.Get("Confidence_Admx"),
        Core.Model.Confidence.Questionable => Loc.Get("Confidence_Questionable"),
        _ => Loc.Get("Confidence_Community"),
    };

    public static string ConfidenceTip(Confidence? c) => c switch
    {
        Core.Model.Confidence.Official => Loc.Get("ConfidenceTip_Official"),
        Core.Model.Confidence.Admx => Loc.Get("ConfidenceTip_Admx"),
        Core.Model.Confidence.Questionable => Loc.Get("ConfidenceTip_Questionable"),
        _ => Loc.Get("ConfidenceTip_Community"),
    };

    public static string AdmxState(AdmxPolicyState s) => s switch
    {
        AdmxPolicyState.NotConfigured => Loc.Get("PolicyState_NotConfigured"),
        AdmxPolicyState.Enabled => Loc.Get("PolicyState_Enabled"),
        AdmxPolicyState.Disabled => Loc.Get("PolicyState_Disabled"),
        AdmxPolicyState.Unknown => Loc.Get("PolicyState_Custom"),
        _ => s.ToString(),
    };

    /// <summary>Target state of a policy change (Not configured / Enabled / Disabled).</summary>
    public static string PolicyTargetState(PolicyTargetState s) => s switch
    {
        Core.Policies.PolicyTargetState.Enabled => Loc.Get("PolicyState_Enabled"),
        Core.Policies.PolicyTargetState.Disabled => Loc.Get("PolicyState_Disabled"),
        _ => Loc.Get("PolicyState_NotConfigured"),
    };

    public static string AdmxClassText(AdmxClass c) => c switch
    {
        AdmxClass.Machine => Loc.Get("PolicyClass_Machine"),
        AdmxClass.User => Loc.Get("PolicyClass_User"),
        AdmxClass.Both => Loc.Get("PolicyClass_Both"),
        _ => c.ToString(),
    };

    public static string ActionType(string type) => type switch
    {
        ActionSpec.Registry => Loc.Get("ActionType_Registry"),
        ActionSpec.Service => Loc.Get("ActionType_Service"),
        ActionSpec.ScheduledTask => Loc.Get("ActionType_ScheduledTask"),
        ActionSpec.Appx => Loc.Get("ActionType_Appx"),
        ActionSpec.OptionalFeature => Loc.Get("ActionType_OptionalFeature"),
        ActionSpec.PowerShell => Loc.Get("ActionType_PowerShell"),
        ActionSpec.FeatureFlag => Loc.Get("ActionType_FeatureFlag"),
        _ => type,
    };

    /// <summary>What the action does, in one line (e.g. "HKCU\...\X = 0 (dword)").</summary>
    public static string ActionDesired(ActionSpec a) => a.Type switch
    {
        ActionSpec.Registry when a.Delete => Loc.Format("ActionDesired_RegistryDelete", $"{a.Path}\\{Display(a.Name)}"),
        ActionSpec.Registry => $"{a.Path}\\{Display(a.Name)} = {FormatValue(a.Value)} ({a.Kind})",
        ActionSpec.Service => Loc.Format("ActionDesired_Service", a.Name, Startup(a.Startup)),
        ActionSpec.ScheduledTask => $"{a.Path}: {(a.State == "disabled" ? Loc.Get("ActionDesired_TaskDisabled") : Loc.Get("ActionDesired_TaskEnabled"))}",
        ActionSpec.Appx => Loc.Format("ActionDesired_Appx", a.Name),
        ActionSpec.OptionalFeature => $"{a.Name}: {(a.State == "disabled" ? Loc.Get("ActionDesired_FeatureDisabled") : Loc.Get("ActionDesired_FeatureEnabled"))}",
        ActionSpec.PowerShell => Loc.Get("ActionDesired_PowerShell"),
        ActionSpec.FeatureFlag => Loc.Format("ActionDesired_FeatureFlag",
            Debloater.Core.Platform.FeatureFlags.KnownFeatures.Label(a.FeatureId ?? 0),
            a.State switch
            {
                "enabled" => Loc.Get("ActionDesired_FeatureFlagEnabled"),
                "disabled" => Loc.Get("ActionDesired_FeatureFlagDisabled"),
                _ => Loc.Get("ActionDesired_FeatureFlagDefault"),
            },
            string.IsNullOrWhiteSpace(a.Priority) ? "user" : a.Priority),
        _ => a.ToString(),
    };

    private static string Display(string? name) => string.IsNullOrEmpty(name) ? Loc.Get("ActionDesired_DefaultValue") : name;

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        IEnumerable<object> list => string.Join(" | ", list),
        _ => value.ToString() ?? "",
    };

    private static string Startup(string? s) => s switch
    {
        "disabled" => Loc.Get("Startup_Disabled"),
        "manual" => Loc.Get("Startup_Manual"),
        "automatic" => Loc.Get("Startup_Automatic"),
        "automaticDelayed" => Loc.Get("Startup_AutomaticDelayed"),
        _ => s ?? "?",
    };

    // ---------------- colors ----------------

    private static readonly Dictionary<Color, SolidColorBrush> Cache = [];

    public static SolidColorBrush Brush(Color c)
    {
        if (!Cache.TryGetValue(c, out var b)) Cache[c] = b = new SolidColorBrush(c);
        return b;
    }

    private static Color Hex(uint rgb) => Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static Color RiskColor(RiskLevel r) => r switch
    {
        RiskLevel.Low => Hex(0x2E7D32),      // green
        RiskLevel.Medium => Hex(0xF2C200),   // yellow
        RiskLevel.High => Hex(0xEF6C00),     // orange
        RiskLevel.Critical => Hex(0xC62828), // red
        _ => Colors.Gray,
    };

    public static Color StateColor(TweakState s) => s switch
    {
        TweakState.Applied => Hex(0x0F7B6C),
        TweakState.NotApplied => Hex(0x616161),
        TweakState.Partial => Hex(0x7E57C2),
        TweakState.NotApplicable => Hex(0xA0A0A0),
        TweakState.Error => Hex(0xB00020),
        _ => Colors.Gray,
    };

    public static Color ConfidenceColor(Confidence? c) => c switch
    {
        Core.Model.Confidence.Official => Hex(0x1565C0),
        Core.Model.Confidence.Admx => Hex(0x00796B),
        Core.Model.Confidence.Questionable => Hex(0xAD1457),
        _ => Hex(0x6D4C41),
    };

    public static Color AdmxStateColor(AdmxPolicyState s) => s switch
    {
        AdmxPolicyState.Enabled => Hex(0x0F7B6C),
        AdmxPolicyState.Disabled => Hex(0x5D4037),
        AdmxPolicyState.Unknown => Hex(0x7E57C2),
        _ => Hex(0x9E9E9E),
    };

    /// <summary>Black text on light backgrounds (yellow, light gray), white on the others.</summary>
    public static SolidColorBrush ForegroundOn(Color background)
    {
        double luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
        return Brush(luminance > 0.6 ? Colors.Black : Colors.White);
    }
}
