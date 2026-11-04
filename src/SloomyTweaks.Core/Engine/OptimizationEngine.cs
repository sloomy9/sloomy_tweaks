using SloomyTweaks.Core.Diagnostics;
using SloomyTweaks.Core.Hardware;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Core.Platform;

namespace SloomyTweaks.Core.Engine;

public enum OperationOutcome
{
    Succeeded,
    AlreadyInDesiredState,
    NotActionable,
    Unsupported,
    AcknowledgementRequired,
    AdministratorRequired,
    Conflict,
    NoBackup,
    Failed,
    FailedAndRolledBack,
    VerificationFailed,
}

public sealed record OperationResult(
    string OptimizationId,
    OperationOutcome Outcome,
    string Message,
    IReadOnlyList<string> Steps,
    bool RestartRequired = false,
    string? ErrorDetail = null)
{
    public bool Success => Outcome is OperationOutcome.Succeeded or OperationOutcome.AlreadyInDesiredState;
}

public sealed record OptimizationStatus(
    Optimization Optimization,
    OptimizationState State,
    CompatibilityResult Compatibility,
    bool HasBackup,
    IReadOnlyDictionary<string, SettingValue> CurrentValues,
    string? DetectionError);

/// <summary>
/// Implements the Sloomy Tweaks safe optimization workflow:
/// detect → compatibility → privileges → conflicts → save originals → apply → verify → log → offer rollback.
/// A success is reported only after the resulting configuration has been read back and verified.
/// Any failure during apply or verification rolls back the primitives already changed.
/// </summary>
public sealed class OptimizationEngine
{
    private readonly IReadOnlyDictionary<string, Optimization> _catalog;
    private readonly Func<SystemContext> _context;
    private readonly IPrivilegeService _privileges;
    private readonly IBackupStore _backups;
    private readonly IHistoryStore _history;
    private readonly IRestorePointService? _restorePoints;
    private readonly IClock _clock;
    private readonly IAppLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _restorePointAttempted;

    public OptimizationEngine(
        IEnumerable<Optimization> catalog,
        Func<SystemContext> context,
        IPrivilegeService privileges,
        IBackupStore backups,
        IHistoryStore history,
        IClock clock,
        IAppLogger log,
        IRestorePointService? restorePoints = null)
    {
        _catalog = catalog.ToDictionary(o => o.Id);
        _context = context;
        _privileges = privileges;
        _backups = backups;
        _history = history;
        _clock = clock;
        _log = log;
        _restorePoints = restorePoints;
    }

    /// <summary>When true, a System Restore point is attempted once per session before the first system-wide change.</summary>
    public bool CreateRestorePoints { get; set; } = true;

    public IReadOnlyCollection<Optimization> Catalog => _catalog.Values.ToList();

    public Optimization Get(string id) =>
        _catalog.TryGetValue(id, out var o) ? o : throw new KeyNotFoundException($"Unknown optimization '{id}'.");

    public OptimizationStatus GetStatus(string id)
    {
        Optimization opt = Get(id);
        SystemContext ctx = _context();
        CompatibilityResult compat = opt.CheckCompatibility(ctx);
        bool hasBackup = _backups.Get(id) is not null;

        if (opt.Definition.Kind != OptimizationKind.Actionable)
            return new(opt, OptimizationState.Unsupported, compat, false, new Dictionary<string, SettingValue>(), null);

        try
        {
            var current = opt.ReadCurrent();
            OptimizationState state = compat.Status == CompatibilityStatus.Unsupported
                ? OptimizationState.Unsupported
                : opt.DetectState(current);

            if (opt.Definition.RequiresRestart && IsRestartPending(id, ctx))
                state = OptimizationState.RestartPending;

            return new(opt, state, compat, hasBackup, current, null);
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            _log.Error("optimization.detect_failed", $"State detection failed for {id}", e, new Dictionary<string, object?> { ["id"] = id });
            return new(opt, OptimizationState.Unknown, compat, hasBackup, new Dictionary<string, SettingValue>(), e.Message);
        }
    }

    public async Task<OperationResult> ApplyAsync(string id, bool warningsAcknowledged, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ApplyCore(id, warningsAcknowledged), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> RestoreAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RestoreCore(id), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private OperationResult ApplyCore(string id, bool warningsAcknowledged)
    {
        var steps = new List<string>();
        Optimization opt = Get(id);
        OptimizationDefinition def = opt.Definition;

        if (def.Kind != OptimizationKind.Actionable)
            return Finish(id, OperationAction.Apply, OperationOutcome.NotActionable, "This item is guidance only; Sloomy Tweaks does not change it.", steps, null, null);

        // 1. Detect current configuration.
        IReadOnlyDictionary<string, SettingValue> before;
        try
        {
            before = opt.ReadCurrent();
            steps.Add("Detected current configuration: " + Describe(before));
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            return Finish(id, OperationAction.Apply, OperationOutcome.Failed, "Could not read the current configuration.", steps, null, null, e);
        }

        // 2. Hardware and Windows compatibility.
        SystemContext ctx = _context();
        CompatibilityResult compat = opt.CheckCompatibility(ctx);
        if (compat.Status == CompatibilityStatus.Unsupported)
            return Finish(id, OperationAction.Apply, OperationOutcome.Unsupported, string.Join(" ", compat.Reasons), steps, before, null);
        if (compat.RequiresAcknowledgement && !warningsAcknowledged)
            return Finish(id, OperationAction.Apply, OperationOutcome.AcknowledgementRequired, string.Join(" ", compat.Reasons), steps, before, null, log: false);
        steps.Add(compat.Reasons.Count == 0 ? "Compatibility check passed." : "Compatibility warnings acknowledged: " + string.Join(" ", compat.Reasons));

        // 3. Administrator permissions.
        if (def.RequiresAdministrator && !_privileges.IsElevated)
            return Finish(id, OperationAction.Apply, OperationOutcome.AdministratorRequired, "Administrator rights are required. Restart Sloomy Tweaks as administrator to apply this change.", steps, before, null, log: false);

        // 4. Conflicts.
        foreach (string conflictId in def.ConflictsWith)
        {
            if (_catalog.ContainsKey(conflictId) && GetStatus(conflictId).State is OptimizationState.Enabled or OptimizationState.RestartPending)
                return Finish(id, OperationAction.Apply, OperationOutcome.Conflict, $"Conflicts with '{_catalog[conflictId].Definition.Name}', which is currently enabled.", steps, before, null);
        }

        if (opt.DetectState(before) == OptimizationState.Enabled)
            return Finish(id, OperationAction.Apply, OperationOutcome.AlreadyInDesiredState, "Already configured. No changes were made.", steps, before, before, log: false);

        // 5. Save original values (keep the earliest backup if one already exists).
        OptimizationBackup? existing = _backups.Get(id);
        if (existing is null)
        {
            _backups.Save(new OptimizationBackup(id, _clock.UtcNow, before));
            steps.Add("Saved original values for rollback.");
        }
        else
        {
            steps.Add($"Kept original values saved on {existing.CreatedAt:u}.");
        }

        if (def.RequiresAdministrator && CreateRestorePoints && _restorePoints is not null && !_restorePointAttempted)
        {
            _restorePointAttempted = true;
            RestorePointResult rp = _restorePoints.TryCreate($"Sloomy Tweaks: before {def.Name}");
            steps.Add(rp.Created ? "Created a System Restore point." : $"System Restore point not created ({rp.Message}). Sloomy Tweaks' own backup is still available.");
        }

        // 6/7. Apply (the explanation is shown by the UI before this call).
        var applied = new List<ISettingPrimitive>();
        try
        {
            foreach (ISettingPrimitive p in opt.Primitives)
            {
                p.ApplyDesired();
                applied.Add(p);
                steps.Add($"Applied {p.DesiredDescription}.");
            }
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            string rollback = Rollback(applied, before, steps);
            if (existing is null) _backups.Delete(id);
            return Finish(id, OperationAction.Apply, OperationOutcome.FailedAndRolledBack, $"The change failed and was rolled back. {rollback}", steps, before, SafeRead(opt), e);
        }

        // 8. Verify by reading the configuration back.
        IReadOnlyDictionary<string, SettingValue> after;
        try
        {
            after = opt.ReadCurrent();
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            string rollback = Rollback(applied, before, steps);
            if (existing is null) _backups.Delete(id);
            return Finish(id, OperationAction.Apply, OperationOutcome.FailedAndRolledBack, $"Could not verify the change; it was rolled back. {rollback}", steps, before, null, e);
        }

        var unverified = opt.Primitives.Where(p => p.IsDesired(after[p.Id]) != true).ToList();
        if (unverified.Count > 0)
        {
            string detail = string.Join("; ", unverified.Select(p => $"{p.Id} reads {after[p.Id]}"));
            steps.Add("Verification failed: " + detail);
            string rollback = Rollback(applied, before, steps);
            if (existing is null) _backups.Delete(id);
            return Finish(id, OperationAction.Apply, OperationOutcome.VerificationFailed,
                $"Windows did not keep the new value (possibly enforced by policy or another tool). The change was rolled back. {rollback}",
                steps, before, SafeRead(opt), detail: detail);
        }
        steps.Add("Verified resulting configuration: " + Describe(after));

        // 9/10. Log and offer rollback.
        string message = def.RequiresRestart
            ? "Applied and verified. Restart Windows for the change to take effect. You can restore the original setting at any time."
            : "Applied and verified. You can restore the original setting at any time.";
        return Finish(id, OperationAction.Apply, OperationOutcome.Succeeded, message, steps, before, after, restartRequired: def.RequiresRestart);
    }

    private OperationResult RestoreCore(string id)
    {
        var steps = new List<string>();
        Optimization opt = Get(id);
        OptimizationBackup? backup = _backups.Get(id);
        if (backup is null)
            return Finish(id, OperationAction.Restore, OperationOutcome.NoBackup, "Sloomy Tweaks has no saved original values for this optimization. It was not changed by Sloomy Tweaks, or it was already restored.", steps, null, null, log: false);

        if (opt.Definition.RequiresAdministrator && !_privileges.IsElevated)
            return Finish(id, OperationAction.Restore, OperationOutcome.AdministratorRequired, "Administrator rights are required to restore this setting.", steps, null, null, log: false);

        IReadOnlyDictionary<string, SettingValue> before = SafeRead(opt) ?? new Dictionary<string, SettingValue>();
        var errors = new List<string>();
        foreach (ISettingPrimitive p in opt.Primitives)
        {
            if (!backup.OriginalValues.TryGetValue(p.Id, out SettingValue? original))
            {
                errors.Add($"No saved value for {p.Id}.");
                continue;
            }
            try
            {
                p.Restore(original);
                SettingValue now = p.Read();
                if (p.IsRestored(now, original))
                    steps.Add($"Restored {p.Id} to {original}.");
                else
                    errors.Add($"{p.Id} reads {now} after restore, expected {original}.");
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                errors.Add($"{p.Id}: {e.Message}");
            }
        }

        IReadOnlyDictionary<string, SettingValue>? after = SafeRead(opt);
        if (errors.Count > 0)
        {
            steps.AddRange(errors);
            // Keep the backup so the user can retry.
            return Finish(id, OperationAction.Restore, OperationOutcome.Failed, "Restore did not complete: " + string.Join(" ", errors), steps, before, after, detail: string.Join(" ", errors));
        }

        _backups.Delete(id);
        string message = opt.Definition.RequiresRestart
            ? "Original setting restored and verified. Restart Windows for it to take effect."
            : "Original setting restored and verified.";
        return Finish(id, OperationAction.Restore, OperationOutcome.Succeeded, message, steps, before, after, restartRequired: opt.Definition.RequiresRestart);
    }

    private static string Rollback(List<ISettingPrimitive> applied, IReadOnlyDictionary<string, SettingValue> before, List<string> steps)
    {
        var failures = new List<string>();
        for (int i = applied.Count - 1; i >= 0; i--)
        {
            ISettingPrimitive p = applied[i];
            try
            {
                p.Restore(before[p.Id]);
                steps.Add($"Rolled back {p.Id}.");
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                failures.Add($"{p.Id}: {e.Message}");
            }
        }
        if (failures.Count == 0) return "Rollback completed.";
        steps.Add("Rollback incomplete: " + string.Join("; ", failures));
        return "WARNING: rollback incomplete — " + string.Join("; ", failures) + ". Use Restore Center to retry.";
    }

    private bool IsRestartPending(string id, SystemContext ctx)
    {
        OperationRecord? last = _history.LatestSuccessful(id);
        return last is not null && ctx.LastBootTime is { } boot && last.Timestamp > boot;
    }

    private OperationResult Finish(
        string id, OperationAction action, OperationOutcome outcome, string message, List<string> steps,
        IReadOnlyDictionary<string, SettingValue>? before, IReadOnlyDictionary<string, SettingValue>? after,
        Exception? error = null, bool restartRequired = false, string? detail = null, bool log = true)
    {
        string? errorDetail = detail ?? (error is null ? null : $"{error.GetType().Name}: {error.Message} (0x{error.HResult:X8})");
        if (error is not null) steps.Add("Error: " + errorDetail);

        var result = new OperationResult(id, outcome, message, steps, restartRequired, errorDetail);
        bool success = result.Success && outcome == OperationOutcome.Succeeded;

        if (log)
        {
            _history.Append(new OperationRecord(0, id, action, _clock.UtcNow, success, message,
                before ?? new Dictionary<string, SettingValue>(), after ?? new Dictionary<string, SettingValue>()));
        }

        _log.Log(success || result.Success ? LogLevel.Information : LogLevel.Warning,
            $"optimization.{action.ToString().ToLowerInvariant()}", message,
            new Dictionary<string, object?> { ["id"] = id, ["outcome"] = outcome.ToString(), ["error"] = errorDetail },
            error);
        return result;
    }

    private static IReadOnlyDictionary<string, SettingValue>? SafeRead(Optimization opt)
    {
        try { return opt.ReadCurrent(); }
        catch (Exception e) when (IsRecoverable(e)) { return null; }
    }

    private static string Describe(IReadOnlyDictionary<string, SettingValue> values) =>
        string.Join("; ", values.Select(kv => $"{kv.Key} = {kv.Value}"));

    private static bool IsRecoverable(Exception e) =>
        e is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
