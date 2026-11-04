using System.Text;
using System.Windows.Input;
using SloomyTweaks.Core.Engine;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Presentation.Mvvm;
using SloomyTweaks.Presentation.Services;

namespace SloomyTweaks.Presentation.ViewModels;

public sealed record SourceViewModel(string Title, string Url, string KindText, ICommand OpenCommand);

/// <summary>
/// One optimization card. The ON/OFF toggle reflects the configuration read back from Windows, never an
/// intention: turning ON runs the full apply-and-verify workflow, turning OFF restores the saved original,
/// and <see cref="IsOn"/> only changes after the engine has re-read the real state.
/// </summary>
public sealed class OptimizationCardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<bool> _hasPremium;
    private OptimizationStatus _status;
    private bool _isBusy;
    private bool _isExpanded;
    private string? _resultMessage;
    private bool _resultIsError;
    private IReadOnlyList<SelectorOption> _options = Array.Empty<SelectorOption>();

    public OptimizationCardViewModel(OptimizationStatus status, AppServices services, Func<bool> hasPremium)
    {
        _status = status;
        _services = services;
        _hasPremium = hasPremium;
        OptimizationDefinition d = status.Optimization.Definition;
        Sources = d.Sources.Select(s => new SourceViewModel(s.Title, s.Url, SourceKindText(s.Kind),
            new RelayCommand(() => services.Dialogs.OpenUrl(s.Url)))).ToList();
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => CanToggle, ReportError);
        RestoreOriginalCommand = new AsyncRelayCommand(RestoreAsync, () => CanRestoreOriginal, ReportError);
        ToggleDetailsCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        LoadOptions();
    }

    // ---------------------------------------------------------------- definition
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
        _ => "Not recommended",
    };
    public RiskLevel Risk => Definition.Risk;
    public string RiskText => Definition.Risk switch { RiskLevel.Low => "Low risk", RiskLevel.Medium => "Medium risk", _ => "High risk" };
    public string RestartText => Definition.RequiresRestart ? "Restart required" : "No restart";
    public bool RequiresRestart => Definition.RequiresRestart;
    public string AdministratorText => Definition.RequiresAdministrator ? "Administrator required" : "No admin needed";
    public bool RequiresAdministrator => Definition.RequiresAdministrator;
    public IReadOnlyList<SourceViewModel> Sources { get; }
    public bool HasSources => Sources.Count > 0;
    public string CompatibilitySummary => Definition.CompatibilitySummary;
    public IReadOnlyList<string> Risks => Definition.Risks;
    public bool HasRisks => Risks.Count > 0;
    public IReadOnlyList<string> ManualSteps => Definition.ManualSteps;
    public bool HasManualSteps => ManualSteps.Count > 0;
    public bool IsToggle => Definition.Kind == OptimizationKind.Actionable;
    public bool IsSelector => Definition.Kind == OptimizationKind.Selector;
    public bool IsGuidance => Definition.Kind == OptimizationKind.Guidance;
    public bool InRecommendedFlow => Definition.EligibleForRecommendedFlow;

    // ---------------------------------------------------------------- live state
    public OptimizationStatus Status => _status;
    public OptimizationState State => _status.State;
    public bool HasBackup => _status.HasBackup;
    public bool RestartPending => _status.RestartPending;

    /// <summary>Not applicable on this system: the toggle shows a neutral "N/A" state.</summary>
    public bool IsUnsupported => _status.Compatibility.Status == CompatibilityStatus.Unsupported;

    /// <summary>True only when Windows reports the optimized configuration (never for unsupported items).</summary>
    public bool IsOn => IsToggle && !IsUnsupported && _status.ConfigState == OptimizationState.Enabled && _status.DetectionError is null;

    public string CompatibilityText => _status.Compatibility.Status switch
    {
        CompatibilityStatus.Compatible => "Compatible",
        CompatibilityStatus.CompatibleWithWarnings => "Compatible with warnings",
        CompatibilityStatus.Unverified => "Support unverified",
        _ => "Not supported",
    };
    public bool IsCompatibilityOk => _status.Compatibility.Status == CompatibilityStatus.Compatible;
    public IReadOnlyList<string> CompatibilityReasons => _status.Compatibility.Reasons;
    public bool HasCompatibilityReasons => CompatibilityReasons.Count > 0;

    public string StatusText
    {
        get
        {
            if (IsGuidance) return "Manual guide";
            if (_status.DetectionError is not null) return "State could not be read";
            if (_status.Compatibility.Status == CompatibilityStatus.Unsupported) return "Not supported on this PC";
            string baseText = IsSelector
                ? (SelectedOption?.Label ?? "Unknown")
                : _status.ConfigState switch
                {
                    OptimizationState.Enabled => "On",
                    OptimizationState.Disabled => "Off",
                    _ => "Unknown (Windows default)",
                };
            return RestartPending ? baseText + " · Pending restart" : baseText;
        }
    }

    public string? RestoreInfo => !_status.HasBackup
        ? null // the blocked-reason line already explains the "already on" case
        : IsSelector
            ? $"Original value saved {_status.BackupCreatedAt?.LocalDateTime:g}. Use \"Restore original\" to put it back."
            : $"Original value saved {_status.BackupCreatedAt?.LocalDateTime:g}. Turning this off restores it exactly.";

    // ---------------------------------------------------------------- toggle
    public bool CanToggle => ToggleBlockedReason is null && !IsBusy;

    /// <summary>Why the toggle is disabled, or null when it can be used.</summary>
    public string? ToggleBlockedReason
    {
        get
        {
            if (!IsToggle) return null;
            if (_status.DetectionError is not null) return "The current state could not be read: " + _status.DetectionError;
            if (_status.Compatibility.Status == CompatibilityStatus.Unsupported) return string.Join(" ", _status.Compatibility.Reasons);
            if (Definition.RequiresAdministrator && !_services.App.IsElevated) return "Requires administrator rights. Use \"Restart as administrator\" in the title bar.";
            if (IsOn && !_status.HasBackup) return "Already on before Sloomy Tweaks changed anything, so there is no original to restore. Change it in Windows Settings if you want it off.";
            if (!IsOn && !_hasPremium()) return "An active license is required to apply optimizations.";
            return null;
        }
    }

    public AsyncRelayCommand ToggleCommand { get; }

    internal async Task ToggleAsync()
    {
        if (IsOn) await RestoreAsync();
        else await ApplyAsync();
    }

    internal async Task ApplyAsync()
    {
        OptimizationDefinition d = Definition;
        bool needsConfirmation = _status.Compatibility.RequiresAcknowledgement || d.Evidence == EvidenceLevel.Experimental || d.RequiresRestart;
        if (needsConfirmation && !await _services.Dialogs.ConfirmAsync($"Turn on: {d.Name}", Explain(_status.Optimization.Primitives, _status.Compatibility.Reasons), "Turn on"))
            return;

        await RunAsync(() => _services.Engine.ApplyAsync(Id, warningsAcknowledged: true));
    }

    internal async Task RestoreAsync() => await RunAsync(() => _services.Engine.RestoreAsync(Id));

    // ---------------------------------------------------------------- selector
    public IReadOnlyList<SelectorOption> Options { get => _options; private set => SetProperty(ref _options, value); }

    public SelectorOption? SelectedOption
    {
        get => _options.FirstOrDefault(o => string.Equals(o.Value, _status.SelectedValue, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null || string.Equals(value.Value, _status.SelectedValue, StringComparison.OrdinalIgnoreCase)) return;
            _ = SelectAsync(value);
        }
    }

    public bool CanSelect => IsSelector && !IsBusy && _status.Compatibility.Status != CompatibilityStatus.Unsupported && _hasPremium()
        && _status.DetectionError is null && _options.Count > 0
        && !(Definition.RequiresAdministrator && !_services.App.IsElevated);

    public bool CanRestoreOriginal => IsSelector && !IsBusy && _status.HasBackup;
    public AsyncRelayCommand RestoreOriginalCommand { get; }

    internal async Task SelectAsync(SelectorOption option)
    {
        if (!CanSelect)
        {
            OnPropertyChanged(nameof(SelectedOption)); // revert the visual selection
            return;
        }
        Optimization opt = _status.Optimization;
        var context = _services.Hardware.GetContext();
        var compat = opt.Selector!.CompatibilityFor?.Invoke(context, option.Value) ?? CompatibilityResult.Compatible();
        bool confirm = compat.RequiresAcknowledgement || Definition.RequiresRestart;
        if (confirm)
        {
            var primitives = opt.Selector.PrimitivesFor(option.Value);
            if (!await _services.Dialogs.ConfirmAsync($"{Definition.Name}: {option.Label}", Explain(primitives, compat.Reasons), "Change"))
            {
                OnPropertyChanged(nameof(SelectedOption));
                return;
            }
        }
        await RunAsync(() => _services.Engine.SelectAsync(Id, option.Value, warningsAcknowledged: true));
    }

    // ---------------------------------------------------------------- shared
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RaiseCommandStates(); }
    }

    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public string? ResultMessage { get => _resultMessage; private set => SetProperty(ref _resultMessage, value); }
    public bool ResultIsError { get => _resultIsError; private set => SetProperty(ref _resultIsError, value); }
    public RelayCommand ToggleDetailsCommand { get; }

    private async Task RunAsync(Func<Task<OperationResult>> operation)
    {
        IsBusy = true;
        try
        {
            OperationResult result = await operation();
            ResultIsError = !result.Success;
            ResultMessage = result.ErrorDetail is null ? result.Message : $"{result.Message} ({result.ErrorDetail})";
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
        LoadOptions();
        foreach (string p in new[]
                 {
                     nameof(Status), nameof(State), nameof(IsOn), nameof(IsUnsupported), nameof(HasBackup), nameof(RestartPending), nameof(StatusText),
                     nameof(CompatibilityText), nameof(IsCompatibilityOk), nameof(CompatibilityReasons), nameof(HasCompatibilityReasons),
                     nameof(RestoreInfo), nameof(SelectedOption),
                 })
            OnPropertyChanged(p);
        RaiseCommandStates();
    }

    public void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ToggleBlockedReason));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(CanRestoreOriginal));
        ToggleCommand.RaiseCanExecuteChanged();
        RestoreOriginalCommand.RaiseCanExecuteChanged();
    }

    private void LoadOptions()
    {
        if (!IsSelector) return;
        try { Options = _status.Optimization.Selector!.Options(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Options = Array.Empty<SelectorOption>(); ResultMessage = "Options could not be read: " + e.Message; ResultIsError = true; }
        OnPropertyChanged(nameof(CanSelect));
    }

    private string Explain(IReadOnlyList<ISettingPrimitive> primitives, IReadOnlyList<string> compatibilityReasons)
    {
        OptimizationDefinition d = Definition;
        var sb = new StringBuilder();
        sb.AppendLine(d.Description).AppendLine();
        sb.AppendLine("Changes:");
        foreach (ISettingPrimitive p in primitives) sb.AppendLine("  • " + p.DesiredDescription);
        sb.AppendLine().AppendLine("Evidence: " + EvidenceText + ". " + d.ExpectedBenefit);
        if (d.Risks.Count > 0)
        {
            sb.AppendLine().AppendLine("Risks:");
            foreach (string r in d.Risks) sb.AppendLine("  • " + r);
        }
        if (compatibilityReasons.Count > 0)
        {
            sb.AppendLine().AppendLine("Compatibility notes:");
            foreach (string r in compatibilityReasons) sb.AppendLine("  • " + r);
        }
        if (d.RequiresRestart) sb.AppendLine().AppendLine("A restart is required for this change to take effect.");
        sb.AppendLine().Append("Your current settings are saved first and can be restored at any time.");
        return sb.ToString();
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
