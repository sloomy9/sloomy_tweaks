using System.Collections.ObjectModel;
using SloomyTweaks.Core.Engine;
using SloomyTweaks.Core.Hardware;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Presentation.Mvvm;
using SloomyTweaks.Presentation.Services;

namespace SloomyTweaks.Presentation.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    private bool _isLoading;
    private string? _errorMessage;

    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public bool IsLoading { get => _isLoading; protected set => SetProperty(ref _isLoading, value); }
    public string? ErrorMessage { get => _errorMessage; protected set => SetProperty(ref _errorMessage, value); }

    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
    public virtual void OnNavigatedFrom() { }

    protected async Task RunSafeAsync(Func<Task> work)
    {
        IsLoading = true;
        ErrorMessage = null;
        try { await work(); }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorMessage = e.Message; }
        finally { IsLoading = false; }
    }
}

public sealed record InfoRow(string Label, string Value);

/// <summary>
/// A category page: system facts relevant to the category plus its optimization cards
/// (actionable first, then guidance, then items Sloomy Tweaks deliberately does not offer).
/// </summary>
public sealed class CategoryPageViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly Func<Optimization, bool> _filter;
    private readonly Func<AppServices, IReadOnlyList<InfoRow>>? _info;
    private readonly Func<bool> _hasPremium;
    private IReadOnlyList<OptimizationCardViewModel> _cards = Array.Empty<OptimizationCardViewModel>();
    private IReadOnlyList<InfoRow> _infoRows = Array.Empty<InfoRow>();

    public CategoryPageViewModel(string title, string subtitle, AppServices services, Func<Optimization, bool> filter,
        Func<bool> hasPremium, Func<AppServices, IReadOnlyList<InfoRow>>? info = null, string? emptyMessage = null)
    {
        Title = title;
        Subtitle = subtitle;
        _services = services;
        _filter = filter;
        _hasPremium = hasPremium;
        _info = info;
        EmptyMessage = emptyMessage ?? "No optimizations in this category currently meet Sloomy Tweaks' evidence requirements.";
        RefreshCommand = new AsyncRelayCommand(OnNavigatedToAsync);
    }

    public override string Title { get; }
    public override string Subtitle { get; }
    public string EmptyMessage { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public IReadOnlyList<OptimizationCardViewModel> Cards { get => _cards; private set { SetProperty(ref _cards, value); OnPropertyChanged(nameof(HasCards)); } }
    public bool HasCards => Cards.Count > 0;
    public IReadOnlyList<InfoRow> InfoRows { get => _infoRows; private set { SetProperty(ref _infoRows, value); OnPropertyChanged(nameof(HasInfo)); } }
    public bool HasInfo => InfoRows.Count > 0;

    public override Task OnNavigatedToAsync() => RunSafeAsync(async () =>
    {
        var (statuses, info) = await Task.Run(() =>
        {
            var list = _services.Engine.Catalog.Where(_filter)
                .OrderBy(o => o.Definition.Kind)
                .ThenBy(o => o.Definition.Evidence)
                .ThenBy(o => o.Definition.Name, StringComparer.CurrentCulture)
                .Select(o => _services.Engine.GetStatus(o.Id))
                .ToList();
            return (list, _info?.Invoke(_services) ?? Array.Empty<InfoRow>());
        });
        Cards = statuses.Select(s => new OptimizationCardViewModel(s, _services, _hasPremium)).ToList();
        InfoRows = info;
    });

    public void RefreshLicenseState()
    {
        foreach (var c in Cards) c.RaiseCommandStates();
    }
}

public sealed class LockedPageViewModel : PageViewModel
{
    public LockedPageViewModel(string title, Action goToLicense)
    {
        Title = title;
        GoToLicenseCommand = new RelayCommand(goToLicense);
    }

    public override string Title { get; }
    public override string Subtitle => "This section requires an active Sloomy Tweaks license.";
    public RelayCommand GoToLicenseCommand { get; }
}

public sealed class HistoryViewModel : PageViewModel
{
    private readonly AppServices _services;
    private IReadOnlyList<HistoryItem> _items = Array.Empty<HistoryItem>();

    public HistoryViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(OnNavigatedToAsync);
    }

    public override string Title => "Optimization History";
    public override string Subtitle => "Every change Sloomy Tweaks attempted, with before and after values.";
    public AsyncRelayCommand RefreshCommand { get; }
    public IReadOnlyList<HistoryItem> Items { get => _items; private set { SetProperty(ref _items, value); OnPropertyChanged(nameof(IsEmpty)); } }
    public bool IsEmpty => Items.Count == 0;

    public override Task OnNavigatedToAsync() => RunSafeAsync(async () =>
    {
        var names = _services.Engine.Catalog.ToDictionary(o => o.Id, o => o.Definition.Name);
        var records = await Task.Run(() => _services.History.List(500));
        Items = records.Select(r => new HistoryItem(
            r.Timestamp.LocalDateTime.ToString("g"),
            names.GetValueOrDefault(r.OptimizationId, r.OptimizationId),
            r.Action.ToString(),
            r.Success ? "Succeeded" : "Not completed",
            r.Success,
            r.Message,
            string.Join(Environment.NewLine, r.Before.Select(kv => $"{kv.Key} = {kv.Value}")),
            string.Join(Environment.NewLine, r.After.Select(kv => $"{kv.Key} = {kv.Value}")))).ToList();
    });
}

public sealed record HistoryItem(string When, string Optimization, string Action, string Outcome, bool Success, string Message, string Before, string After);

/// <summary>Restore Center: always available, even without a license, so users can undo every change.</summary>
public sealed class RestoreCenterViewModel : PageViewModel
{
    private readonly AppServices _services;
    private IReadOnlyList<RestoreItemViewModel> _items = Array.Empty<RestoreItemViewModel>();
    private string? _statusMessage;

    public RestoreCenterViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(OnNavigatedToAsync);
        RestoreAllCommand = new AsyncRelayCommand(RestoreAllAsync, () => Items.Count > 0);
    }

    public override string Title => "Restore Center";
    public override string Subtitle => "Original values saved by Sloomy Tweaks before each change. Restore them individually or all at once.";
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RestoreAllCommand { get; }
    public IReadOnlyList<RestoreItemViewModel> Items
    {
        get => _items;
        private set { SetProperty(ref _items, value); OnPropertyChanged(nameof(IsEmpty)); RestoreAllCommand.RaiseCanExecuteChanged(); }
    }
    public bool IsEmpty => Items.Count == 0;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public override Task OnNavigatedToAsync() => RunSafeAsync(async () =>
    {
        var backups = await Task.Run(() => _services.Backups.List());
        Items = backups.Select(b =>
        {
            Optimization? opt = _services.Engine.Catalog.FirstOrDefault(o => o.Id == b.OptimizationId);
            return new RestoreItemViewModel(b, opt?.Definition.Name ?? b.OptimizationId, opt?.Definition.RequiresAdministrator == true, this);
        }).ToList();
    });

    internal async Task RestoreOneAsync(RestoreItemViewModel item)
    {
        OperationResult r = await _services.Engine.RestoreAsync(item.OptimizationId);
        StatusMessage = $"{item.Name}: {r.Message}";
        await OnNavigatedToAsync();
    }

    private async Task RestoreAllAsync()
    {
        if (!await _services.Dialogs.ConfirmAsync("Restore all", $"Restore the original values of {Items.Count} optimization(s)?", "Restore all")) return;
        var messages = new List<string>();
        foreach (RestoreItemViewModel item in Items.ToList())
        {
            OperationResult r = await _services.Engine.RestoreAsync(item.OptimizationId);
            messages.Add($"{item.Name}: {(r.Success ? "restored" : r.Message)}");
        }
        StatusMessage = string.Join(Environment.NewLine, messages);
        await OnNavigatedToAsync();
    }
}

public sealed class RestoreItemViewModel
{
    public RestoreItemViewModel(OptimizationBackup backup, string name, bool requiresAdmin, RestoreCenterViewModel owner)
    {
        OptimizationId = backup.OptimizationId;
        Name = name;
        SavedAt = backup.CreatedAt.LocalDateTime.ToString("g");
        Values = string.Join(Environment.NewLine, backup.OriginalValues.Select(kv => $"{kv.Key} = {kv.Value}"));
        RequiresAdministrator = requiresAdmin;
        RestoreCommand = new AsyncRelayCommand(() => owner.RestoreOneAsync(this));
    }

    public string OptimizationId { get; }
    public string Name { get; }
    public string SavedAt { get; }
    public string Values { get; }
    public bool RequiresAdministrator { get; }
    public AsyncRelayCommand RestoreCommand { get; }
}

/// <summary>Standard category definitions shared by the sidebar.</summary>
public static class CategoryInfo
{
    public static IReadOnlyList<InfoRow> Cpu(AppServices s)
    {
        SystemContext c = s.Hardware.GetContext();
        var rows = new List<InfoRow>
        {
            new("Processor", c.CpuName),
            new("Manufacturer", c.CpuVendor.ToString()),
            new("Cores / threads", $"{c.PhysicalCores?.ToString() ?? "Unknown"} / {c.LogicalProcessors}"),
            new("Hybrid architecture", c.IsHybridCpu switch { true => "Yes (performance + efficiency cores)", false => "No", _ => "Unknown" }),
        };
        if (c.IsAmdX3D) rows.Add(new("3D V-Cache", "Detected. AMD's driver relies on Game Mode / Game Bar game detection and the Balanced plan."));
        rows.Add(new("Policy", "Sloomy Tweaks never disables SMT, C-states, CPPC, boost or security mitigations automatically."));
        return rows;
    }

    public static IReadOnlyList<InfoRow> Gpu(AppServices s)
    {
        var gpus = s.Hardware.GetSnapshot().Gpus;
        if (gpus.Count == 0) return new[] { new InfoRow("Display adapters", "None detected") };
        return gpus.SelectMany(g => new[]
        {
            new InfoRow("Adapter", g.Name),
            new InfoRow("Driver version", g.DriverVersion ?? "Unknown"),
            new InfoRow("Dedicated memory", g.DedicatedMemoryBytes is { } m ? ByteFormat.ToGiB(m) : "Unknown"),
        }).ToList();
    }

    public static IReadOnlyList<InfoRow> Memory(AppServices s)
    {
        UsageSample u = s.Usage.Sample();
        return new[]
        {
            new InfoRow("Installed memory", ByteFormat.ToGiB(s.Hardware.GetSnapshot().TotalPhysicalMemoryBytes)),
            new InfoRow("In use", $"{ByteFormat.ToGiB(u.MemoryUsedBytes)} ({u.MemoryPercent:0}%)"),
            new InfoRow("Policy", "No RAM 'cleaning', standby purging or page-file removal. Windows memory management is left intact."),
        };
    }

    public static IReadOnlyList<InfoRow> Storage(AppServices _)
    {
        var rows = new List<InfoRow>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                rows.Add(new InfoRow($"{d.Name} ({d.DriveFormat})", $"{ByteFormat.ToGiB((ulong)d.AvailableFreeSpace)} free of {ByteFormat.ToGiB((ulong)d.TotalSize)}"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (rows.Count == 0) rows.Add(new InfoRow("Drives", "No fixed drives reported"));
        return rows;
    }

    public static IReadOnlyList<InfoRow> Power(AppServices s) => new[]
    {
        new InfoRow("Active power plan", s.Hardware.GetSnapshot().ActivePowerPlan ?? "Unknown"),
        new InfoRow("Note", "Higher-performance plans are not universally better. Measure before keeping a change."),
    };
}
