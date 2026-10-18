using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Debloater.GuiAgent;

/// <summary>
/// Command loop: every *.json file in &lt;dir&gt;\inbox is one command ({"op": ...}); the result goes to
/// &lt;dir&gt;\outbox\&lt;same name&gt; ({"ok", "output", "error"}). Commands run in order of file name.
/// The host writes commands through PowerShell Direct and polls the outbox.
/// </summary>
internal static class Program
{
    private static readonly UIA3Automation Automation = new();
    private static string _log = "";

    [STAThread]
    private static int Main(string[] args)
    {
        var dir = args.Length > 0 ? args[0] : @"C:\GuiTest";
        var inbox = Directory.CreateDirectory(Path.Combine(dir, "inbox")).FullName;
        var outbox = Directory.CreateDirectory(Path.Combine(dir, "outbox")).FullName;
        _log = Path.Combine(dir, "agent.log");
        Log($"agent started, pid {Environment.ProcessId}, elevated={IsElevated()}, session {Process.GetCurrentProcess().SessionId}");
        File.WriteAllText(Path.Combine(dir, "agent.ready"), Environment.ProcessId.ToString());

        while (true)
        {
            string[] files;
            try { files = Directory.GetFiles(inbox, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray(); }
            catch (IOException) { files = []; }
            foreach (var file in files)
            {
                JsonObject cmd;
                try { cmd = JsonNode.Parse(File.ReadAllText(file))!.AsObject(); }
                catch (Exception ex) when (ex is IOException or JsonException) { Thread.Sleep(100); continue; }
                File.Delete(file);
                var result = new JsonObject();
                var op = (string?)cmd["op"] ?? "";
                Log($"> {cmd.ToJsonString()}");
                try
                {
                    if (op == "exit")
                    {
                        Write(outbox, file, new JsonObject { ["ok"] = true, ["output"] = "bye" });
                        return 0;
                    }
                    result["output"] = Run(op, cmd);
                    result["ok"] = true;
                }
                catch (Exception ex)
                {
                    result["ok"] = false;
                    result["error"] = ex.GetType().Name + ": " + ex.Message;
                }
                Log($"< {result.ToJsonString()}");
                Write(outbox, file, result);
            }
            Thread.Sleep(200);
        }
    }

    private static void Write(string outbox, string file, JsonObject result)
    {
        var target = Path.Combine(outbox, Path.GetFileName(file));
        File.WriteAllText(target + ".tmp", result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(target + ".tmp", target, true);
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch (IOException) { }
    }

    private static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string Run(string op, JsonObject c)
    {
        int timeout = (int?)c["timeout"] ?? 30;
        switch (op)
        {
            case "screenshot":
                return Screenshot((string)c["path"]!, (string?)c["window"]);
            case "windows":
                return string.Join(Environment.NewLine, Automation.GetDesktop().FindAllChildren()
                    .Select(w => $"{w.ControlType} '{w.Properties.Name.ValueOrDefault}' pid={w.Properties.ProcessId.ValueOrDefault} class={w.Properties.ClassName.ValueOrDefault}"));
            case "dump":
                {
                    var root = FindWindow(c, timeout);
                    var sb = new StringBuilder();
                    Dump(root, 0, (int?)c["depth"] ?? 40, sb);
                    return sb.ToString();
                }
            case "process":
                {
                    var w = FindWindow(c, timeout);
                    var p = Process.GetProcessById(w.Properties.ProcessId.Value);
                    return $"pid={p.Id} start={p.StartTime:HH:mm:ss}";
                }
            case "find":
                return Describe(FindElement(c, timeout));
            case "invoke":
                {
                    var e = FindElement(c, timeout, mustBeEnabled: (bool?)c["enabled"] ?? true);
                    if (e.Patterns.Invoke.IsSupported) e.Patterns.Invoke.Pattern.Invoke();
                    else if (e.Patterns.Toggle.IsSupported) e.Patterns.Toggle.Pattern.Toggle();
                    else if (e.Patterns.SelectionItem.IsSupported) e.Patterns.SelectionItem.Pattern.Select();
                    else if (e.Patterns.ExpandCollapse.IsSupported) e.Patterns.ExpandCollapse.Pattern.Expand();
                    else e.Click();
                    return Describe(e);
                }
            case "click":
                {
                    var e = FindElement(c, timeout);
                    e.Click();
                    return Describe(e);
                }
            case "rightclick":
                {
                    var e = FindElement(c, timeout);
                    e.RightClick();
                    return Describe(e);
                }
            case "toggle":
                {
                    var e = FindElement(c, timeout, mustBeEnabled: true);
                    bool want = (bool?)c["value"] ?? true;
                    var t = e.Patterns.Toggle.Pattern;
                    for (int i = 0; i < 3 && (t.ToggleState.Value == ToggleState.On) != want; i++) t.Toggle();
                    return Describe(e);
                }
            case "select":
                {
                    var e = FindElement(c, timeout);
                    if (e.Patterns.SelectionItem.IsSupported) e.Patterns.SelectionItem.Pattern.Select();
                    else e.Click();
                    return Describe(e);
                }
            case "settext":
                {
                    var e = FindElement(c, timeout, mustBeEnabled: true);
                    var text = (string?)c["text"] ?? "";
                    if (e.Patterns.Value.IsSupported && !((bool?)c["type"] ?? false)) e.Patterns.Value.Pattern.SetValue(text);
                    else
                    {
                        e.Focus();
                        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                        Keyboard.Type(text);
                    }
                    return Describe(e);
                }
            case "keys":
                {
                    if ((string?)c["window"] is not null || (bool?)c["focus"] == true) FindWindow(c, timeout).Focus();
                    Keyboard.Type((string)c["text"]!);
                    return "typed";
                }
            case "key":
                {
                    var k = Enum.Parse<VirtualKeyShort>((string)c["key"]!, true);
                    Keyboard.Press(k);
                    Keyboard.Release(k);
                    return "pressed";
                }
            case "scrollinto":
                {
                    var e = FindElement(c, timeout);
                    if (e.Patterns.ScrollItem.IsSupported) e.Patterns.ScrollItem.Pattern.ScrollIntoView();
                    return Describe(e);
                }
            case "waitfor":
                {
                    // Waits until the element exists and (optionally) its text contains / does not contain a value.
                    var deadline = DateTime.UtcNow.AddSeconds(timeout);
                    var contains = (string?)c["contains"];
                    var notContains = (string?)c["notContains"];
                    bool? enabled = (bool?)c["isEnabled"];
                    string last = "(not found)";
                    while (DateTime.UtcNow < deadline)
                    {
                        var e = TryFind(c);
                        if (e is not null)
                        {
                            var text = Text(e);
                            last = Describe(e);
                            bool ok = (contains is null || text.Contains(contains, StringComparison.OrdinalIgnoreCase))
                                && (notContains is null || !text.Contains(notContains, StringComparison.OrdinalIgnoreCase))
                                && (enabled is null || e.Properties.IsEnabled.ValueOrDefault == enabled);
                            if (ok) return last;
                        }
                        Thread.Sleep(500);
                    }
                    throw new TimeoutException("waitfor timed out; last: " + last);
                }
            case "findall":
                {
                    // Every element matching id/name/type (e.g. the result rows of an apply session).
                    var root = FindWindow(c, timeout);
                    var cf = Automation.ConditionFactory;
                    FlaUI.Core.Conditions.ConditionBase cond = (string?)c["id"] is { } fid ? cf.ByAutomationId(fid) : cf.ByControlType(Enum.Parse<ControlType>((string)c["type"]!, true));
                    return string.Join(Environment.NewLine, root.FindAllDescendants(cond).Select(Text));
                }
            case "texts":
                {
                    // All text under an element (for result lists and dialogs).
                    var e = FindElement(c, timeout);
                    var sb = new StringBuilder();
                    foreach (var d in e.FindAllDescendants())
                    {
                        var t = Text(d);
                        if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine($"[{d.Properties.ControlType.ValueOrDefault}] {t}");
                    }
                    return sb.ToString();
                }
            case "launch":
                {
                    var psi = new ProcessStartInfo((string)c["exe"]!, (string?)c["args"] ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName((string)c["exe"]!) };
                    var p = Process.Start(psi)!;
                    return $"pid={p.Id}";
                }
            case "maximize":
                {
                    var w = FindWindow(c, timeout);
                    w.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
                    return "maximized";
                }
            case "resolution":
                return Display.SetResolution((int)c["width"]!, (int)c["height"]!);
            case "sleep":
                Thread.Sleep(((int?)c["ms"]) ?? 1000);
                return "slept";
            case "closeclass":
                {
                    // Closes every top-level window of a class (e.g. CabinetWClass = File Explorer windows).
                    var cls = (string)c["class"]!;
                    int n = 0;
                    foreach (var w in Automation.GetDesktop().FindAllChildren().Where(x => x.Properties.ClassName.ValueOrDefault == cls))
                    {
                        w.AsWindow().Close();
                        n++;
                    }
                    return $"closed {n}";
                }
            case "close":
                {
                    var w = FindWindow(c, timeout).AsWindow();
                    w.Close();
                    return "closed";
                }
            default:
                throw new ArgumentException("unknown op " + op);
        }
    }

    private static string Screenshot(string path, string? window)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Rectangle bounds;
        if (window is not null)
        {
            var w = FindWindow(new JsonObject { ["window"] = window }, 10);
            var r = w.BoundingRectangle;
            bounds = new Rectangle(r.X, r.Y, r.Width, r.Height);
        }
        else bounds = Screen.PrimaryScreen!.Bounds;
        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bmp.Save(path, ImageFormat.Png);
        return $"{path} {bounds.Width}x{bounds.Height}";
    }

    private static AutomationElement FindWindow(JsonObject c, int timeout)
    {
        var title = (string?)c["window"] ?? "Windows Debloater";
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (true)
        {
            var w = Automation.GetDesktop().FindAllChildren()
                .Where(x => (x.Properties.Name.ValueOrDefault ?? "") == title || ((bool?)c["windowContains"] == true && (x.Properties.Name.ValueOrDefault ?? "").Contains(title, StringComparison.OrdinalIgnoreCase)))
                .Select(x => (x, start: StartTime(x)))
                .OrderByDescending(t => t.start)
                .Select(t => t.x)
                .FirstOrDefault();
            if (w is not null) return w;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"window '{title}' not found");
            Thread.Sleep(500);
        }
    }

    private static DateTime StartTime(AutomationElement e)
    {
        try { return Process.GetProcessById(e.Properties.ProcessId.Value).StartTime; }
        catch (Exception) { return DateTime.MinValue; }
    }

    private static AutomationElement? TryFind(JsonObject c)
    {
        // A busy UI thread (cold start of the app on the slow VM) makes UIA calls time out: treat as "not yet".
        try { return TryFindCore(c); }
        catch (System.Runtime.InteropServices.COMException) { return null; }
        catch (TimeoutException) { return null; }
    }

    private static AutomationElement? TryFindCore(JsonObject c)
    {
        AutomationElement root;
        try { root = FindWindow(c, 0); }
        catch (TimeoutException) { return null; }
        // "scope": {id/name/type...} searches only under that element (e.g. the items of one combo box).
        if (c["scope"] is JsonObject scope)
        {
            scope["window"] ??= (string?)c["window"];
            var s = TryFind(scope);
            if (s is null) return null;
            root = s;
        }
        var cf = Automation.ConditionFactory;
        var id = (string?)c["id"];
        var name = (string?)c["name"];
        var type = (string?)c["type"];
        FlaUI.Core.Conditions.ConditionBase? cond = null;
        if (id is not null) cond = cf.ByAutomationId(id);
        if (name is not null)
        {
            var n = (bool?)c["nameContains"] == true ? null : cf.ByName(name);
            if (n is not null) cond = cond is null ? n : cond.And(n);
        }
        if (type is not null)
        {
            var t = cf.ByControlType(Enum.Parse<ControlType>(type, true));
            cond = cond is null ? t : cond.And(t);
        }
        if (cond is null) return root;
        var all = root.FindAllDescendants(cond);
        if (name is not null && (bool?)c["nameContains"] == true)
            all = root.FindAllDescendants(cond).Where(e => (e.Properties.Name.ValueOrDefault ?? "").Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (name is not null && (bool?)c["nameContains"] == true && cond is null)
            all = root.FindAllDescendants().Where(e => (e.Properties.Name.ValueOrDefault ?? "").Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        int index = (int?)c["index"] ?? 0;
        return all.Length > index ? all[index] : null;
    }

    private static AutomationElement FindElement(JsonObject c, int timeout, bool mustBeEnabled = false)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (true)
        {
            var e = TryFind(c);
            if (e is not null && (!mustBeEnabled || e.Properties.IsEnabled.ValueOrDefault)) return e;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(e is null ? $"element not found: {c.ToJsonString()}" : $"element disabled: {Describe(e)}");
            Thread.Sleep(500);
        }
    }

    private static string Text(AutomationElement e)
    {
        var name = e.Properties.Name.ValueOrDefault ?? "";
        string value = "";
        try { if (e.Patterns.Value.IsSupported) value = e.Patterns.Value.Pattern.Value.ValueOrDefault ?? ""; } catch (Exception) { }
        if (value.Length == 0)
        {
            try { if (e.Patterns.Text.IsSupported) value = e.Patterns.Text.Pattern.DocumentRange.GetText(-1) ?? ""; } catch (Exception) { }
        }
        return value.Length > 0 && value != name ? (name.Length > 0 ? name + " = " + value : value) : name;
    }

    private static string Describe(AutomationElement e)
    {
        var sb = new StringBuilder();
        sb.Append(e.Properties.ControlType.ValueOrDefault);
        var id = e.Properties.AutomationId.ValueOrDefault;
        if (!string.IsNullOrEmpty(id)) sb.Append($" #{id}");
        var text = Text(e);
        if (text.Length > 0) sb.Append($" '{(text.Length > 400 ? text[..400] + "..." : text)}'");
        if (!e.Properties.IsEnabled.ValueOrDefault) sb.Append(" [disabled]");
        try { if (e.Patterns.Toggle.IsSupported) sb.Append($" toggle={e.Patterns.Toggle.Pattern.ToggleState.Value}"); } catch (Exception) { }
        try { if (e.Patterns.SelectionItem.IsSupported && e.Patterns.SelectionItem.Pattern.IsSelected.Value) sb.Append(" selected"); } catch (Exception) { }
        try { if (e.Patterns.ExpandCollapse.IsSupported) sb.Append($" expand={e.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value}"); } catch (Exception) { }
        if (e.Properties.IsOffscreen.ValueOrDefault) sb.Append(" offscreen");
        return sb.ToString();
    }

    private static void Dump(AutomationElement e, int depth, int max, StringBuilder sb)
    {
        sb.Append(' ', depth * 2).AppendLine(Describe(e));
        if (depth >= max) return;
        foreach (var child in e.FindAllChildren()) Dump(child, depth + 1, max, sb);
    }
}
