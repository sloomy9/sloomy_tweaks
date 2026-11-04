# Sloomy Tweaks — Implementation Status (0.1.0 preview)

Legend: ✅ implemented and covered by automated tests · 🟢 implemented, compiled, not testable off Windows · 🟡 written, **not yet compiled** · ⛔ not implemented (marked as unavailable in the app)

## Build verification performed

- Built with the .NET 8 SDK (8.0.133) on Linux: Contracts, Core, Data, Platform.Windows, Presentation, LicenseServer, Tests — **all compile**.
- **69/69 automated tests pass**, including license-server integration tests over real HTTP.
- License server run as a real process: key generation CLI, `/health`, admin auth, license creation, activation and security headers checked with curl.
- **The WPF project (`SloomyTweaks.App`) was not compiled.** The build environment had no access to the Windows Desktop targeting pack. Its XAML was checked to be well-formed, every `StaticResource` key was checked to exist, and every binding path was checked against view-model properties. It still needs a first build and visual QA on Windows.

## Features

| Area | Status | Notes |
|---|---|---|
| Optimization framework (primitives, definitions, compatibility) | ✅ | |
| Safe workflow engine: backup, verify, rollback on failure, history, restart-pending | ✅ | Includes failure-mid-apply and policy-revert tests. |
| Evidence registry | ✅ | 6 actionable, 2 guidance, 8 not offered. See [EVIDENCE.md](EVIDENCE.md). |
| Registry, power plan and pointer settings (Win32) | 🟢 | Logic is tested through fakes. The Win32 calls still need a Windows run. |
| Hardware detection (CPU, cores, hybrid, GPU + driver, RAM, motherboard, Windows build, power plan, uptime, CPU/RAM usage) | 🟢 | Registry + `EnumDisplayDevices` + `GetLogicalProcessorInformationEx`. Needs validation across real systems (multi-GPU, laptops). |
| HAGS driver-support detection | ⛔ | Reported as "Unverified" and requires explicit acknowledgement. A `D3DKMTQueryAdapterInfo` probe is a candidate for a later version once its struct layout is verified. |
| System Restore points | 🟢 | Windows throttles creation. Sloomy Tweaks' own backups are the primary rollback. |
| Restore Center (individual + all, no license required) | ✅ | |
| Optimization history | ✅ | |
| Benchmark Center: PresentMon import, stats, Welch comparison, export | ✅ | Live capture from the app is not implemented: capture with PresentMon and import the CSV. |
| Network diagnostics (adapters, gateway vs. internet ping, jitter, loss, interpretation) | ✅ / 🟢 | ICMP disclaimer shown. No placebo TCP tweaks. |
| Processes & Services | 🟢 | Read-only inventory with protected-item marking. Service changes are intentionally not offered. |
| Licensing: client state machine, offline grace, DPAPI token storage | ✅ / 🟢 | DPAPI store compiled, not run. |
| License server: activation, refresh, deactivation, device limits, expiry from first activation, revocation, admin API, rate limiting, audit log | ✅ | |
| Admin dashboard (web) | 🟢 | Served and reachable. Interactive QA not performed. |
| WPF UI: title bar, sidebar (16 sections), dashboard, cards, all pages, activation overlay | 🟡 | |
| Settings (start with Windows, restore points, retention, logs folder) | 🟡 | View model ✅. |
| Light theme | ⛔ | Stated in Settings. |
| Automatic updates | ⛔ | Stated in Settings. Needs a signed update feed. |
| Fortnite config-file editing | ⛔ | Guidance only. Planned with backups and explicit consent. |
| Storage optimizations | — | None meet the evidence bar. The page shows drive information only. |
| Installer | 🟡 | Inno Setup script provided, not built. |
| Code signing | 🟡 | `build.ps1 -SignThumbprint` wraps `signtool`. Requires your certificate. |

## Recommended next steps

1. Build `SloomyTweaks.App` on Windows 11 and do visual QA. Then test on Windows 11 Enterprise LTSC 2024, without Xbox Game Bar.
2. Run each actionable optimization on real hardware (NVIDIA, AMD, Intel; X3D; hybrid Intel). Confirm detection, apply, verify and restore.
3. Deploy the license server behind HTTPS, generate the production keys, and fill in `licensing.json`.
4. Add measured evidence: repeated PresentMon captures before and after each actionable item on several systems. Promote an item to Verified only if the results support it.
