using System.Globalization;
using SloomyTweaks.Core.Diagnostics;
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

/// <summary>A label/value line of real system information. Severity drives the colour in the UI.</summary>
public sealed record InfoRow(string Label, string Value, DiagnosticSeverity Severity = DiagnosticSeverity.Info)
{
    public static InfoRow From(DiagnosticRow r) => new(r.Label, r.Value, r.Severity);
    public bool IsWarning => Severity == DiagnosticSeverity.Warning;
    public bool IsGood => Severity == DiagnosticSeverity.Good;
}

public sealed record InfoSection(string Title, IReadOnlyList<InfoRow> Rows);

public sealed record SettingsLink(string Title, string Uri, RelayCommand OpenCommand);

public sealed record NotOfferedItem(string Name, string Reason);

/// <summary>Everything a category page shows besides the optimization cards.</summary>
public sealed record CategoryContent(IReadOnlyList<InfoSection> Sections, IReadOnlyList<(string Title, string Uri)> Links);

/// <summary>
/// A category page: diagnostics (information, never changes), optimization cards with verified
/// toggles or selectors, guidance cards, and a compact list of what Sloomy Tweaks does not offer and why.
/// </summary>
public sealed class CategoryPageViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly Func<Optimization, bool> _filter;
    private readonly Func<AppServices, CategoryContent>? _content;
    private readonly Func<bool> _hasPremium;
    private IReadOnlyList<OptimizationCardViewModel> _cards = Array.Empty<OptimizationCardViewModel>();
    private IReadOnlyList<InfoSection> _sections = Array.Empty<InfoSection>();
    private IReadOnlyList<SettingsLink> _links = Array.Empty<SettingsLink>();
    private IReadOnlyList<NotOfferedItem> _notOffered = Array.Empty<NotOfferedItem>();
    private bool _showNotOffered;

    public CategoryPageViewModel(string title, string subtitle, AppServices services, Func<Optimization, bool> filter,
        Func<bool> hasPremium, Func<AppServices, CategoryContent>? content = null, string? emptyMessage = null)
    {
        Title = title;
        Subtitle = subtitle;
        _services = services;
        _filter = filter;
        _hasPremium = hasPremium;
        _content = content;
        EmptyMessage = emptyMessage ?? "No optimizations in this category currently meet Sloomy Tweaks' evidence requirements.";
        RefreshCommand = new AsyncRelayCommand(OnNavigatedToAsync);
        ToggleNotOfferedCommand = new RelayCommand(() => ShowNotOffered = !ShowNotOffered);
    }

    public override string Title { get; }
    public override string Subtitle { get; }
    public string EmptyMessage { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ToggleNotOfferedCommand { get; }

    public IReadOnlyList<OptimizationCardViewModel> Cards { get => _cards; private set { SetProperty(ref _cards, value); OnPropertyChanged(nameof(HasCards)); } }
    public bool HasCards => Cards.Count > 0;
    public IReadOnlyList<InfoSection> Sections { get => _sections; private set { SetProperty(ref _sections, value); OnPropertyChanged(nameof(HasSections)); } }
    public bool HasSections => Sections.Count > 0;
    public IReadOnlyList<SettingsLink> Links { get => _links; private set { SetProperty(ref _links, value); OnPropertyChanged(nameof(HasLinks)); } }
    public bool HasLinks => Links.Count > 0;
    public IReadOnlyList<NotOfferedItem> NotOffered { get => _notOffered; private set { SetProperty(ref _notOffered, value); OnPropertyChanged(nameof(HasNotOffered)); } }
    public bool HasNotOffered => NotOffered.Count > 0;
    public bool ShowNotOffered { get => _showNotOffered; set => SetProperty(ref _showNotOffered, value); }

    public override Task OnNavigatedToAsync() => RunSafeAsync(async () =>
    {
        var (statuses, notOffered, content) = await Task.Run(() =>
        {
            var items = _services.Engine.Catalog.Where(_filter).ToList();
            var cards = items.Where(o => o.Definition.Kind != OptimizationKind.NotOffered)
                .OrderBy(o => o.Definition.Kind switch { OptimizationKind.Actionable => 0, OptimizationKind.Selector => 1, _ => 2 })
                .ThenBy(o => o.Definition.Evidence)
                .ThenBy(o => o.Definition.Name, StringComparer.CurrentCulture)
                .Select(o => _services.Engine.GetStatus(o.Id))
                .ToList();
            var excluded = items.Where(o => o.Definition.Kind == OptimizationKind.NotOffered)
                .Select(o => new NotOfferedItem(o.Definition.Name, o.Definition.TechnicalMechanism)).ToList();
            return (cards, excluded, _content?.Invoke(_services) ?? new CategoryContent(Array.Empty<InfoSection>(), Array.Empty<(string, string)>()));
        });
        Cards = statuses.Select(s => new OptimizationCardViewModel(s, _services, _hasPremium)).ToList();
        NotOffered = notOffered;
        Sections = content.Sections;
        Links = content.Links.Select(l => new SettingsLink(l.Title, l.Uri, new RelayCommand(() => _services.Dialogs.OpenWindowsSettings(l.Uri)))).ToList();
    });

    public void RefreshLicenseState()
    {
        foreach (var c in Cards) c.RaiseCommandStates();
    }
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

    public override string Title => "Optimization history";
    public override string Subtitle => "Every change Sloomy Tweaks attempted, with before and after values.";
    public AsyncRelayCommand RefreshCommand { get; }
    public IReadOnlyList<HistoryItem> Items { get => _items; private set { SetProperty(ref _items, value); OnPropertyChanged(nameof(IsEmpty)); } }
    public bool IsEmpty => Items.Count == 0;

    public override Task OnNavigatedToAsync() => RunSafeAsync(async () =>
    {
        var names = _services.Engine.Catalog.ToDictionary(o => o.Id, o => o.Definition.Name);
        var records = await Task.Run(() => _services.History.List(500));
        Items = records.Select(r => new HistoryItem(
            r.Timestamp.LocalDateTime.ToString("g", CultureInfo.CurrentCulture),
            names.GetValueOrDefault(r.OptimizationId, r.OptimizationId),
            r.Action == OperationAction.Apply ? "Turned on / changed" : "Restored",
            r.Success ? "Succeeded" : "Not completed",
            r.Success,
            r.Message,
            string.Join(Environment.NewLine, r.Before.Select(kv => $"{kv.Key} = {kv.Value}")),
            string.Join(Environment.NewLine, r.After.Select(kv => $"{kv.Key} = {kv.Value}")))).ToList();
    });
}

public sealed record HistoryItem(string When, string Optimization, string Action, string Outcome, bool Success, string Message, string Before, string After);

/// <summary>
/// Restore Center: the saved originals of every change, restorable individually or all at once, plus the
/// operation history. Also reachable from the activation screen so an expired license never traps changes.
/// </summary>
public sealed class RestoreCenterViewModel : PageViewModel
{
    private readonly AppServices _services;
    private IReadOnlyList<RestoreItemViewModel> _items = Array.Empty<RestoreItemViewModel>();
    private string? _statusMessage;

    public RestoreCenterViewModel(AppServices services)
    {
        _services = services;
        History = new HistoryViewModel(services);
        RefreshCommand = new AsyncRelayCommand(OnNavigatedToAsync);
        RestoreAllCommand = new AsyncRelayCommand(RestoreAllAsync, () => Items.Count > 0);
    }

    public override string Title => "Restore Center";
    public override string Subtitle => "Original values saved before each change. Restore them individually or all at once.";
    public HistoryViewModel History { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RestoreAllCommand { get; }
    public IReadOnlyList<RestoreItemViewModel> Items
    {
        get => _items;
        private set { SetProperty(ref _items, value); OnPropertyChanged(nameof(IsEmpty)); RestoreAllCommand.RaiseCanExecuteChanged(); }
    }
    public bool IsEmpty => Items.Count == 0;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public override async Task OnNavigatedToAsync()
    {
        await RunSafeAsync(async () =>
        {
            var backups = await Task.Run(() => _services.Backups.List());
            Items = backups.Select(b =>
            {
                Optimization? opt = _services.Engine.Catalog.FirstOrDefault(o => o.Id == b.OptimizationId);
                bool admin = opt?.Definition.RequiresAdministrator == true;
                return new RestoreItemViewModel(b, opt?.Definition.Name ?? b.OptimizationId, admin, admin && !_services.App.IsElevated, this);
            }).ToList();
        });
        await History.OnNavigatedToAsync();
    }

    internal async Task RestoreOneAsync(RestoreItemViewModel item)
    {
        OperationResult r = await _services.Engine.RestoreAsync(item.OptimizationId);
        StatusMessage = $"{item.Name}: {r.Message}";
        await OnNavigatedToAsync();
    }

    private async Task RestoreAllAsync()
    {
        if (!await _services.Dialogs.ConfirmAsync("Restore all", $"Restore the original values of {Items.Count} change(s)?", "Restore all")) return;
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
    public RestoreItemViewModel(OptimizationBackup backup, string name, bool requiresAdmin, bool blockedByElevation, RestoreCenterViewModel owner)
    {
        OptimizationId = backup.OptimizationId;
        Name = name;
        SavedAt = backup.CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
        Values = string.Join(Environment.NewLine, backup.OriginalValues.Select(kv => $"{kv.Key} = {kv.Value}"));
        RequiresAdministrator = requiresAdmin;
        BlockedByElevation = blockedByElevation;
        RestoreCommand = new AsyncRelayCommand(() => owner.RestoreOneAsync(this), () => !blockedByElevation);
    }

    public string OptimizationId { get; }
    public string Name { get; }
    public string SavedAt { get; }
    public string Values { get; }
    public bool RequiresAdministrator { get; }
    public bool BlockedByElevation { get; }
    public AsyncRelayCommand RestoreCommand { get; }
}

/// <summary>Diagnostics and Windows Settings shortcuts for each category page. Read-only.</summary>
public static class CategoryInfo
{
    private static IReadOnlyList<InfoRow> Rows(IEnumerable<DiagnosticRow> rows) => rows.Select(InfoRow.From).ToList();

    private static IReadOnlyList<InfoRow> Safe(Func<IReadOnlyList<InfoRow>> read, string label)
    {
        try { return read(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return new[] { new InfoRow(label, "Unavailable: " + e.Message, DiagnosticSeverity.Warning) }; }
    }

    public static CategoryContent Cpu(AppServices s)
    {
        SystemContext c = s.Hardware.GetContext();
        var processor = new List<InfoRow>
        {
            new("Processor", c.CpuName),
            new("Manufacturer", c.CpuVendor.ToString()),
            new("Cores / threads", $"{c.PhysicalCores?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"} / {c.LogicalProcessors}"),
            new("Hybrid architecture", c.IsHybridCpu switch { true => "Yes (performance + efficiency cores)", false => "No", _ => "Unknown" }),
        };
        if (c.IsAmdX3D) processor.Add(new InfoRow("3D V-Cache", "Detected. AMD's driver relies on Game Mode / Game Bar detection and the Balanced plan.", DiagnosticSeverity.Warning));
        return new CategoryContent(new[]
        {
            new InfoSection("Processor", processor),
            new InfoSection("Power policy of the active plan", Safe(() => Rows(s.Diagnostics.ProcessorPowerPolicy()), "Power policy")),
        }, new[] { ("Power settings", WindowsSettingsLinks.Power) });
    }

    public static CategoryContent Gpu(AppServices s)
    {
        var gpus = s.Hardware.GetSnapshot().Gpus;
        IReadOnlyList<InfoRow> adapters = gpus.Count == 0
            ? new[] { new InfoRow("Display adapters", "None detected", DiagnosticSeverity.Warning) }
            : gpus.SelectMany(g => new[]
            {
                new InfoRow("Adapter", g.Name),
                new InfoRow("Driver version", g.DriverVersion ?? "Unknown"),
                new InfoRow("Dedicated memory", g.DedicatedMemoryBytes is { } m ? ByteFormat.ToGiB(m) : "Unknown"),
            }).ToList();
        return new CategoryContent(new[]
        {
            new InfoSection("Graphics adapters", adapters),
            new InfoSection("Displays", Displays(s)),
        }, new[] { ("Graphics settings", WindowsSettingsLinks.Graphics), ("Display settings", WindowsSettingsLinks.Display) });
    }

    public static CategoryContent Memory(AppServices s)
    {
        UsageSample u = s.Usage.Sample();
        var usage = new List<InfoRow>
        {
            new("Installed memory", ByteFormat.ToGiB(s.Hardware.GetSnapshot().TotalPhysicalMemoryBytes)),
            new("In use", $"{ByteFormat.ToGiB(u.MemoryUsedBytes)} ({u.MemoryPercent:0}%)", u.MemoryPercent >= 90 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info),
        };
        return new CategoryContent(new[]
        {
            new InfoSection("Memory", usage),
            new InfoSection("Commit and page file", Safe(() => Rows(s.Diagnostics.Memory()), "Memory diagnostics")),
        }, Array.Empty<(string, string)>());
    }

    public static CategoryContent Windows(AppServices s) => new(Array.Empty<InfoSection>(), new[]
    {
        ("Game Mode", WindowsSettingsLinks.GameMode),
        ("Captures", WindowsSettingsLinks.Captures),
        ("Graphics", WindowsSettingsLinks.Graphics),
        ("Startup apps", WindowsSettingsLinks.StartupApps),
    });

    public static CategoryContent Gaming(AppServices s) => new(new[]
    {
        new InfoSection("Displays (for frame-rate caps and VRR)", Displays(s)),
        new InfoSection("Fortnite configuration (read-only)", Safe(() => Rows(FortniteSettingsReader.Parse(s.Diagnostics.ReadFortniteSettings())), "Fortnite settings")),
    }, new[] { ("Game Mode", WindowsSettingsLinks.GameMode), ("Graphics", WindowsSettingsLinks.Graphics) });

    public static CategoryContent Input(AppServices s) => new(new[]
    {
        new InfoSection("Pointer", Safe(() => new[]
        {
            new InfoRow("Pointer speed", s.Diagnostics.PointerSpeed() is { } v ? $"{v} of 20 (Windows default is 10)" : "Unknown"),
            new InfoRow("Polling rate", "Set in your mouse's own software. Windows does not report it, so Sloomy Tweaks does not show a value."),
        }, "Pointer")),
    }, new[] { ("Mouse settings", WindowsSettingsLinks.Mouse), ("Keyboard accessibility", WindowsSettingsLinks.Keyboard) });

    public static CategoryContent Storage(AppServices s)
    {
        var rows = new List<InfoRow>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                double freePct = d.TotalSize == 0 ? 0 : d.AvailableFreeSpace * 100.0 / d.TotalSize;
                rows.Add(new InfoRow($"{d.Name} ({d.DriveFormat})", $"{ByteFormat.ToGiB((ulong)d.AvailableFreeSpace)} free of {ByteFormat.ToGiB((ulong)d.TotalSize)}",
                    freePct < 10 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (rows.Count == 0) rows.Add(new InfoRow("Drives", "No fixed drives reported"));
        return new CategoryContent(new[]
        {
            new InfoSection("Drives", rows),
            new InfoSection("TRIM", Safe(() => new[] { InfoRow.From(s.Diagnostics.TrimStatus()) }, "TRIM")),
        }, new[] { ("Storage settings", WindowsSettingsLinks.Storage) });
    }

    public static CategoryContent Power(AppServices s) => new(new[]
    {
        new InfoSection("Active plan", new[] { new InfoRow("Power plan", s.Hardware.GetSnapshot().ActivePowerPlan ?? "Unknown") }),
        new InfoSection("Processor power policy", Safe(() => Rows(s.Diagnostics.ProcessorPowerPolicy()), "Power policy")),
    }, new[] { ("Power settings", WindowsSettingsLinks.Power) });

    private static IReadOnlyList<InfoRow> Displays(AppServices s) => Safe(() =>
    {
        var displays = s.Diagnostics.Displays();
        return displays.Count == 0
            ? new[] { new InfoRow("Displays", "None reported") }
            : displays.Select(d => new InfoRow(d.Name, $"{d.Width} × {d.Height} at {d.RefreshRateHz} Hz")).ToList();
    }, "Displays");
}
