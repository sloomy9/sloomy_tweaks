# Sloomy Tweaks — Evidence Registry

Generated from `OptimizationCatalog` (`tests/SloomyTweaks.Tests -- --export-evidence`). Do not edit by hand.

| Evidence level | Meaning |
|---|---|
| Verified | Supported by reproducible measurements relevant to the optimization. |
| Documented | Supported by authoritative documentation, without demonstrated gaming performance gains. |
| Experimental | Technically plausible but lacking sufficient independent evidence. Never enabled by default. |
| Not Recommended | Unsupported, obsolete, dangerous or ineffective. Listed only to explain why it is not offered. |

| Item | Kind | Evidence | Recommended flow | Admin | Restart |
|---|---|---|---|---|---|
| [Hardware-accelerated GPU scheduling](#hardware-accelerated-gpu-scheduling) | Actionable | Documented | Yes | Yes | Yes |
| [Optimizations for windowed games](#optimizations-for-windowed-games) | Actionable | Documented | Yes | No | No |
| [Windows Game Mode](#windows-game-mode) | Actionable | Documented | Yes | No | No |
| [Turn off background recording](#turn-off-background-recording) | Actionable | Documented | Yes | No | No |
| [Turn off Enhance pointer precision](#turn-off-enhance-pointer-precision) | Actionable | Documented | Yes | No | No |
| [High performance power plan](#high-performance-power-plan) | Actionable | Experimental | No | No | No |
| [Use NVIDIA Reflex in supported games](#use-nvidia-reflex-in-supported-games) | Guidance | Verified | No | — | — |
| [Fortnite: measured, per-system configuration](#fortnite-measured-per-system-configuration) | Guidance | Documented | No | — | — |
| [Disable CPU security mitigations (Spectre/Meltdown etc.)](#disable-cpu-security-mitigations-spectremeltdown-etc) | NotOffered | NotRecommended | No | — | — |
| [Globally disable core parking, SMT/Hyper-Threading or C-states](#globally-disable-core-parking-smthyper-threading-or-c-states) | NotOffered | NotRecommended | No | — | — |
| [Disable the page file](#disable-the-page-file) | NotOffered | NotRecommended | No | — | — |
| [RAM cleaning / standby list purging](#ram-cleaning--standby-list-purging) | NotOffered | NotRecommended | No | — | — |
| [Disable services from generic internet lists](#disable-services-from-generic-internet-lists) | NotOffered | NotRecommended | No | — | — |
| [Timer resolution tools, HPET and dynamic tick changes](#timer-resolution-tools-hpet-and-dynamic-tick-changes) | NotOffered | NotRecommended | No | — | — |
| [TCP registry tweaks (TcpAckFrequency, TCPNoDelay, NetworkThrottlingIndex)](#tcp-registry-tweaks-tcpackfrequency-tcpnodelay-networkthrottlingindex) | NotOffered | NotRecommended | No | — | — |
| [Change DNS to reduce game ping](#change-dns-to-reduce-game-ping) | NotOffered | NotRecommended | No | — | — |

## Hardware-accelerated GPU scheduling

- **Id:** `gpu.hardware-accelerated-gpu-scheduling` · **Category:** Gpu · **Kind:** Actionable · **Evidence:** Documented
- **Changes:** `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode = 2 (REG_DWORD)`

**What it does.** Turns on hardware-accelerated GPU scheduling (HAGS), the same switch as Settings > System > Display > Graphics.

**Mechanism.** With supported GPUs and drivers, Windows offloads most GPU scheduling from a high-priority CPU thread to a dedicated scheduling processor on the GPU. Stored in HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode (2 = on, 1 = off).

**Expected benefit.** Reduces CPU-side scheduling overhead. Microsoft notes most applications already hide scheduling cost through buffering, and independent testing found no consistent FPS or frametime improvement. Treat this as a configuration choice, not a performance boost.

**What is not known.** Mechanism documented by Microsoft. Independent measurements (2020) showed little difference. Sloomy Tweaks cannot yet query driver support directly, so compatibility is reported as unverified.

**Compatibility.** Windows 10 2004 (build 19041) or newer, a GPU and driver that support HAGS. Administrator rights required.

**Risks.**
- Requires a restart.
- If the GPU or driver does not support HAGS, the setting has no effect.
- Some users report stability problems with specific driver versions; restore if you notice issues.

**Sources.**
- [Microsoft DirectX Developer Blog — Hardware Accelerated GPU Scheduling](https://devblogs.microsoft.com/directx/hardware-accelerated-gpu-scheduling/) — OfficialDocumentation
- [GamersNexus — Hardware-Accelerated GPU Scheduling Benchmarks (frametimes, FPS)](https://gamersnexus.net/guides/3599-windows-10-hardware-accelerated-gpu-scheduling-benchmarks) — IndependentMeasurement. 2020 measurements on Windows 10 2004; results may differ on current drivers and Windows 11.

## Optimizations for windowed games

- **Id:** `gpu.windowed-game-optimizations` · **Category:** Gpu · **Kind:** Actionable · **Evidence:** Documented
- **Changes:** `SwapEffectUpgradeEnable=1 inside DirectXUserGlobalSettings`

**What it does.** Turns on the Windows 11 setting that upgrades DirectX 10/11 games in windowed and borderless modes to the modern flip presentation model.

**Mechanism.** Windows replaces the legacy blt presentation model with the flip model used by DirectX 12 for DX10/DX11 games running windowed or borderless. Stored as the SwapEffectUpgradeEnable=1 token in HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings; other tokens in that value are preserved.

**Expected benefit.** Microsoft documents lower frame latency and access to Auto HDR and variable refresh rate for affected games. No effect on DirectX 12, Vulkan or exclusive-fullscreen games.

**What is not known.** Latency improvement is stated by Microsoft without published measurements. Only relevant for DX10/DX11 games in windowed/borderless mode. When the token is absent the effective default depends on the Windows build, so Sloomy Tweaks reports the state as Unknown rather than guessing.

**Compatibility.** Windows 11 22H2 (build 22621) or newer. Per user. Restart the game after changing it.

**Risks.**
- A specific game may behave differently; Windows supports turning it off per game in Graphics settings.
- If Auto HDR is on, Windows forces this setting on.

**Sources.**
- [Microsoft Support — Optimizations for windowed games in Windows 11](https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952) — OfficialDocumentation
- [ElevenForum — Turn On or Off Optimizations for Windowed Games (registry location)](https://elevenforum.com/t/turn-on-or-off-optimizations-for-windowed-games-in-windows-11.4943/) — CommunityDocumentation. Documents the DirectXUserGlobalSettings value. Not a Microsoft source.

## Windows Game Mode

- **Id:** `windows.game-mode` · **Category:** Windows · **Kind:** Actionable · **Evidence:** Documented
- **Changes:** `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled = 1 (REG_DWORD)`

**What it does.** Ensures Windows Game Mode is turned on for the current user.

**Mechanism.** Game Mode lets Windows prioritize the foreground game: Windows Update does not install drivers or show restart notifications during play, and background work is deprioritized. Stored in HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled (on by default when absent).

**Expected benefit.** Fewer interruptions from background activity while gaming; Microsoft describes a more consistent experience. No universal FPS gain is documented.

**What is not known.** Behavior is documented by Microsoft; performance impact is game- and system-dependent and not quantified by Microsoft. The registry location comes from community documentation and matches the Settings toggle. On Ryzen X3D processors AMD's driver depends on Game Mode / Game Bar game detection.

**Compatibility.** Windows 11. Per user. On LTSC, Xbox Game Bar is not installed; Game Mode itself is still available.

**Risks.**
- Rare reports of specific games or tools behaving differently with Game Mode on; turn it off for that case.

**Sources.**
- [Microsoft Learn — Performance, capture, and system-level gaming features](https://learn.microsoft.com/en-us/training/modules/optimize-windows-gaming/4-performance-gaming-features) — OfficialDocumentation
- [HWCooling — Ryzen 7000X3D technical details and asymmetric CCX handling](https://www.hwcooling.net/en/ryzen-7000x3d-technical-details-and-asymmetric-ccx-handling/) — TechnicalReporting. Explains that AMD's driver relies on Windows Game Mode / Xbox Game Bar game detection to prefer V-Cache cores.
- [ElevenForum — Turn On or Off Game Mode in Windows 11 (registry location)](https://www.elevenforum.com/t/turn-on-or-off-game-mode-in-windows-11.1447/) — CommunityDocumentation. Documents HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled. Not a Microsoft source.

## Turn off background recording

- **Id:** `windows.background-recording-off` · **Category:** Windows · **Kind:** Actionable · **Evidence:** Documented
- **Changes:** `HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\HistoricalCaptureEnabled = 0 (REG_DWORD)`

**What it does.** Turns off Xbox Game Bar 'Record what happened', which continuously records gameplay in the background.

**Mechanism.** Background recording keeps a rolling video capture while a game runs, which uses encoder, memory and disk resources. Stored in HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\HistoricalCaptureEnabled (off when absent).

**Expected benefit.** Removes the resource cost of continuous capture. Microsoft states background recording can affect game performance; the size of the effect is not quantified.

**What is not known.** Performance impact is stated by Microsoft without numbers. Independent measurements were not found. The registry location comes from community documentation and matches the Settings toggle.

**Compatibility.** Windows 11. Per user. Already off by default on most installations.

**Risks.**
- You can no longer save the last moments of gameplay with Win+Alt+G until it is turned back on.

**Sources.**
- [Microsoft Support — Capture and share game clips (background recording)](https://support.microsoft.com/en-us/help/4027144) — OfficialDocumentation. States that background recording can affect game performance.
- [ElevenForum — Enable or Disable Record What Happened for Gaming Captures](https://www.elevenforum.com/t/enable-or-disable-record-what-happened-for-gaming-captures-in-windows-11.17900) — CommunityDocumentation. Documents HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\HistoricalCaptureEnabled. Not a Microsoft source.

## Turn off Enhance pointer precision

- **Id:** `input.enhance-pointer-precision-off` · **Category:** InputUsb · **Kind:** Actionable · **Evidence:** Documented
- **Changes:** `Mouse acceleration = 0 (Enhance pointer precision off)`

**What it does.** Turns off Windows pointer acceleration so cursor movement is proportional to physical mouse movement.

**Mechanism.** Calls SystemParametersInfo(SPI_SETMOUSE) with the acceleration value set to 0, the same change as the 'Enhance pointer precision' checkbox. Thresholds are preserved so restore is exact.

**Expected benefit.** Consistent, speed-independent cursor movement on the desktop and in games that use the Windows cursor. Games that read raw input are not affected. This is about consistency, not latency.

**What is not known.** The API is documented by Microsoft. Whether a specific game uses raw input is game-specific and not detected by Sloomy Tweaks.

**Compatibility.** All supported Windows versions. Per user. No restart.

**Risks.**
- Desktop cursor will feel different; you may want to adjust pointer speed or mouse DPI.

**Sources.**
- [Microsoft Learn — Accessibility parameters (SPI_GETMOUSE / SPI_SETMOUSE)](https://learn.microsoft.com/en-us/windows/win32/winauto/accessibility-parameters) — OfficialDocumentation

## High performance power plan

- **Id:** `power.high-performance-plan` · **Category:** Power · **Kind:** Actionable · **Evidence:** Experimental
- **Changes:** `Active power scheme = 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`

**What it does.** Switches the active power plan to the built-in 'High performance' plan. Your previous plan is restored exactly.

**Mechanism.** Calls PowerSetActiveScheme with the High performance scheme. That plan keeps processor performance states higher, trading power and heat for faster clock ramp-up.

**Expected benefit.** Possibly faster frequency response on some systems. No consistent, independent gaming measurements were found; on modern CPUs Balanced is often equivalent, and on some it is better.

**What is not known.** Not eligible for the recommended flow. Community reports indicate AMD's dual-CCD X3D core parking works with the Balanced plan; Intel hybrid CPUs rely on Windows scheduling features that interact with power settings.

**Compatibility.** Desktop systems where Windows exposes the High performance plan. Not available on many Modern Standby devices.

**Risks.**
- Higher idle power, heat and fan noise.
- On laptops: reduced battery life.
- On Ryzen X3D processors it can interfere with AMD's core-parking behavior.

**Sources.**
- [Microsoft Learn — PowerSetActiveScheme function](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powersetactivescheme) — OfficialDocumentation
- [HWCooling — Ryzen 7000X3D technical details and asymmetric CCX handling](https://www.hwcooling.net/en/ryzen-7000x3d-technical-details-and-asymmetric-ccx-handling/) — TechnicalReporting. Explains that AMD's driver relies on Windows Game Mode / Xbox Game Bar game detection to prefer V-Cache cores.

## Use NVIDIA Reflex in supported games

- **Id:** `gpu.nvidia-reflex-guidance` · **Category:** Gpu · **Kind:** Guidance · **Evidence:** Verified

**What it does.** Enable NVIDIA Reflex Low Latency inside games that support it (Fortnite does) instead of stacking driver-level latency settings.

**Mechanism.** Reflex is integrated into the game engine: the game paces CPU work so frames do not queue up in front of the GPU. Driver 'Low Latency Mode' limits queued frames from the driver side and, per TechSpot's testing, does not apply to DirectX 12 titles.

**Expected benefit.** Lower system latency in GPU-bound scenarios. Independent LDAT measurements show large reductions when the GPU is the bottleneck and little change when latency is already low or the game is CPU-bound.

**What is not known.** Verified for supported games on NVIDIA GPUs, GPU-bound. The size of the effect depends on the game, settings and bottleneck. Sloomy Tweaks does not change NVIDIA driver settings: there is no supported public API for doing so from a third-party application.

**Compatibility.** NVIDIA GeForce GPUs in games that implement Reflex.

**Risks.**
- 'On + Boost' keeps GPU clocks high and increases power and heat.
- Enabling several latency limiters at once (Reflex, driver Low Latency Mode, external caps) makes behavior harder to reason about.

**Steps.**
1. In Fortnite: Settings > Video > NVIDIA Reflex Low Latency > On.
2. Leave NVIDIA Control Panel 'Low Latency Mode' at its default for that game; Reflex supersedes it while active.
3. Use 'On + Boost' only if measurements on your system show a benefit worth the extra power.
4. Measure with a tool that reports PC latency (for example NVIDIA FrameView) before and after.

**Sources.**
- [TechSpot — Nvidia Reflex tested with LDAT (includes Fortnite)](https://www.techspot.com/article/2123-nvidia-reflex-rtx/) — IndependentMeasurement. Hardware photodetector (LDAT) measurements. Also notes driver low-latency modes do not apply to DirectX 12 titles.

## Fortnite: measured, per-system configuration

- **Id:** `gaming.fortnite-profile-guidance` · **Category:** Gaming · **Kind:** Guidance · **Evidence:** Documented

**What it does.** A checklist for configuring Fortnite with measurements instead of copied 'best settings'. Sloomy Tweaks does not edit Fortnite's configuration files in this version.

**Mechanism.** Fortnite offers several rendering modes (DirectX 11, DirectX 12, Performance). Which one gives the best frame pacing and 1% lows depends on CPU, GPU, driver and settings. Frame-rate caps, V-Sync and variable refresh rate interact with latency.

**Expected benefit.** Stable frame pacing and fewer stutters when the configuration matches the system's actual bottleneck. No single configuration is optimal for every PC.

**What is not known.** Configuration guidance only. Any improvement must be confirmed with repeated captures in the Benchmark Center.

**Compatibility.** Fortnite on Windows 11.

**Risks.**
- Editing GameUserSettings.ini while the game is running can be overwritten by the game.
- Changing rendering mode triggers shader recompilation; the first matches afterwards may stutter.

**Steps.**
1. Record a baseline: capture at least 3 runs of the same replay segment with PresentMon and import them in Benchmark Center.
2. Test one change at a time (rendering mode, frame-rate cap, Reflex) and capture another 3+ runs.
3. After changing rendering mode or drivers, play one match first so shaders finish compiling before measuring.
4. With a VRR display, a frame cap slightly below the refresh rate keeps VRR active; verify on your system.
5. Keep the configuration only if Benchmark Center reports a statistically meaningful improvement.

**Sources.**
- [TechSpot — Nvidia Reflex tested with LDAT (includes Fortnite)](https://www.techspot.com/article/2123-nvidia-reflex-rtx/) — IndependentMeasurement. Hardware photodetector (LDAT) measurements. Also notes driver low-latency modes do not apply to DirectX 12 titles.
- [Microsoft Support — Optimizations for windowed games in Windows 11](https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952) — OfficialDocumentation

## Disable CPU security mitigations (Spectre/Meltdown etc.)

- **Id:** `cpu.disable-security-mitigations` · **Category:** Cpu · **Kind:** NotOffered · **Evidence:** NotRecommended

**What it does.** Not offered by Sloomy Tweaks.

**Mechanism.** Disabling mitigations exposes the system to known side-channel attacks. Sloomy Tweaks never disables security protections for performance.

**Expected benefit.** None demonstrated.

**What is not known.** No authoritative source or reproducible measurement supporting a gaming benefit was found.

**Compatibility.** Not applicable.

**Sources.**
- None found that supports a gaming benefit.

## Globally disable core parking, SMT/Hyper-Threading or C-states

- **Id:** `cpu.disable-core-parking-smt-cstates` · **Category:** Cpu · **Kind:** NotOffered · **Evidence:** NotRecommended

**What it does.** Not offered by Sloomy Tweaks.

**Mechanism.** Effects are hardware-specific and can reduce boost behavior or break vendor scheduling. On dual-CCD Ryzen X3D processors, AMD's driver parks cores intentionally during games. Any such change needs hardware-specific evidence, which Sloomy Tweaks does not have.

**Expected benefit.** None demonstrated.

**What is not known.** See sources.

**Compatibility.** Not applicable.

**Sources.**
- [HWCooling — Ryzen 7000X3D technical details and asymmetric CCX handling](https://www.hwcooling.net/en/ryzen-7000x3d-technical-details-and-asymmetric-ccx-handling/) — TechnicalReporting. Explains that AMD's driver relies on Windows Game Mode / Xbox Game Bar game detection to prefer V-Cache cores.

## Disable the page file

- **Id:** `memory.disable-page-file` · **Category:** Memory · **Kind:** NotOffered · **Evidence:** NotRecommended

**What it does.** Not offered by Sloomy Tweaks.

**Mechanism.** Microsoft documents that page file sizing depends on crash-dump settings and peak commit charge, is unique to each system, and cannot be generalized. Removing it lowers the commit limit, which can make applications fail with out-of-memory errors even with free RAM, and prevents crash dumps.

**Expected benefit.** None demonstrated.

**What is not known.** See sources.

**Compatibility.** Not applicable.

**Sources.**
- [Microsoft Docs — How to determine the appropriate page file size for 64-bit versions of Windows](https://github.com/MicrosoftDocs/windows-itpro-docs/blob/master/windows/client-management/determine-appropriate-page-file-size.md) — OfficialDocumentation

## RAM cleaning / standby list purging

- **Id:** `memory.standby-list-cleaning` · **Category:** Memory · **Kind:** NotOffered · **Evidence:** NotRecommended

**What it does.** Not offered by Sloomy Tweaks.

**Mechanism.** The standby list is Windows' file cache. Purging it forces data to be re-read from disk. Sloomy Tweaks found no reproducible measurements showing a general gaming benefit, so it is not offered.

**Expected benefit.** None demonstrated.

**What is not known.** No authoritative source or reproducible measurement supporting a gaming benefit was found.

**Compatibility.** Not applicable.

**Sources.**
- None found that supports a gaming benefit.

## Disable services from generic internet lists

- **Id:** `services.generic-disable-lists` · **Category:** ProcessesServices · **Kind:** NotOffered · **Evidence:** NotRecommended

**What it does.** Not offered by Sloomy Tweaks.

**Mechanism.** Service dependencies differ between systems and Windows builds. Sloomy Tweaks only suggests service changes for a specific, measured workload, and never touches Windows Update, Defender or security services.

**Expected benefit.** None demonstrated.

**What is not known.** No authoritative source or reproducible measurement supporting a gaming benefit was found.

**Compatibility.** Not applicable.

**Sources.**
- None found that supports a gaming benefit.

## Timer resolution tools, HPET and dynamic tick changes

- **Id:** `input.timer-resolution-hpet` · **Category:** InputUsb · **Kind:** NotOffered · **Evidence:** NotRecommended

**What it does.** Not offered by Sloomy Tweaks.

**Mechanism.** These change global system timing. Sloomy Tweaks found no reproducible, current measurements showing a general improvement, and it does not change global timer settings.

**Expected benefit.** None demonstrated.

**What is not known.** No authoritative source or reproducible measurement supporting a gaming benefit was found.

**Compatibility.** Not applicable.

**Sources.**
- None found that supports a gaming benefit.

## TCP registry tweaks (TcpAckFrequency, TCPNoDelay, NetworkThrottlingIndex)

- **Id:** `network.tcp-registry-tweaks` · **Category:** Network · **Kind:** NotOffered · **Evidence:** NotRecommended

**What it does.** Not offered by Sloomy Tweaks.

**Mechanism.** Real-time game traffic generally uses UDP, so TCP acknowledgement settings do not apply to it. No reproducible measurements of in-game latency improvement were found.

**Expected benefit.** None demonstrated.

**What is not known.** No authoritative source or reproducible measurement supporting a gaming benefit was found.

**Compatibility.** Not applicable.

**Sources.**
- None found that supports a gaming benefit.

## Change DNS to reduce game ping

- **Id:** `network.dns-for-ping` · **Category:** Network · **Kind:** NotOffered · **Evidence:** NotRecommended

**What it does.** Not offered by Sloomy Tweaks.

**Mechanism.** DNS is only used to resolve server names. It does not change the network path to the game server once connected.

**Expected benefit.** None demonstrated.

**What is not known.** No authoritative source or reproducible measurement supporting a gaming benefit was found.

**Compatibility.** Not applicable.

**Sources.**
- None found that supports a gaming benefit.
