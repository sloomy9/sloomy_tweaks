using System.Text;
using System.Windows.Input;
using SloomyTweaks.Core.Engine;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Presentation.Mvvm;
using SloomyTweaks.Presentation.Services;

namespace SloomyTweaks.Presentation.ViewModels;

public sealed record SourceViewModel(string Title, string Url, string KindText, ICommand OpenCommand);

/// <summary>One optimization card. State always comes from reading the real configuration.</summary>
public sealed class OptimizationCardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<bool> _hasPremium;
    private OptimizationStatus _status;
    private bool _isBusy;
    private bool _isExpanded;
    private string? _resultMessage;
    private bool _resultIsError;

    public OptimizationCardViewModel(OptimizationStatus status, AppServices services, Func<bool> hasPremium)
    {
        _status = status;
        _services = services;
        _hasPremium = hasPremium;
        OptimizationDefinition d = status.Optimization.Definition;
        Sources = d.Sources.Select(s => new SourceViewModel(s.Title, s.Url, SourceKindText(s.Kind),
            new RelayCommand(() => services.Dialogs.OpenUrl(s.Url)))).ToList();
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply, ReportError);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => CanRestore, ReportError);
        ToggleDetailsCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public OptimizationDefinition Definition => _status.Optimization.Definition;
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string TechnicalMechanism => Definition.TechnicalMechanism;
    public string ExpectedBenefit => Definition.ExpectedBenefit;
    public string EvidenceNotes => Definition.EvidenceNotes;
    public EvidenceLevel Evidence => Definition.Evidence;
    public string EvidenceText => Definition.Evidence switch
    {
        EvidenceLevel.Verified => "Verified",
        EvidenceLevel.Documented => "Documented",
        EvidenceLevel.Experimental => "Experimental",
        _ => "Not Recommended",
    };
    public IReadOnlyList<SourceViewModel> Sources { get; }
    public bool HasSources => Sources.Count > 0;
    public string CompatibilitySummary => Definition.CompatibilitySummary;
    public IReadOnlyList<string> CompatibilityReasons => _status.Compatibility.Reasons;
    public bool HasCompatibilityReasons => CompatibilityReasons.Count > 0;
    public IReadOnlyList<string> Risks => Definition.Risks;
    public bool HasRisks => Risks.Count > 0;
    public IReadOnlyList<string> ManualSteps => Definition.ManualSteps;
    public bool HasManualSteps => ManualSteps.Count > 0;
    public string RestartRequiredText => Definition.Kind == OptimizationKind.Actionable ? (Definition.RequiresRestart ? "Yes" : "No") : "—";
    public string AdministratorText => Definition.RequiresAdministrator ? "Required" : "Not required";
    public bool IsActionable => Definition.Kind == OptimizationKind.Actionable;
    public bool IsGuidance => Definition.Kind == OptimizationKind.Guidance;
    public bool IsNotOffered => Definition.Kind == OptimizationKind.NotOffered;
    public bool InRecommendedFlow => Definition.EligibleForRecommendedFlow;

    public OptimizationState State => _status.State;
    public string StateText => Definition.Kind switch
    {
        OptimizationKind.Guidance => "Guidance",
        OptimizationKind.NotOffered => "Not offered",
        _ => _status.State switch
        {
            OptimizationState.Enabled => "Enabled",
            OptimizationState.Disabled => "Disabled",
            OptimizationState.Unsupported => "Unsupported",
            OptimizationState.RestartPending => "Restart pending",
            _ => _status.DetectionError is null ? "Unknown" : "Unknown (detection failed)",
        },
    };

    public bool HasBackup => _status.HasBackup;

    public bool CanApply => IsActionable && !IsBusy && _hasPremium()
        && _status.State is OptimizationState.Disabled or OptimizationState.Unknown;

    /// <summary>Restore is always allowed, even without a license, so users can undo changes.</summary>
    public bool CanRestore => IsActionable && !IsBusy && _status.HasBackup;

    public string? ApplyBlockedReason => !IsActionable ? null
        : !_hasPremium() ? "Activate a license to apply optimizations."
        : _status.State == OptimizationState.Unsupported ? "Not supported on this system."
        : _status.State == OptimizationState.Enabled ? null
        : _status.State == OptimizationState.RestartPending ? "Restart Windows to finish the previous change."
        : null;

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RaiseCommandStates(); }
    }

    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public string? ResultMessage { get => _resultMessage; private set => SetProperty(ref _resultMessage, value); }
    public bool ResultIsError { get => _resultIsError; private set => SetProperty(ref _resultIsError, value); }

    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand ToggleDetailsCommand { get; }

    internal async Task ApplyAsync()
    {
        OptimizationDefinition d = Definition;
        var explanation = new StringBuilder();
        explanation.AppendLine(d.Description).AppendLine();
        explanation.AppendLine("Changes:");
        foreach (ISettingPrimitive p in _status.Optimization.Primitives) explanation.AppendLine("  • " + p.DesiredDescription);
        explanation.AppendLine().AppendLine("Evidence: " + EvidenceText + ". " + d.ExpectedBenefit);
        if (d.Risks.Count > 0)
        {
            explanation.AppendLine().AppendLine("Risks:");
            foreach (string r in d.Risks) explanation.AppendLine("  • " + r);
        }
        if (_status.Compatibility.Reasons.Count > 0)
        {
            explanation.AppendLine().AppendLine("Compatibility notes:");
            foreach (string r in _status.Compatibility.Reasons) explanation.AppendLine("  • " + r);
        }
        if (d.RequiresRestart) explanation.AppendLine().AppendLine("A restart is required for this change to take effect.");
        explanation.AppendLine().Append("Your current settings are saved and can be restored from this card or the Restore Center.");

        bool confirmed = await _services.Dialogs.ConfirmAsync($"Apply: {d.Name}", explanation.ToString(), "Apply");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            OperationResult result = await _services.Engine.ApplyAsync(Id, warningsAcknowledged: true);
            ShowResult(result);
        }
        finally
        {
            await RefreshAsync();
            IsBusy = false;
        }
    }

    internal async Task RestoreAsync()
    {
        bool confirmed = await _services.Dialogs.ConfirmAsync($"Restore: {Definition.Name}",
            "Sloomy Tweaks will restore the values that were in place before it changed this setting.", "Restore");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            ShowResult(await _services.Engine.RestoreAsync(Id));
        }
        finally
        {
            await RefreshAsync();
            IsBusy = false;
        }
    }

    public async Task RefreshAsync()
    {
        _status = await Task.Run(() => _services.Engine.GetStatus(Id));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasBackup));
        OnPropertyChanged(nameof(CompatibilityReasons));
        OnPropertyChanged(nameof(HasCompatibilityReasons));
        OnPropertyChanged(nameof(ApplyBlockedReason));
        RaiseCommandStates();
    }

    public void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(ApplyBlockedReason));
        ApplyCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
    }

    private void ShowResult(OperationResult result)
    {
        ResultIsError = !result.Success;
        ResultMessage = result.ErrorDetail is null ? result.Message : $"{result.Message} ({result.ErrorDetail})";
    }

    private void ReportError(Exception e)
    {
        IsBusy = false;
        ResultIsError = true;
        ResultMessage = "Unexpected error: " + e.Message;
        _services.Log.Log(Core.Diagnostics.LogLevel.Error, "ui.card_error", e.Message, new Dictionary<string, object?> { ["id"] = Id }, e);
    }

    private static string SourceKindText(SourceKind kind) => kind switch
    {
        SourceKind.OfficialDocumentation => "Official documentation",
        SourceKind.VendorDocumentation => "Vendor documentation",
        SourceKind.IndependentMeasurement => "Independent measurement",
        SourceKind.TechnicalReporting => "Technical reporting",
        _ => "Community documentation",
    };
}
