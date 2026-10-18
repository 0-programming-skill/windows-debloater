using CommunityToolkit.Mvvm.ComponentModel;
using Debloater.App.Helpers;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Debloater.App.ViewModels;

/// <summary>Uma ação do tweak com o seu estado individual.</summary>
public sealed class ActionItemViewModel(ActionSpec action, ActionStatus? status)
{
    public string TypeText { get; } = Labels.ActionType(action.Type);
    public string Desired { get; } = Labels.ActionDesired(action);
    public string StateText { get; } = status is null ? "—" : Labels.ActionState(status.State);
    public string Detail { get; } = status?.Detail ?? "";
    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(Detail) ? Visibility.Collapsed : Visibility.Visible;

    public SolidColorBrush StateBrush { get; } = Labels.Brush(status?.State switch
    {
        ActionState.Desired => Labels.StateColor(TweakState.Applied),
        ActionState.NotDesired => Labels.StateColor(TweakState.NotApplied),
        ActionState.Error => Labels.StateColor(TweakState.Error),
        _ => Labels.StateColor(TweakState.NotApplicable),
    });
}

/// <summary>Link de fonte/referência.</summary>
public sealed record LinkItem(string Text, Uri? Uri)
{
    public bool IsLink => Uri is not null;
    public bool IsText => Uri is null;
}

/// <summary>Linha da lista de ajustes: tweak + estado atual + estado desejado.</summary>
public sealed partial class TweakItemViewModel : ObservableObject
{
    private readonly AppState _app;

    public TweakItemViewModel(Tweak tweak, AppState app)
    {
        Tweak = tweak;
        _app = app;
        Links = BuildLinks(tweak);
    }

    public Tweak Tweak { get; }
    public string Id => Tweak.Id;
    public string Name => Tweak.Name;
    public string Description => Tweak.Description.Trim();

    /// <summary>Primeira frase da descrição, para a linha recolhida.</summary>
    public string ShortDescription
    {
        get
        {
            var d = Description.ReplaceLineEndings(" ");
            int i = d.IndexOf(". ", StringComparison.Ordinal);
            return i > 0 && i < 160 ? d[..(i + 1)] : (d.Length > 180 ? d[..177] + "..." : d);
        }
    }

    public string Category => Tweak.Category;
    public string? Breaks => Tweak.Breaks?.Trim();
    public Visibility BreaksVisibility => string.IsNullOrWhiteSpace(Tweak.Breaks) ? Visibility.Collapsed : Visibility.Visible;

    public string RiskText => Labels.RiskShort(Tweak.Risk);
    public SolidColorBrush RiskBrush => Labels.Brush(Labels.RiskColor(Tweak.Risk));
    public SolidColorBrush RiskForeground => Labels.ForegroundOn(Labels.RiskColor(Tweak.Risk));
    public string RiskTip => Labels.Risk(Tweak.Risk);

    public string ConfidenceText => Labels.Confidence(Tweak.Confidence);
    public SolidColorBrush ConfidenceBrush => Labels.Brush(Labels.ConfidenceColor(Tweak.Confidence));
    public string ConfidenceTip => Labels.ConfidenceTip(Tweak.Confidence);

    public string BuildText => (Tweak.MinBuild, Tweak.MaxBuild) switch
    {
        (null, null) => "Todos os builds",
        ({ } min, null) => $"Build {min} ou superior",
        (null, { } max) => $"Até o build {max}",
        ({ } min, { } max) => $"Builds {min} a {max}",
    };

    public string ReversibleText => Tweak.Reversible
        ? "Reversível: o valor original é guardado em backup antes de aplicar."
        : "Não reversível pelo programa (ex.: app removido; reinstale pela Microsoft Store).";

    public string TagsText => Tweak.Tags.Count == 0 ? "" : "Tags: " + string.Join(", ", Tweak.Tags);
    public string SourceFileText => $"Arquivo: {Tweak.SourceFile}";

    public IReadOnlyList<LinkItem> Links { get; }
    public Visibility LinksVisibility => Links.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    // ---------------- estado ----------------

    [ObservableProperty]
    public partial TweakStatus? Status { get; set; }

    public TweakState? State => Status?.State;
    public string StateText => Status is null ? "Verificando..." : Labels.State(Status.State);
    public SolidColorBrush StateBrush => Labels.Brush(Status is null ? Labels.StateColor(TweakState.NotApplicable) : Labels.StateColor(Status.State));
    public SolidColorBrush StateForeground => Labels.ForegroundOn(Status is null ? Labels.StateColor(TweakState.NotApplicable) : Labels.StateColor(Status.State));
    public string StateMessage => Status?.Message ?? "";
    public Visibility StateMessageVisibility => string.IsNullOrWhiteSpace(Status?.Message) ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<ActionItemViewModel> Actions =>
        Tweak.Actions.Select((a, i) => new ActionItemViewModel(a, Status?.Actions.ElementAtOrDefault(i))).ToList();

    partial void OnStatusChanged(TweakStatus? value)
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(StateForeground));
        OnPropertyChanged(nameof(StateMessage));
        OnPropertyChanged(nameof(StateMessageVisibility));
        OnPropertyChanged(nameof(Actions));
        RaiseDesiredChanged();
    }

    // ---------------- desejado ----------------

    /// <summary>Estado desejado escolhido explicitamente (nulo = manter como está).</summary>
    public DesiredState? ExplicitDesired => _app.GetDesired(Id);

    /// <summary>Desejado efetivo: o explícito ou, sem escolha, o atual (Parcial = indefinido).</summary>
    public DesiredState? EffectiveDesired => ExplicitDesired ?? State switch
    {
        TweakState.Applied => DesiredState.Applied,
        TweakState.NotApplied or TweakState.Error => DesiredState.NotApplied,
        _ => null,
    };

    /// <summary>Caixa "desejado: aplicado". Indeterminada para estado Parcial sem escolha.</summary>
    public bool? DesiredChecked
    {
        get => EffectiveDesired switch
        {
            DesiredState.Applied => true,
            DesiredState.NotApplied => false,
            _ => null,
        };
        set
        {
            if (value is null) return;
            var want = value.Value ? DesiredState.Applied : DesiredState.NotApplied;
            if (want == ExplicitDesired) return;
            _app.SetDesired(Id, want);
        }
    }

    public bool CanChoose => State is not null and not TweakState.NotApplicable;

    public bool IsPending => State is { } s && ExplicitDesired is { } d && ChangePlanner.NeedsChange(s, d);

    public string PendingText => !IsPending ? "" : ExplicitDesired == DesiredState.Applied ? "Será aplicado" : "Será revertido";
    public Visibility PendingVisibility => IsPending ? Visibility.Visible : Visibility.Collapsed;

    public void RaiseDesiredChanged()
    {
        OnPropertyChanged(nameof(ExplicitDesired));
        OnPropertyChanged(nameof(EffectiveDesired));
        OnPropertyChanged(nameof(DesiredChecked));
        OnPropertyChanged(nameof(CanChoose));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(PendingVisibility));
    }

    private static List<LinkItem> BuildLinks(Tweak t)
    {
        var links = new List<LinkItem>();
        foreach (var s in t.Sources)
        {
            Uri? uri = s.Kind switch
            {
                SourceRef.MsDocs => MsDocsUri(s.Ref),
                SourceRef.Url or SourceRef.CommunityKind when Uri.TryCreate(s.Ref, UriKind.Absolute, out var u) => u,
                _ => null,
            };
            var label = s.Kind switch
            {
                SourceRef.MsDocs => "Microsoft Learn: " + s.Ref,
                SourceRef.AdmxKind => "Política ADMX: " + s.Ref,
                SourceRef.CommunityKind => "Comunidade: " + s.Ref,
                SourceRef.ReKind => "Engenharia reversa: " + s.Ref,
                SourceRef.ToolKind => "Ferramenta: " + s.Ref,
                _ => s.Ref,
            };
            links.Add(new LinkItem(label, uri));
        }
        foreach (var r in t.References)
            if (Uri.TryCreate(r, UriKind.Absolute, out var u)) links.Add(new LinkItem(r, u));
        return links;
    }

    /// <summary>"Policy CSP - Area/Name" → página policy-csp-area#name.</summary>
    private static Uri? MsDocsUri(string reference)
    {
        const string prefix = "Policy CSP - ";
        if (!reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = reference[prefix.Length..].Split('/', 2);
        if (parts.Length != 2) return null;
        return new Uri($"https://learn.microsoft.com/windows/client-management/mdm/policy-csp-{parts[0].ToLowerInvariant()}#{parts[1].ToLowerInvariant()}");
    }
}
