using CommunityToolkit.Mvvm.ComponentModel;
using Debloater.App.Helpers;
using Debloater.Core.Model;
using Debloater.Core.Planning;
using Debloater.Core.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Debloater.App.ViewModels;

/// <summary>One action of the tweak with its individual state.</summary>
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

/// <summary>Source/reference link.</summary>
public sealed record LinkItem(string Text, Uri? Uri)
{
    public bool IsLink => Uri is not null;
    public bool IsText => Uri is null;
}

/// <summary>Row of the tweak list: tweak + current state + desired state.</summary>
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
    public string Name => Tweak.DisplayName;
    public string Description => Tweak.DisplayDescription.Trim();

    /// <summary>First sentence of the description, for the collapsed row.</summary>
    public string ShortDescription
    {
        get
        {
            var d = Description.ReplaceLineEndings(" ");
            int i = d.IndexOf(". ", StringComparison.Ordinal);
            return i > 0 && i < 160 ? d[..(i + 1)] : (d.Length > 180 ? d[..177] + "..." : d);
        }
    }

    /// <summary>Category as shown to the user (translated path).</summary>
    public string Category => _app.DisplayCategory(Tweak.Category);
    public string? Breaks => Tweak.DisplayBreaks?.Trim();
    public Visibility BreaksVisibility => string.IsNullOrWhiteSpace(Tweak.Breaks) ? Visibility.Collapsed : Visibility.Visible;

    public string PreconditionText => Tweak.DisplayPreconditionReason is { } reason && !string.IsNullOrWhiteSpace(reason)
        ? Loc.Format("Tweak_Precondition", reason.Trim()) : "";
    public Visibility PreconditionVisibility => PreconditionText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string RiskText => Labels.RiskShort(Tweak.Risk);
    public SolidColorBrush RiskBrush => Labels.Brush(Labels.RiskColor(Tweak.Risk));
    public SolidColorBrush RiskForeground => Labels.ForegroundOn(Labels.RiskColor(Tweak.Risk));
    public string RiskTip => Labels.Risk(Tweak.Risk);

    public string ConfidenceText => Labels.Confidence(Tweak.Confidence);
    public SolidColorBrush ConfidenceBrush => Labels.Brush(Labels.ConfidenceColor(Tweak.Confidence));
    public string ConfidenceTip => Labels.ConfidenceTip(Tweak.Confidence);

    public string BuildText => (Tweak.MinBuild, Tweak.MaxBuild) switch
    {
        (null, null) => Loc.Get("Tweak_AllBuilds"),
        ({ } min, null) => Loc.Format("Tweak_BuildMin", min),
        (null, { } max) => Loc.Format("Tweak_BuildMax", max),
        ({ } min, { } max) => Loc.Format("Tweak_BuildRange", min, max),
    };

    public string ReversibleText => Tweak.Reversible
        ? Loc.Get("Tweak_Reversible")
        : Loc.Get("Tweak_NotReversible");

    public string TagsText => Tweak.Tags.Count == 0 ? "" : Loc.Format("Tweak_Tags", string.Join(", ", Tweak.Tags));
    public string SourceFileText => Loc.Format("Tweak_SourceFile", Tweak.SourceFile);

    public IReadOnlyList<LinkItem> Links { get; }
    public Visibility LinksVisibility => Links.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    // ---------------- state ----------------

    [ObservableProperty]
    public partial TweakStatus? Status { get; set; }

    public TweakState? State => Status?.State;
    public string StateText => Status is null ? Loc.Get("Tweak_Checking") : Labels.State(Status.State);
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

    // ---------------- desired ----------------

    /// <summary>Desired state chosen explicitly (null = keep as is).</summary>
    public DesiredState? ExplicitDesired => _app.GetDesired(Id);

    /// <summary>Effective desired state: the explicit one or, without a choice, the current one (Partial = undefined).</summary>
    public DesiredState? EffectiveDesired => ExplicitDesired ?? State switch
    {
        TweakState.Applied => DesiredState.Applied,
        TweakState.NotApplied or TweakState.Error => DesiredState.NotApplied,
        _ => null,
    };

    /// <summary>"Desired: applied" check box. Indeterminate for the Partial state without a choice.</summary>
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

    public string PendingText => !IsPending ? "" : ExplicitDesired == DesiredState.Applied ? Loc.Get("Tweak_WillApply") : Loc.Get("Tweak_WillRevert");
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
                SourceRef.MsDocs => Loc.Format("Source_MsDocs", s.Ref),
                SourceRef.AdmxKind => Loc.Format("Source_Admx", s.Ref),
                SourceRef.CommunityKind => Loc.Format("Source_Community", s.Ref),
                SourceRef.ReKind => Loc.Format("Source_ReverseEngineering", s.Ref),
                SourceRef.ToolKind => Loc.Format("Source_Tool", s.Ref),
                _ => s.Ref,
            };
            links.Add(new LinkItem(label, uri));
        }
        foreach (var r in t.References)
            if (Uri.TryCreate(r, UriKind.Absolute, out var u)) links.Add(new LinkItem(r, u));
        return links;
    }

    /// <summary>"Policy CSP - Area/Name" → page policy-csp-area#name.</summary>
    private static Uri? MsDocsUri(string reference)
    {
        const string prefix = "Policy CSP - ";
        if (!reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = reference[prefix.Length..].Split('/', 2);
        if (parts.Length != 2) return null;
        return new Uri($"https://learn.microsoft.com/windows/client-management/mdm/policy-csp-{parts[0].ToLowerInvariant()}#{parts[1].ToLowerInvariant()}");
    }
}
