using System.Text.RegularExpressions;
using Debloater.Core.Model;

namespace Debloater.Coverage;

/// <summary>Alvos do nosso catálogo, para casar com os das outras ferramentas.</summary>
public sealed class CatalogIndex
{
    private readonly Dictionary<(TargetKind, string), HashSet<string>> _exact = [];
    private readonly List<(string Prefix, string TweakId)> _appxPrefixes = [];
    private readonly List<(string Key, string TweakId)> _registryKeys = [];

    public CatalogIndex(IEnumerable<Tweak> tweaks)
    {
        foreach (var tweak in tweaks)
            foreach (var a in tweak.Actions)
                foreach (var t in TargetsOf(a))
                    Add(t, tweak.Id);
    }

    public static IEnumerable<Target> TargetsOf(ActionSpec a)
    {
        switch (a.Type)
        {
            case ActionSpec.Registry:
                if (Targets.Registry(a.Path, a.Name ?? "") is { } r) yield return r;
                break;
            case ActionSpec.Service:
                if (Targets.Service(a.Name) is { } s) yield return s;
                break;
            case ActionSpec.ScheduledTask:
                if (Targets.Task(a.Path) is { } k) yield return k;
                break;
            case ActionSpec.Appx:
                if (a.Name is not null && Targets.Appx(a.Name) is { } x)
                    yield return a.Name.EndsWith('*') ? x with { Key = x.Key + "*" } : x;
                break;
            case ActionSpec.OptionalFeature:
                if (Targets.Feature(a.Name) is { } f) yield return f;
                break;
            case "capability":
                if (Targets.Capability(a.Name) is { } c) yield return c;
                break;
            case ActionSpec.PowerShell:
                foreach (var script in new[] { a.Detect, a.Apply, a.Revert })
                    if (script is not null)
                        foreach (var t in Extractors.FromScript(script)) yield return t;
                break;
        }
    }

    /// <summary>Índice com alvos avulsos (ex.: os de uma ferramenta), para reaproveitar as regras de casamento.</summary>
    public CatalogIndex With(IEnumerable<Target> targets)
    {
        foreach (var t in targets) Add(t, "*");
        return this;
    }

    private void Add(Target t, string id)
    {
        if (t.Kind == TargetKind.Appx && t.Key.EndsWith('*')) { _appxPrefixes.Add((t.Key[..^1], id)); return; }
        if (t.Kind == TargetKind.RegistryKey) _registryKeys.Add((t.Key, id));
        if (!_exact.TryGetValue((t.Kind, t.Key), out var set)) _exact[(t.Kind, t.Key)] = set = new(StringComparer.Ordinal);
        set.Add(id);
    }

    /// <summary>Tweaks que cobrem o alvo (vazio = não coberto).</summary>
    public IReadOnlyCollection<string> CoveredBy(Target t)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (_exact.TryGetValue((t.Kind, t.Key), out var set)) ids.UnionWith(set);
        switch (t.Kind)
        {
            case TargetKind.Appx:
                foreach (var (p, id) in _appxPrefixes) if (t.Key.StartsWith(p, StringComparison.Ordinal)) ids.Add(id);
                break;
            case TargetKind.Registry:
                // A mesma política em HKCU e HKLM (Software\Policies) conta como coberta.
                var other = SwapPolicyHive(t.Key);
                if (other is not null && _exact.TryGetValue((TargetKind.Registry, other), out var o)) ids.UnionWith(o);
                // Apagar a chave inteira no catálogo cobre valores dentro dela.
                foreach (var (k, id) in _registryKeys) if (t.Key.StartsWith(k + "\\", StringComparison.Ordinal)) ids.Add(id);
                break;
            case TargetKind.RegistryKey:
                // Chave inteira: coberta se o catálogo mexe em algum valor dentro dela.
                foreach (var ((kind, key), s) in _exact)
                    if (kind == TargetKind.Registry && key.StartsWith(t.Key + "\\", StringComparison.Ordinal)) ids.UnionWith(s);
                break;
        }
        return ids;
    }

    private static string? SwapPolicyHive(string key)
    {
        if (key.StartsWith(@"HKCU\SOFTWARE\POLICIES\", StringComparison.Ordinal)) return "HKLM" + key[4..];
        if (key.StartsWith(@"HKLM\SOFTWARE\POLICIES\", StringComparison.Ordinal)) return "HKCU" + key[4..];
        return null;
    }
}

/// <summary>Categoria (pt-BR) de um alvo, para agrupar a lista do que falta. Heurística por palavras-chave.</summary>
public static class Categorizer
{
    private static readonly (string Category, string Pattern)[] s_rules =
    [
        ("Microsoft Edge", @"\\EDGE(UPDATE)?\b|MICROSOFTEDGE|\bEDGEUPDATE|MSEDGE|EDGE_|\\EDGE\\"),
        ("OneDrive", @"ONEDRIVE|ONESYNC|018D5C66-4533-4307-9B53-224DE2ED1FE6"),
        ("Defender e segurança", @"WINDOWS DEFENDER|WINDEFEND|\bSENSE\b|SMARTSCREEN|SPYNET|MPENGINE|WDNIS|WDFILTER|WDBOOT|SECURITYHEALTH|SGRMBROKER|\\MRT\b|WTDS|WEBTHREATDEFSVC|MPSSVC|\\LSA\b|SCHANNEL|CIPHERS|\\SYSTEM\\ENABLELUA|CONSENTPROMPT|WINDOWS SCRIPT HOST|AMSI|DEVICEGUARD|HYPERVISORENFORCEDCODE|VIRTUALIZATIONBASED|BITLOCKER|FVE\b|EXPLOIT"),
        ("IA e Copilot", @"COPILOT|WINDOWSAI|RECALL|CLICKTODO|\\AI\b|AIX|PAINT\\|NOTEPAD|AISERVICE|AGENT"),
        ("Windows Update e entrega", @"WINDOWSUPDATE|DELIVERYOPTIMIZATION|\bDOSVC\b|WUAUSERV|USOSVC|WAASMEDIC|UPDATEORCHESTRATOR|\\UPDATE\\|DEVICEMETADATA|DRIVERSEARCHING|STORE\\AUTODOWNLOAD|WINDOWSSTORE"),
        ("Telemetria e diagnóstico", @"DATACOLLECTION|DIAGTRACK|DMWAPPUSH|SIUF|APPCOMPAT|APPLICATION EXPERIENCE|CUSTOMER EXPERIENCE|\bCEIP|SQM|ERROR REPORTING|\bWER|FEEDBACK|AUTOLOGGER|DIAGNOSTIC|TELEMETRY|DIAGSVC|DPS\b|WDISYS|PCASVC|INVENTORY|COMPATTELRUNNER|\\WMI\\|ETW|TRACE|\\PERFTRACK"),
        ("Anúncios, sugestões e conteúdo", @"CONTENTDELIVERYMANAGER|CLOUDCONTENT|ADVERTISINGINFO|SUGGEST|SPOTLIGHT|SUBSCRIBEDCONTENT|USERPROFILEENGAGEMENT|SOFTLANDING|CONSUMERFEATURES|IRISSERVICE|PUSHNOTIFICATIONS|NOTIFICATIONS|TOAST"),
        ("Pesquisa e Cortana", @"\\SEARCH|CORTANA|\bBING|WSEARCH|SEARCHSETTINGS|SEARCHBOX|WINDOWS SEARCH"),
        ("Permissões de apps e privacidade", @"CAPABILITYACCESSMANAGER|CONSENTSTORE|APPPRIVACY|LETAPPS|LOCATION|SENSOR|SPEECH|INPUTPERSONALIZATION|\\TIPC|CLIPBOARD|ACTIVITY|TEXTINPUT|PRIVACY|LFSVC|\\MESSAGING|CONTACTS|CDP|SETTINGSYNC|\\SYNC|BIOMETRICS|HANDWRITING|INKING|PERSONALIZATION\\SETTINGS"),
        ("Jogos", @"GAMEDVR|GAMEBAR|GAMECONFIGSTORE|\bXBL|XBOX|GAMEMODE|\\GAMES?\b|GAMING|GAMEINPUT"),
        ("Rede", @"TCPIP|LANMAN|NETBT|DNSCLIENT|DNSCACHE|NETWORKCONNECTIVITY|NLASVC|WINHTTP|PSCHED|INTERNET SETTINGS|NETWORK|WLAN|WCMSVC|\bSMB|LLMNR|TEREDO|IPV6|\\NCSI|REMOTE ASSISTANCE|TERMINAL SERVER|REMOTEREGISTRY|WINRM|NETTCP|IPHLPSVC|SHAREDACCESS|BLUETOOTH|BTH"),
        ("Explorer, barra de tarefas e Iniciar", @"EXPLORER|TASKBAR|\\START|SHELL|CLSID|NAMESPACE|DESKTOP\\|CONTEXTMENU|\\ADVANCED|FOLDERDESCRIPTIONS|STARTMENU|WIDGETS|\\DSH|NEWSANDINTERESTS|FEEDS|CHAT|TASKVIEW|PEN WORKSPACE|THEMES|DWM|PERSONALIZE|LOCK ?SCREEN|LOGON|ACCENT|COLORS"),
        ("Desempenho, energia e efeitos visuais", @"POWER|PRIORITYCONTROL|MULTIMEDIA\\SYSTEMPROFILE|MMCSS|MEMORY MANAGEMENT|SYSMAIN|PREFETCH|VISUALEFFECTS|CONTROL PANEL\\DESKTOP|CONTROL PANEL\\MOUSE|CONTROL PANEL\\KEYBOARD|ACCESSIBILITY|STICKYKEYS|SESSION MANAGER|GRAPHICSDRIVERS|FTH|STORAGESENSE|STORAGEPOLICY|RESERVEMANAGER|MAINTENANCE|HIBERNATE|SLEEPSTUDY|DISK|NTFS|FILESYSTEM|CRASHCONTROL|PAGEFILE|BACKGROUNDACCESSAPPLICATIONS"),
    ];

    private static readonly List<(string Category, Regex Regex)> s_compiled =
        s_rules.Select(r => (r.Category, new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled))).ToList();

    public static string Of(Target t)
    {
        if (t.Kind == TargetKind.Appx)
        {
            if (Regex.IsMatch(t.Key, "EDGE")) return "Microsoft Edge";
            if (Regex.IsMatch(t.Key, "COPILOT|AI\\.|WINDOWSAI")) return "IA e Copilot";
            if (Regex.IsMatch(t.Key, "XBOX|GAMING")) return "Jogos";
            return "Apps (appx)";
        }
        if (t.Kind is TargetKind.Feature) return "Recursos opcionais e capacidades";
        if (t.Kind is TargetKind.Capability) return "Recursos opcionais e capacidades";
        var key = t.Display.ToUpperInvariant();
        foreach (var (cat, re) in s_compiled) if (re.IsMatch(key)) return cat;
        return t.Kind switch
        {
            TargetKind.Service => "Serviços (outros)",
            TargetKind.Task => "Tarefas agendadas (outras)",
            _ => "Outros (registro)",
        };
    }

    public static IReadOnlyList<string> Order { get; } =
    [
        "Microsoft Edge", "OneDrive", "Defender e segurança", "IA e Copilot", "Windows Update e entrega", "Telemetria e diagnóstico",
        "Anúncios, sugestões e conteúdo", "Pesquisa e Cortana", "Permissões de apps e privacidade", "Jogos", "Rede",
        "Explorer, barra de tarefas e Iniciar", "Desempenho, energia e efeitos visuais", "Apps (appx)", "Recursos opcionais e capacidades",
        "Serviços (outros)", "Tarefas agendadas (outras)", "Outros (registro)",
    ];
}
