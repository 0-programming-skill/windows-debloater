using Debloater.Core.Admx;
using Debloater.Core.Model;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Debloater.App.Helpers;

/// <summary>Textos (pt-BR) e cores dos selos de estado, risco e confiança.</summary>
public static class Labels
{
    public static string State(TweakState s) => s switch
    {
        TweakState.Applied => "Aplicado",
        TweakState.NotApplied => "Não aplicado",
        TweakState.Partial => "Parcial",
        TweakState.NotApplicable => "Não se aplica",
        TweakState.Error => "Erro",
        _ => s.ToString(),
    };

    public static string ActionState(ActionState s) => s switch
    {
        Core.Model.ActionState.Desired => "No estado desejado",
        Core.Model.ActionState.NotDesired => "Diferente do desejado",
        Core.Model.ActionState.NotApplicable => "Não se aplica",
        Core.Model.ActionState.Error => "Erro",
        _ => s.ToString(),
    };

    public static string Risk(RiskLevel r) => r switch
    {
        RiskLevel.Low => "Risco baixo",
        RiskLevel.Medium => "Risco médio",
        RiskLevel.High => "Risco alto",
        RiskLevel.Critical => "Risco crítico",
        _ => r.ToString(),
    };

    public static string RiskShort(RiskLevel r) => r switch
    {
        RiskLevel.Low => "Baixo",
        RiskLevel.Medium => "Médio",
        RiskLevel.High => "Alto",
        RiskLevel.Critical => "Crítico",
        _ => r.ToString(),
    };

    public static string Confidence(Confidence? c) => c switch
    {
        Core.Model.Confidence.Official => "Oficial",
        Core.Model.Confidence.Admx => "ADMX",
        Core.Model.Confidence.Questionable => "Duvidoso",
        _ => "Comunidade",
    };

    public static string ConfidenceTip(Confidence? c) => c switch
    {
        Core.Model.Confidence.Official => "Documentado pela Microsoft (Policy CSP no Microsoft Learn).",
        Core.Model.Confidence.Admx => "Corresponde a uma política de um modelo administrativo (ADMX) oficial.",
        Core.Model.Confidence.Questionable => "Há indícios de que não funciona como descrito (valor fora do previsto, só Insider, edição...).",
        _ => "Conhecido pela comunidade e por ferramentas consolidadas, sem documentação oficial.",
    };

    public static string AdmxState(AdmxPolicyState s) => s switch
    {
        AdmxPolicyState.NotConfigured => "Não configurada",
        AdmxPolicyState.Enabled => "Habilitada",
        AdmxPolicyState.Disabled => "Desabilitada",
        AdmxPolicyState.Unknown => "Valores próprios",
        _ => s.ToString(),
    };

    public static string AdmxClassText(AdmxClass c) => c switch
    {
        AdmxClass.Machine => "Computador (HKLM)",
        AdmxClass.User => "Usuário (HKCU)",
        AdmxClass.Both => "Computador e usuário",
        _ => c.ToString(),
    };

    public static string ActionType(string type) => type switch
    {
        ActionSpec.Registry => "Registro",
        ActionSpec.Service => "Serviço",
        ActionSpec.ScheduledTask => "Tarefa agendada",
        ActionSpec.Appx => "App (Appx)",
        ActionSpec.OptionalFeature => "Recurso opcional",
        ActionSpec.PowerShell => "PowerShell",
        _ => type,
    };

    /// <summary>O que a ação faz, em uma linha (ex.: "HKCU\...\X = 0 (dword)").</summary>
    public static string ActionDesired(ActionSpec a) => a.Type switch
    {
        ActionSpec.Registry when a.Delete => $"{a.Path}\\{Display(a.Name)}: apagar o valor",
        ActionSpec.Registry => $"{a.Path}\\{Display(a.Name)} = {FormatValue(a.Value)} ({a.Kind})",
        ActionSpec.Service => $"{a.Name}: inicialização {Startup(a.Startup)}",
        ActionSpec.ScheduledTask => $"{a.Path}: {(a.State == "disabled" ? "desativada" : "ativada")}",
        ActionSpec.Appx => $"{a.Name}: remover",
        ActionSpec.OptionalFeature => $"{a.Name}: {(a.State == "disabled" ? "desativado" : "ativado")}",
        ActionSpec.PowerShell => "Script PowerShell (detectar / aplicar / reverter)",
        _ => a.ToString(),
    };

    private static string Display(string? name) => string.IsNullOrEmpty(name) ? "(Padrão)" : name;

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        IEnumerable<object> list => string.Join(" | ", list),
        _ => value.ToString() ?? "",
    };

    private static string Startup(string? s) => s switch
    {
        "disabled" => "desativada",
        "manual" => "manual",
        "automatic" => "automática",
        "automaticDelayed" => "automática (atraso)",
        _ => s ?? "?",
    };

    // ---------------- cores ----------------

    private static readonly Dictionary<Color, SolidColorBrush> Cache = [];

    public static SolidColorBrush Brush(Color c)
    {
        if (!Cache.TryGetValue(c, out var b)) Cache[c] = b = new SolidColorBrush(c);
        return b;
    }

    private static Color Hex(uint rgb) => Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static Color RiskColor(RiskLevel r) => r switch
    {
        RiskLevel.Low => Hex(0x2E7D32),      // verde
        RiskLevel.Medium => Hex(0xF2C200),   // amarelo
        RiskLevel.High => Hex(0xEF6C00),     // laranja
        RiskLevel.Critical => Hex(0xC62828), // vermelho
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

    /// <summary>Texto preto em fundos claros (amarelo, cinza claro), branco nos demais.</summary>
    public static SolidColorBrush ForegroundOn(Color background)
    {
        double luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
        return Brush(luminance > 0.6 ? Colors.Black : Colors.White);
    }
}
