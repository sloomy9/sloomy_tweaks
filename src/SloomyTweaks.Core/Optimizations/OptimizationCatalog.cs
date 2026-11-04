using SloomyTweaks.Core.Hardware;
using SloomyTweaks.Core.Platform;

namespace SloomyTweaks.Core.Optimizations;

/// <summary>
/// The Sloomy Tweaks evidence registry. Every entry states its mechanism, evidence level,
/// sources, risks and what is still unknown. Entries are intentionally few: an optimization is
/// added only when its mechanism is documented and it can be detected, verified and reversed.
///
/// Source policy: every URL below was checked while writing this registry. Where only
/// community documentation exists for a mechanism (for example a registry location behind a
/// Settings toggle), the source is labelled CommunityDocumentation so the card says so.
/// </summary>
public static class OptimizationCatalog
{
    public static class Ids
    {
        public const string GameMode = "windows.game-mode";
        public const string BackgroundRecording = "windows.background-recording-off";
        public const string Hags = "gpu.hardware-accelerated-gpu-scheduling";
        public const string WindowedGameOptimizations = "gpu.windowed-game-optimizations";
        public const string NvidiaReflex = "gpu.nvidia-reflex-guidance";
        public const string PointerPrecision = "input.enhance-pointer-precision-off";
        public const string PowerPlan = "power.active-plan";
        public const string UsbSelectiveSuspend = "power.usb-selective-suspend-off";
        public const string AccessibilityShortcuts = "input.accessibility-shortcuts-off";
        public const string Trim = "storage.trim-enabled";
        public const string FortniteProfile = "gaming.fortnite-profile-guidance";
        public const string NoPageFileDisable = "memory.disable-page-file";
        public const string NoRamCleaner = "memory.standby-list-cleaning";
        public const string NoMitigationDisable = "cpu.disable-security-mitigations";
        public const string NoCoreParkingTweaks = "cpu.disable-core-parking-smt-cstates";
        public const string NoTimerTweaks = "input.timer-resolution-hpet";
        public const string NoTcpTweaks = "network.tcp-registry-tweaks";
        public const string NoDnsPing = "network.dns-for-ping";
        public const string NoServiceLists = "services.generic-disable-lists";
    }

    public static readonly Guid HighPerformanceSchemeGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid BalancedSchemeGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid UltimatePerformanceSchemeGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid UsbSubgroupGuid = new("2a737441-1930-4402-8d77-b2bebba308a3");
    public static readonly Guid UsbSelectiveSuspendGuid = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    // ---- Sources (checked) -------------------------------------------------------------------
    private static readonly EvidenceSource MsLearnGamingFeatures = new(
        "Microsoft Learn — Performance, capture, and system-level gaming features",
        "https://learn.microsoft.com/en-us/training/modules/optimize-windows-gaming/4-performance-gaming-features",
        SourceKind.OfficialDocumentation);

    private static readonly EvidenceSource GameModeRegistry = new(
        "ElevenForum — Turn On or Off Game Mode in Windows 11 (registry location)",
        "https://www.elevenforum.com/t/turn-on-or-off-game-mode-in-windows-11.1447/",
        SourceKind.CommunityDocumentation,
        "Documents HKCU\\Software\\Microsoft\\GameBar\\AutoGameModeEnabled. Not a Microsoft source.");

    private static readonly EvidenceSource X3DDriverBehavior = new(
        "HWCooling — Ryzen 7000X3D technical details and asymmetric CCX handling",
        "https://www.hwcooling.net/en/ryzen-7000x3d-technical-details-and-asymmetric-ccx-handling/",
        SourceKind.TechnicalReporting,
        "Explains that AMD's driver relies on Windows Game Mode / Xbox Game Bar game detection to prefer V-Cache cores.");

    private static readonly EvidenceSource MsBackgroundRecording = new(
        "Microsoft Support — Capture and share game clips (background recording)",
        "https://support.microsoft.com/en-us/help/4027144",
        SourceKind.OfficialDocumentation,
        "States that background recording can affect game performance.");

    private static readonly EvidenceSource BackgroundRecordingRegistry = new(
        "ElevenForum — Enable or Disable Record What Happened for Gaming Captures",
        "https://www.elevenforum.com/t/enable-or-disable-record-what-happened-for-gaming-captures-in-windows-11.17900",
        SourceKind.CommunityDocumentation,
        "Documents HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR\\HistoricalCaptureEnabled. Not a Microsoft source.");

    private static readonly EvidenceSource MsHagsBlog = new(
        "Microsoft DirectX Developer Blog — Hardware Accelerated GPU Scheduling",
        "https://devblogs.microsoft.com/directx/hardware-accelerated-gpu-scheduling/",
        SourceKind.OfficialDocumentation);

    private static readonly EvidenceSource GamersNexusHags = new(
        "GamersNexus — Hardware-Accelerated GPU Scheduling Benchmarks (frametimes, FPS)",
        "https://gamersnexus.net/guides/3599-windows-10-hardware-accelerated-gpu-scheduling-benchmarks",
        SourceKind.IndependentMeasurement,
        "2020 measurements on Windows 10 2004; results may differ on current drivers and Windows 11.");

    private static readonly EvidenceSource MsWindowedGames = new(
        "Microsoft Support — Optimizations for windowed games in Windows 11",
        "https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952",
        SourceKind.OfficialDocumentation);

    private static readonly EvidenceSource WindowedGamesRegistry = new(
        "ElevenForum — Turn On or Off Optimizations for Windowed Games (registry location)",
        "https://elevenforum.com/t/turn-on-or-off-optimizations-for-windowed-games-in-windows-11.4943/",
        SourceKind.CommunityDocumentation,
        "Documents the DirectXUserGlobalSettings value. Not a Microsoft source.");

    private static readonly EvidenceSource TechSpotReflex = new(
        "TechSpot — Nvidia Reflex tested with LDAT (includes Fortnite)",
        "https://www.techspot.com/article/2123-nvidia-reflex-rtx/",
        SourceKind.IndependentMeasurement,
        "Hardware photodetector (LDAT) measurements. Also notes driver low-latency modes do not apply to DirectX 12 titles.");

    private static readonly EvidenceSource MsAccessibilityParams = new(
        "Microsoft Learn — Accessibility parameters (SPI_GETMOUSE / SPI_SETMOUSE)",
        "https://learn.microsoft.com/en-us/windows/win32/winauto/accessibility-parameters",
        SourceKind.OfficialDocumentation);

    private static readonly EvidenceSource MsPowerSetActiveScheme = new(
        "Microsoft Learn — PowerSetActiveScheme function",
        "https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powersetactivescheme",
        SourceKind.OfficialDocumentation);

    private static readonly EvidenceSource MsPageFile = new(
        "Microsoft Docs — How to determine the appropriate page file size for 64-bit versions of Windows",
        "https://github.com/MicrosoftDocs/windows-itpro-docs/blob/master/windows/client-management/determine-appropriate-page-file-size.md",
        SourceKind.OfficialDocumentation);

    private static readonly EvidenceSource MsStickyKeys = new(
        "Microsoft Learn — STICKYKEYS structure (SKF_HOTKEYACTIVE)",
        "https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-stickykeys",
        SourceKind.OfficialDocumentation);

    private static readonly EvidenceSource MsFsutilBehavior = new(
        "Microsoft Learn — fsutil behavior (disabledeletenotify / TRIM)",
        "https://learn.microsoft.com/windows-server/administration/windows-commands/fsutil-behavior",
        SourceKind.OfficialDocumentation,
        "TRIM is enabled by default on NTFS; enabling or disabling does not require a restart.");

    private static readonly EvidenceSource UsbSuspendGuide = new(
        "ElevenForum — Enable or Disable USB Selective Suspend in Windows 11 (power setting GUIDs)",
        "https://elevenforum.com/t/enable-or-disable-usb-selective-suspend-in-windows-11.10251/",
        SourceKind.CommunityDocumentation,
        "Documents the USB settings subgroup and selective suspend setting GUIDs used by powercfg. Not a Microsoft source.");

    // ---- Catalog -----------------------------------------------------------------------------

    public static IReadOnlyList<Optimization> Create(IRegistryAccess registry, IPowerManagement power, IMouseSettings mouse,
        IAccessibilitySettings accessibility, IFileSystemBehavior fileSystem)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(mouse);
        ArgumentNullException.ThrowIfNull(accessibility);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var list = new List<Optimization>
        {
            GameMode(registry),
            BackgroundRecording(registry),
            Hags(registry),
            WindowedGameOptimizations(registry),
            PointerPrecision(mouse),
            AccessibilityShortcuts(accessibility),
            PowerPlanSelector(power),
            UsbSelectiveSuspend(power),
            Trim(fileSystem),
            Guidance(Ids.NvidiaReflex, "Use NVIDIA Reflex in supported games", OptimizationCategory.Gpu,
                description: "Enable NVIDIA Reflex Low Latency inside games that support it (Fortnite does) instead of stacking driver-level latency settings.",
                mechanism: "Reflex is integrated into the game engine: the game paces CPU work so frames do not queue up in front of the GPU. Driver 'Low Latency Mode' limits queued frames from the driver side and, per TechSpot's testing, does not apply to DirectX 12 titles.",
                benefit: "Lower system latency in GPU-bound scenarios. Independent LDAT measurements show large reductions when the GPU is the bottleneck and little change when latency is already low or the game is CPU-bound.",
                evidence: EvidenceLevel.Verified,
                notes: "Verified for supported games on NVIDIA GPUs, GPU-bound. The size of the effect depends on the game, settings and bottleneck. Sloomy Tweaks does not change NVIDIA driver settings: there is no supported public API for doing so from a third-party application.",
                sources: new[] { TechSpotReflex },
                risks: new[] { "'On + Boost' keeps GPU clocks high and increases power and heat.", "Enabling several latency limiters at once (Reflex, driver Low Latency Mode, external caps) makes behavior harder to reason about." },
                compat: "NVIDIA GeForce GPUs in games that implement Reflex.",
                steps: new[]
                {
                    "In Fortnite: Settings > Video > NVIDIA Reflex Low Latency > On.",
                    "Leave NVIDIA Control Panel 'Low Latency Mode' at its default for that game; Reflex supersedes it while active.",
                    "Use 'On + Boost' only if measurements on your system show a benefit worth the extra power.",
                    "Measure with a tool that reports PC latency (for example NVIDIA FrameView) before and after.",
                }),
            Guidance(Ids.FortniteProfile, "Fortnite: measured, per-system configuration", OptimizationCategory.Gaming,
                description: "A checklist for configuring Fortnite with measurements instead of copied 'best settings'. Sloomy Tweaks does not edit Fortnite's configuration files in this version.",
                mechanism: "Fortnite offers several rendering modes (DirectX 11, DirectX 12, Performance). Which one gives the best frame pacing and 1% lows depends on CPU, GPU, driver and settings. Frame-rate caps, V-Sync and variable refresh rate interact with latency.",
                benefit: "Stable frame pacing and fewer stutters when the configuration matches the system's actual bottleneck. No single configuration is optimal for every PC.",
                evidence: EvidenceLevel.Documented,
                notes: "Configuration guidance only. Any improvement must be confirmed with repeated captures in the Benchmark Center.",
                sources: new[] { TechSpotReflex, MsWindowedGames },
                risks: new[] { "Editing GameUserSettings.ini while the game is running can be overwritten by the game.", "Changing rendering mode triggers shader recompilation; the first matches afterwards may stutter." },
                compat: "Fortnite on Windows 11.",
                steps: new[]
                {
                    "Record a baseline: capture at least 3 runs of the same replay segment with PresentMon and import them in Benchmark Center.",
                    "Test one change at a time (rendering mode, frame-rate cap, Reflex) and capture another 3+ runs.",
                    "After changing rendering mode or drivers, play one match first so shaders finish compiling before measuring.",
                    "With a VRR display, a frame cap slightly below the refresh rate keeps VRR active; verify on your system.",
                    "Keep the configuration only if Benchmark Center reports a statistically meaningful improvement.",
                }),

            NotOffered(Ids.NoPageFileDisable, "Disable the page file", OptimizationCategory.Memory,
                why: "Microsoft documents that page file sizing depends on crash-dump settings and peak commit charge, is unique to each system, and cannot be generalized. Removing it lowers the commit limit, which can make applications fail with out-of-memory errors even with free RAM, and prevents crash dumps.",
                sources: new[] { MsPageFile }),
            NotOffered(Ids.NoRamCleaner, "RAM cleaning / standby list purging", OptimizationCategory.Memory,
                why: "The standby list is Windows' file cache. Purging it forces data to be re-read from disk. Sloomy Tweaks found no reproducible measurements showing a general gaming benefit, so it is not offered.",
                sources: Array.Empty<EvidenceSource>()),
            NotOffered(Ids.NoMitigationDisable, "Disable CPU security mitigations (Spectre/Meltdown etc.)", OptimizationCategory.Cpu,
                why: "Disabling mitigations exposes the system to known side-channel attacks. Sloomy Tweaks never disables security protections for performance.",
                sources: Array.Empty<EvidenceSource>()),
            NotOffered(Ids.NoCoreParkingTweaks, "Globally disable core parking, SMT/Hyper-Threading or C-states", OptimizationCategory.Cpu,
                why: "Effects are hardware-specific and can reduce boost behavior or break vendor scheduling. On dual-CCD Ryzen X3D processors, AMD's driver parks cores intentionally during games. Any such change needs hardware-specific evidence, which Sloomy Tweaks does not have.",
                sources: new[] { X3DDriverBehavior }),
            NotOffered(Ids.NoTimerTweaks, "Timer resolution tools, HPET and dynamic tick changes", OptimizationCategory.InputUsb,
                why: "These change global system timing. Sloomy Tweaks found no reproducible, current measurements showing a general improvement, and it does not change global timer settings.",
                sources: Array.Empty<EvidenceSource>()),
            NotOffered(Ids.NoTcpTweaks, "TCP registry tweaks (TcpAckFrequency, TCPNoDelay, NetworkThrottlingIndex)", OptimizationCategory.Network,
                why: "Real-time game traffic generally uses UDP, so TCP acknowledgement settings do not apply to it. No reproducible measurements of in-game latency improvement were found.",
                sources: Array.Empty<EvidenceSource>()),
            NotOffered(Ids.NoDnsPing, "Change DNS to reduce game ping", OptimizationCategory.Network,
                why: "DNS is only used to resolve server names. It does not change the network path to the game server once connected.",
                sources: Array.Empty<EvidenceSource>()),
            NotOffered(Ids.NoServiceLists, "Disable services from generic internet lists", OptimizationCategory.ProcessesServices,
                why: "Service dependencies differ between systems and Windows builds. Sloomy Tweaks only suggests service changes for a specific, measured workload, and never touches Windows Update, Defender or security services.",
                sources: Array.Empty<EvidenceSource>()),
        };

        EnsureUniqueIds(list);
        return list;
    }

    private static Optimization GameMode(IRegistryAccess registry) => new(
        new OptimizationDefinition
        {
            Id = Ids.GameMode,
            Name = "Windows Game Mode",
            Category = OptimizationCategory.Windows,
            Kind = OptimizationKind.Actionable,
            Description = "Ensures Windows Game Mode is turned on for the current user.",
            TechnicalMechanism = "Game Mode lets Windows prioritize the foreground game: Windows Update does not install drivers or show restart notifications during play, and background work is deprioritized. Stored in HKCU\\Software\\Microsoft\\GameBar\\AutoGameModeEnabled (on by default when absent).",
            ExpectedBenefit = "Fewer interruptions from background activity while gaming; Microsoft describes a more consistent experience. No universal FPS gain is documented.",
            Evidence = EvidenceLevel.Documented,
            EvidenceNotes = "Behavior is documented by Microsoft; performance impact is game- and system-dependent and not quantified by Microsoft. The registry location comes from community documentation and matches the Settings toggle. On Ryzen X3D processors AMD's driver depends on Game Mode / Game Bar game detection.",
            Sources = new[] { MsLearnGamingFeatures, X3DDriverBehavior, GameModeRegistry },
            Risks = new[] { "Rare reports of specific games or tools behaving differently with Game Mode on; turn it off for that case." },
            CompatibilitySummary = "Windows 11. Per user. On LTSC, Xbox Game Bar is not installed; Game Mode itself is still available.",
            RequiresAdministrator = false,
            RequiresRestart = false,
        },
        new ISettingPrimitive[]
        {
            new RegistryDwordPrimitive(registry, RegistryRoot.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", desired: 1, valueWhenAbsent: 1),
        },
        ctx =>
        {
            var c = new CompatibilityBuilder().RequireBuild(ctx, 22000, "Sloomy Tweaks Game Mode management");
            if (ctx.IsLtsc && ctx.IsAmdX3D)
                c.Warn("LTSC does not include Xbox Game Bar. AMD's X3D driver uses Game Bar game detection, so V-Cache core preference may not work as AMD intends.");
            return c.Build();
        });

    private static Optimization BackgroundRecording(IRegistryAccess registry) => new(
        new OptimizationDefinition
        {
            Id = Ids.BackgroundRecording,
            Name = "Turn off background recording",
            Category = OptimizationCategory.Windows,
            Kind = OptimizationKind.Actionable,
            Description = "Turns off Xbox Game Bar 'Record what happened', which continuously records gameplay in the background.",
            TechnicalMechanism = "Background recording keeps a rolling video capture while a game runs, which uses encoder, memory and disk resources. Stored in HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR\\HistoricalCaptureEnabled (off when absent).",
            ExpectedBenefit = "Removes the resource cost of continuous capture. Microsoft states background recording can affect game performance; the size of the effect is not quantified.",
            Evidence = EvidenceLevel.Documented,
            EvidenceNotes = "Performance impact is stated by Microsoft without numbers. Independent measurements were not found. The registry location comes from community documentation and matches the Settings toggle.",
            Sources = new[] { MsBackgroundRecording, BackgroundRecordingRegistry },
            Risks = new[] { "You can no longer save the last moments of gameplay with Win+Alt+G until it is turned back on." },
            CompatibilitySummary = "Windows 11. Per user. Already off by default on most installations.",
        },
        new ISettingPrimitive[]
        {
            new RegistryDwordPrimitive(registry, RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", desired: 0, valueWhenAbsent: 0),
        },
        ctx => new CompatibilityBuilder().RequireBuild(ctx, 22000, "Background recording management").Build());

    private static Optimization Hags(IRegistryAccess registry) => new(
        new OptimizationDefinition
        {
            Id = Ids.Hags,
            Name = "Hardware-accelerated GPU scheduling",
            Category = OptimizationCategory.Gpu,
            Kind = OptimizationKind.Actionable,
            Description = "Turns on hardware-accelerated GPU scheduling (HAGS), the same switch as Settings > System > Display > Graphics.",
            TechnicalMechanism = "With supported GPUs and drivers, Windows offloads most GPU scheduling from a high-priority CPU thread to a dedicated scheduling processor on the GPU. Stored in HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers\\HwSchMode (2 = on, 1 = off).",
            ExpectedBenefit = "Reduces CPU-side scheduling overhead. Microsoft notes most applications already hide scheduling cost through buffering, and independent testing found no consistent FPS or frametime improvement. Treat this as a configuration choice, not a performance boost.",
            Evidence = EvidenceLevel.Documented,
            EvidenceNotes = "Mechanism documented by Microsoft. Independent measurements (2020) showed little difference. Sloomy Tweaks cannot yet query driver support directly, so compatibility is reported as unverified.",
            Sources = new[] { MsHagsBlog, GamersNexusHags },
            Risks = new[] { "Requires a restart.", "If the GPU or driver does not support HAGS, the setting has no effect.", "Some users report stability problems with specific driver versions; restore if you notice issues." },
            CompatibilitySummary = "Windows 10 2004 (build 19041) or newer, a GPU and driver that support HAGS. Administrator rights required.",
            RequiresAdministrator = true,
            RequiresRestart = true,
            Risk = RiskLevel.Medium,
        },
        new ISettingPrimitive[]
        {
            // Absent = decided by driver/OEM defaults, therefore unknown.
            new RegistryDwordPrimitive(registry, RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", desired: 2, valueWhenAbsent: null),
        },
        ctx =>
        {
            var c = new CompatibilityBuilder().RequireBuild(ctx, 19041, "Hardware-accelerated GPU scheduling");
            if (!ctx.Gpus.Any(g => g.Vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel))
                c.Fail("No NVIDIA, AMD or Intel display adapter was detected.");
            else
                c.Unverified("Sloomy Tweaks cannot confirm that your GPU driver supports HAGS. If the toggle is missing in Windows Settings > System > Display > Graphics, your driver does not support it.");
            return c.Build();
        });

    private static Optimization WindowedGameOptimizations(IRegistryAccess registry) => new(
        new OptimizationDefinition
        {
            Id = Ids.WindowedGameOptimizations,
            Name = "Optimizations for windowed games",
            Category = OptimizationCategory.Gpu,
            Kind = OptimizationKind.Actionable,
            Description = "Turns on the Windows 11 setting that upgrades DirectX 10/11 games in windowed and borderless modes to the modern flip presentation model.",
            TechnicalMechanism = "Windows replaces the legacy blt presentation model with the flip model used by DirectX 12 for DX10/DX11 games running windowed or borderless. Stored as the SwapEffectUpgradeEnable=1 token in HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences\\DirectXUserGlobalSettings; other tokens in that value are preserved.",
            ExpectedBenefit = "Microsoft documents lower frame latency and access to Auto HDR and variable refresh rate for affected games. No effect on DirectX 12, Vulkan or exclusive-fullscreen games.",
            Evidence = EvidenceLevel.Documented,
            EvidenceNotes = "Latency improvement is stated by Microsoft without published measurements. Only relevant for DX10/DX11 games in windowed/borderless mode. When the token is absent the effective default depends on the Windows build, so Sloomy Tweaks reports the state as Unknown rather than guessing.",
            Sources = new[] { MsWindowedGames, WindowedGamesRegistry },
            Risks = new[] { "A specific game may behave differently; Windows supports turning it off per game in Graphics settings.", "If Auto HDR is on, Windows forces this setting on." },
            CompatibilitySummary = "Windows 11 22H2 (build 22621) or newer. Per user. Restart the game after changing it.",
        },
        new ISettingPrimitive[]
        {
            new RegistryStringTokenPrimitive(registry, RegistryRoot.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable", desired: "1", valueWhenAbsent: null),
        },
        ctx => new CompatibilityBuilder().RequireBuild(ctx, 22621, "Optimizations for windowed games").Build());

    private static Optimization PointerPrecision(IMouseSettings mouse) => new(
        new OptimizationDefinition
        {
            Id = Ids.PointerPrecision,
            Name = "Turn off Enhance pointer precision",
            Category = OptimizationCategory.InputUsb,
            Kind = OptimizationKind.Actionable,
            Description = "Turns off Windows pointer acceleration so cursor movement is proportional to physical mouse movement.",
            TechnicalMechanism = "Calls SystemParametersInfo(SPI_SETMOUSE) with the acceleration value set to 0, the same change as the 'Enhance pointer precision' checkbox. Thresholds are preserved so restore is exact.",
            ExpectedBenefit = "Consistent, speed-independent cursor movement on the desktop and in games that use the Windows cursor. Games that read raw input are not affected. This is about consistency, not latency.",
            Evidence = EvidenceLevel.Documented,
            EvidenceNotes = "The API is documented by Microsoft. Whether a specific game uses raw input is game-specific and not detected by Sloomy Tweaks.",
            Sources = new[] { MsAccessibilityParams },
            Risks = new[] { "Desktop cursor will feel different; you may want to adjust pointer speed or mouse DPI." },
            CompatibilitySummary = "All supported Windows versions. Per user. No restart.",
        },
        new ISettingPrimitive[] { new PointerAccelerationPrimitive(mouse) });

    private static Optimization PowerPlanSelector(IPowerManagement power) => new(
        new OptimizationDefinition
        {
            Id = Ids.PowerPlan,
            Name = "Active power plan",
            Category = OptimizationCategory.Power,
            Kind = OptimizationKind.Selector,
            Description = "Chooses which Windows power plan is active. Sloomy Tweaks saves the plan you had before the first change and can restore it exactly.",
            TechnicalMechanism = "Calls PowerSetActiveScheme with the selected plan. Plans differ mainly in how aggressively the processor lowers clocks and parks cores when idle.",
            ExpectedBenefit = "A higher-performance plan can make the processor ramp up faster on some systems at the cost of power and heat. No consistent, independent gaming measurements were found; on many modern CPUs Balanced performs the same, and on some it is better.",
            Evidence = EvidenceLevel.Experimental,
            EvidenceNotes = "The mechanism is documented by Microsoft. Gaming benefit is hardware-dependent and unproven. Community reports indicate AMD's dual-CCD X3D core parking works with the Balanced plan. Measure with the Benchmark Center before keeping a change.",
            Sources = new[] { MsPowerSetActiveScheme, X3DDriverBehavior },
            Risks = new[] { "Higher idle power, heat and fan noise with performance plans.", "On laptops: reduced battery life.", "On Ryzen X3D processors, plans other than Balanced can interfere with AMD's core-parking behavior." },
            CompatibilitySummary = "All Windows versions. The list shows only the plans that exist on this PC (Modern Standby devices often expose only Balanced).",
            Risk = RiskLevel.Medium,
        },
        new ISettingPrimitive[] { new PowerSchemePrimitive(power, Guid.Empty) },
        selector: new SelectorBehavior(
            Options: () => power.ListSchemes().Select(p => new SelectorOption(p.Id.ToString("D"), p.Name)).ToList(),
            CurrentValue: values => values.TryGetValue("Power\\ActiveScheme", out SettingValue? v) ? v.Data : null,
            PrimitivesFor: value => new ISettingPrimitive[] { new PowerSchemePrimitive(power, Guid.Parse(value)) },
            CompatibilityFor: (ctx, value) =>
            {
                var c = new CompatibilityBuilder();
                Guid g = Guid.Parse(value);
                bool performance = g == HighPerformanceSchemeGuid || g == UltimatePerformanceSchemeGuid;
                if (performance && ctx.IsAmdX3D)
                    c.Warn("Ryzen X3D detected: AMD's 3D V-Cache core preference is reported to work with the Balanced plan. A performance plan may reduce gaming performance on dual-CCD models.");
                if (performance && ctx.IsHybridCpu == true)
                    c.Warn("Hybrid (P-core/E-core) CPU detected: Windows scheduling on these CPUs is tuned for the Balanced plan. Measure before keeping this change.");
                return c.Build();
            }));

    private static Optimization UsbSelectiveSuspend(IPowerManagement power) => new(
        new OptimizationDefinition
        {
            Id = Ids.UsbSelectiveSuspend,
            Name = "Turn off USB selective suspend (plugged in)",
            Category = OptimizationCategory.Power,
            Kind = OptimizationKind.Actionable,
            Description = "Stops Windows from suspending idle USB devices while the PC is plugged in. Battery behavior is not changed.",
            TechnicalMechanism = "Sets the AC value of the 'USB selective suspend setting' (USB settings subgroup) of the active power plan to Disabled via PowerWriteACValueIndex, then re-activates the plan. The original value and plan are saved and restored exactly.",
            ExpectedBenefit = "Can prevent USB peripherals from being suspended and woken, which is a common troubleshooting step for devices that briefly disconnect. No measured input-latency or FPS benefit is documented.",
            Evidence = EvidenceLevel.Experimental,
            EvidenceNotes = "The setting is a standard Windows power option. There is no independent evidence of a gaming performance gain; it is offered for device stability. Applies only to the plan active when you turn it on.",
            Sources = new[] { UsbSuspendGuide, MsPowerSetActiveScheme },
            Risks = new[] { "Slightly higher power use while plugged in.", "If you switch power plans, the new plan keeps its own value." },
            CompatibilitySummary = "Systems where the active plan exposes the USB selective suspend setting.",
            Risk = RiskLevel.Low,
        },
        new ISettingPrimitive[] { new PowerSettingPrimitive(power, UsbSubgroupGuid, UsbSelectiveSuspendGuid, 0, "USB selective suspend") },
        ctx =>
        {
            var c = new CompatibilityBuilder();
            try
            {
                if (power.ReadAcValue(power.GetActiveScheme(), UsbSubgroupGuid, UsbSelectiveSuspendGuid) is null)
                    c.Fail("The active power plan does not expose the USB selective suspend setting.");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                c.Unverified($"The power setting could not be read: {e.Message}");
            }
            return c.Build();
        });

    private static Optimization AccessibilityShortcuts(IAccessibilitySettings accessibility) => new(
        new OptimizationDefinition
        {
            Id = Ids.AccessibilityShortcuts,
            Name = "Turn off accessibility shortcut prompts",
            Category = OptimizationCategory.InputUsb,
            Kind = OptimizationKind.Actionable,
            Description = "Prevents the Sticky Keys, Filter Keys and Toggle Keys prompts from opening when you press Shift five times or hold Shift or Num Lock during a game.",
            TechnicalMechanism = "Clears the hotkey flag (SKF/FKF/TKF_HOTKEYACTIVE) of STICKYKEYS, FILTERKEYS and TOGGLEKEYS through SystemParametersInfo. The features themselves and all their other options are left as they are.",
            ExpectedBenefit = "Stops a system prompt from taking focus away from a fullscreen game. This is about interruptions, not FPS or latency.",
            Evidence = EvidenceLevel.Documented,
            EvidenceNotes = "The flags and API are documented by Microsoft. People who rely on these accessibility shortcuts should keep them on; the features stay available in Settings > Accessibility > Keyboard.",
            Sources = new[] { MsStickyKeys, MsAccessibilityParams },
            Risks = new[] { "The keyboard shortcuts no longer turn these accessibility features on; use Settings > Accessibility > Keyboard instead." },
            CompatibilitySummary = "All supported Windows versions. Per user. No restart.",
            Risk = RiskLevel.Low,
        },
        new ISettingPrimitive[] { new AccessibilityShortcutsPrimitive(accessibility) });

    private static Optimization Trim(IFileSystemBehavior fs) => new(
        new OptimizationDefinition
        {
            Id = Ids.Trim,
            Name = "Keep TRIM enabled for SSDs",
            Category = OptimizationCategory.Storage,
            Kind = OptimizationKind.Actionable,
            Description = "Makes sure NTFS delete notifications (TRIM) are on, which is the Windows default. Useful if a tool or setting turned them off.",
            TechnicalMechanism = "Uses the documented `fsutil behavior set disabledeletenotify NTFS 0` interface. TRIM tells the SSD which blocks were freed by file deletions so it can manage them internally.",
            ExpectedBenefit = "Restores the default behavior that helps SSDs maintain write performance over time. If TRIM is already on, nothing changes.",
            Evidence = EvidenceLevel.Documented,
            EvidenceNotes = "Microsoft documents that TRIM is on by default for NTFS and that the change needs no restart. Microsoft also notes some devices may perform worse with delete notifications on; restore if you notice that. Drives that do not support TRIM simply ignore it.",
            Sources = new[] { MsFsutilBehavior },
            Risks = new[] { "Rare devices perform worse with delete notifications on (Microsoft's note)." },
            CompatibilitySummary = "NTFS volumes. Administrator rights required. No restart.",
            RequiresAdministrator = true,
            Risk = RiskLevel.Low,
        },
        new ISettingPrimitive[] { new TrimPrimitive(fs) });

    private static Optimization Guidance(
        string id, string name, OptimizationCategory category, string description, string mechanism, string benefit,
        EvidenceLevel evidence, string notes, EvidenceSource[] sources, string[] risks, string compat, string[] steps) => new(
        new OptimizationDefinition
        {
            Id = id,
            Name = name,
            Category = category,
            Kind = OptimizationKind.Guidance,
            Description = description,
            TechnicalMechanism = mechanism,
            ExpectedBenefit = benefit,
            Evidence = evidence,
            EvidenceNotes = notes,
            Sources = sources,
            Risks = risks,
            CompatibilitySummary = compat,
            ManualSteps = steps,
        },
        Array.Empty<ISettingPrimitive>());

    private static Optimization NotOffered(string id, string name, OptimizationCategory category, string why, EvidenceSource[] sources) => new(
        new OptimizationDefinition
        {
            Id = id,
            Name = name,
            Category = category,
            Kind = OptimizationKind.NotOffered,
            Description = "Not offered by Sloomy Tweaks.",
            TechnicalMechanism = why,
            ExpectedBenefit = "None demonstrated.",
            Evidence = EvidenceLevel.NotRecommended,
            EvidenceNotes = sources.Length == 0
                ? "No authoritative source or reproducible measurement supporting a gaming benefit was found."
                : "See sources.",
            Sources = sources,
            Risks = Array.Empty<string>(),
            CompatibilitySummary = "Not applicable.",
        },
        Array.Empty<ISettingPrimitive>());

    private static void EnsureUniqueIds(IEnumerable<Optimization> items)
    {
        var duplicate = items.GroupBy(o => o.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate optimization id '{duplicate.Key}'.");
    }
}
