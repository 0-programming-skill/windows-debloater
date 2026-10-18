using System.Globalization;
using Debloater.Core.Admx;
using Debloater.Core.Policies;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Debloater.App.Views;

/// <summary>
/// Editores dos elementos de uma política, gerados a partir da apresentação do ADML (na ordem do
/// gpedit): NumberBox para decimal (com mínimo/máximo), CheckBox para boolean, ComboBox para enum,
/// TextBox (ou ComboBox editável com sugestões) para text, caixa de várias linhas para multiText e list.
/// </summary>
internal sealed class PolicyEditor
{
    private readonly List<(AdmxElement Element, Func<List<string>> Read, Control Control)> _editors = [];

    public PolicyEditor(AdmxPolicy policy, PolicySetting initial)
    {
        View = new StackPanel { Spacing = 10 };

        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in policy.Presentation ?? [])
        {
            if (control.RefId is null)
            {
                if (control.Text is { Length: > 0 } text)
                    View.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = Style("Caption") });
                continue;
            }
            if (policy.Elements.FirstOrDefault(e => e.Id == control.RefId) is { } el && placed.Add(el.Id))
                Add(el, initial);
        }
        // Elementos sem controle na apresentação (ADML incompleto) aparecem no fim, com o id como rótulo.
        foreach (var el in policy.Elements.Where(e => !placed.Contains(e.Id))) Add(el, initial);
    }

    /// <summary>Painel com os editores (vazio se a política não tem elementos).</summary>
    public StackPanel View { get; }

    public bool HasElements => _editors.Count > 0;

    /// <summary>Disparado a cada alteração de valor.</summary>
    public event Action? Changed;

    public bool IsEnabled
    {
        set
        {
            foreach (var (_, _, c) in _editors) c.IsEnabled = value;
            View.Opacity = value ? 1 : 0.55;
        }
    }

    /// <summary>Valores atuais dos editores (id → valores), no formato de <see cref="PolicySetting.Values"/>.</summary>
    public Dictionary<string, List<string>> Values()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (el, read, _) in _editors) result[el.Id] = read();
        return result;
    }

    private static Style Style(string key) => (Style)Application.Current.Resources[key];

    private void Raise() => Changed?.Invoke();

    private static List<string>? Initial(PolicySetting s, AdmxElement el) =>
        s.Values.FirstOrDefault(kv => kv.Key.Equals(el.Id, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? InitialScalar(PolicySetting s, AdmxElement el) =>
        Initial(s, el) is { } list ? list.FirstOrDefault() : el.DefaultValue;

    private void Add(AdmxElement el, PolicySetting initial)
    {
        var label = string.IsNullOrWhiteSpace(el.Label) ? el.Id : el.Label!.Trim();
        if (el.Required && el.Type is not "boolean") label += " *";
        Control control;
        Func<List<string>> read;
        string? hint = null;

        switch (el.Type)
        {
            case "decimal":
            case "longDecimal":
            {
                ulong min = el.MinValue ?? 0, max = el.MaxValue ?? (el.Type == "decimal" ? uint.MaxValue : ulong.MaxValue);
                var box = new NumberBox
                {
                    Header = label,
                    Minimum = min,
                    Maximum = max,
                    SmallChange = 1,
                    LargeChange = 10,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
                    NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 0, IsGrouped = false },
                    Value = ulong.TryParse(InitialScalar(initial, el), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : double.NaN,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MinWidth = 200,
                };
                box.ValueChanged += (_, _) => Raise();
                control = box;
                read = () => double.IsNaN(box.Value) || box.Value < 0
                    ? []
                    : [((ulong)Math.Round(box.Value)).ToString(CultureInfo.InvariantCulture)];
                hint = $"Entre {min} e {max}." + (el.StoreAsText ? " Gravado como texto (REG_SZ)." : "");
                break;
            }

            case "boolean":
            {
                var check = new CheckBox { Content = label };
                var text = InitialScalar(initial, el);
                check.IsChecked = text is not null && PolicyWriter.TryParseBool(text, out var on) && on;
                check.Checked += (_, _) => Raise();
                check.Unchecked += (_, _) => Raise();
                control = check;
                read = () => [check.IsChecked == true ? "true" : "false"];
                break;
            }

            case "enum":
            {
                var combo = new ComboBox { Header = label, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var item in el.Items ?? []) combo.Items.Add(item.DisplayName);
                var text = InitialScalar(initial, el);
                combo.SelectedIndex = text is not null && PolicyWriter.ResolveEnum(el, text) is { } idx ? idx : -1;
                combo.SelectionChanged += (_, _) => Raise();
                control = combo;
                read = () => combo.SelectedIndex >= 0 ? [PolicyWriter.EnumToken(el, combo.SelectedIndex)] : [];
                break;
            }

            case "text":
            {
                var text = InitialScalar(initial, el) ?? "";
                int maxLength = el.MaxLength ?? PolicyWriter.DefaultTextMaxLength;
                if (el.Suggestions is { Count: > 0 } suggestions)
                {
                    var combo = new ComboBox { Header = label, IsEditable = true, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch, Text = text };
                    foreach (var s in suggestions) combo.Items.Add(s);
                    combo.TextSubmitted += (_, _) => Raise();
                    combo.SelectionChanged += (_, _) => Raise();
                    combo.LostFocus += (_, _) => Raise();
                    control = combo;
                    read = () =>
                    {
                        var t = combo.SelectedItem as string ?? combo.Text ?? "";
                        return t.Length == 0 ? [] : [t];
                    };
                }
                else
                {
                    var box = new TextBox { Header = label, Text = text, MaxLength = maxLength };
                    box.TextChanged += (_, _) => Raise();
                    control = box;
                    read = () => box.Text.Length == 0 ? [] : [box.Text];
                }
                hint = $"Até {maxLength} caracteres." + (el.Expandable ? " Aceita variáveis de ambiente (REG_EXPAND_SZ)." : "");
                break;
            }

            case "multiText":
            case "list":
            {
                var lines = Initial(initial, el) ?? [];
                var box = new TextBox
                {
                    Header = label,
                    Text = string.Join(Environment.NewLine, lines),
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    MinHeight = 90,
                    MaxHeight = 220,
                    FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["MonoFont"],
                };
                ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
                box.TextChanged += (_, _) => Raise();
                control = box;
                read = () => box.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                    .Select(l => el.Type == "list" ? l.Trim() : l)
                    .Where(l => l.Length > 0).ToList();
                if (el.Type == "multiText")
                    hint = "Uma linha por item." + (el.MaxStrings is > 0 ? $" No máximo {el.MaxStrings} linhas." : "");
                else
                {
                    hint = el.ExplicitValue ? "Um item por linha, no formato nome=valor." : "Um item por linha.";
                    if (el.Additive)
                        hint += " Lista aditiva: itens que já estão no registro e você apagar daqui continuam lá; para limpar a lista, use Desabilitada ou Não configurada.";
                    hint += $" Gravada em {el.Key}.";
                }
                break;
            }

            default:
                View.Children.Add(new TextBlock { Text = $"Elemento '{el.Id}' de tipo '{el.Type}' não é editável.", Style = Style("Caption") });
                return;
        }

        _editors.Add((el, read, control));
        View.Children.Add(control);
        if (hint is not null) View.Children.Add(new TextBlock { Text = hint, Style = Style("Caption"), Margin = new Thickness(0, -6, 0, 0) });
    }
}
