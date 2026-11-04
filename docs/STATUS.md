# Sloomy Tweaks — Implementation Status (0.2.0-rc1)

**Not production-ready.** The security and functional tests that matter most (real Windows 10/11 hardware, a deployed HTTPS license server, a code-signed build) have not been run yet. See "Remaining before release" below.

## Where each thing was tested

| Level | What it means | What ran there |
|---|---|---|
| **Mock** | Automated tests against fake registry/power/mouse/accessibility/fsutil implementations | 95/95 tests pass (Linux, .NET 8.0 SDK): engine workflow, every toggle's apply → verify → restore → verify-restore, failed apply/rollback, permissions, Windows-build compatibility, selector, restart persistence with SQLite, licensing (tokens, server over real HTTP, config validation), view models (toggle, selector, gating, restore without license), parsers |
| **Windows CI** | GitHub Actions `windows-latest` (Windows Server) | Full solution build including WPF, 95/95 tests, self-contained publish and unsigned Inno Setup installer — all green. No UI interaction and no system changes are made here |
| **Wine** | The real compiled WPF app (win-x64) run under Wine 9 on Linux (Windows 11 build 22631 reported) | Activation screen; real activation against the dev license server; dashboard with real hardware detection; Input & USB, Windows, Power, Restore Center pages; **pointer-precision toggle applied through SystemParametersInfo, verified (MouseSpeed 1→0 in the registry), shown ON, restored from Restore Center (back to 1), history shown before/after**. PowrProf and NCrypt ECDSA do not exist in Wine: the app reported this honestly (power items "could not be read"); the QA harness `tools/SloomyTweaks.UiPreview` swaps in a managed ECDSA verifier only for this test |
| **VM** | Windows 10/11 virtual machine | **Not run** |
| **Real hardware** | Windows PC with real GPU/power plans | **Not run** |

Wine does not prove that HAGS, power plans, TRIM or Game DVR changes behave the same on real Windows: those paths are covered only by mock tests until a VM/real-hardware pass is done.

## Features (0.2.0)

| Area | Status |
|---|---|
| License-only access: activation screen first, no section reachable without a valid license | ✅ mock + Wine |
| Restore Center reachable from the activation screen with an expired/missing license | ✅ mock |
| Development mode: only via `licensing.json` `"environment":"development"` with a loopback URL; title-bar badge "DEVELOPMENT MODE" | ✅ mock + Wine |
| Production config validation: https only, no localhost, no example domain, no private keys | ✅ mock |
| Release exe: no `licensing.json`, no keys, no dev URL embedded (checked by scanning the binary) | ✅ |
| Periodic license re-check (6 h) so revocation/expiry reaches long sessions | ✅ code; timer not exercised in tests |
| ON/OFF toggles: ON only when applied and verified; N/A when unsupported; disabled with reason (admin, no backup, no license); Pending restart | ✅ mock + Wine |
| Selector for multi-state settings (power plan) with "Restore original" | ✅ mock; Wine cannot list power plans |
| New toggles: accessibility shortcut prompts off, USB selective suspend off (AC), TRIM on | ✅ mock |
| Diagnostics (read-only, separate from tweaks): processor power policy, commit/page file, displays & refresh rate, pointer speed, Fortnite GameUserSettings.ini, TRIM status | ✅ parsers mock-tested; display/pointer read in Wine |
| 15-section sidebar, premium dark/purple design system, rounded corners (DWM on Win11) | ✅ Wine (icon glyphs need Segoe Fluent Icons/MDL2, present on Windows but not in Wine) |
| License server Docker + Caddy (automatic HTTPS) deployment files | written; **not deployed** |
| Benchmark Center (PresentMon CSV import, repeated-run comparison) | ✅ mock (unchanged from 0.1.0) |

## Remaining before release

1. Run the app on a Windows 11 and a Windows 10 VM: every toggle on/off, restart persistence for HAGS, power-plan selector, TRIM as admin, standard-user behavior.
2. Run it once on real hardware (NVIDIA and AMD GPU; one laptop for power plans).
3. Deploy the license server on a real host with a domain (deploy/license-server), generate production secrets there, set the public key and URL in `licensing.json`, test activation/refresh/deactivation/revocation/offline grace against it.
4. Buy a code-signing certificate and sign the exe and installer (SmartScreen).
5. Privacy policy and terms (the server stores a device-id hash, computer name, app version).
