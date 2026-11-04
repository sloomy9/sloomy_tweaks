# Sloomy Tweaks — Audit of 0.1.0 (input for the 0.2.0 rework)

Prioritized issues found by inspecting the 0.1.0 source. Each line shows its status after the rework.

## P0 — Commercial blockers

| # | Issue | Where | 0.2.0 |
|---|---|---|---|
| 1 | Free access paths: "Continue with limited access", free dashboard and category pages, padlock icons on some items | `MainViewModel`, `MainWindow.xaml` | **Fixed**: the whole app sits behind the activation screen. Without a license, only the restore-only view is reachable. |
| 2 | Production licensing accepted `http://localhost`. No explicit development mode. | `LicensingConfig` (App) | **Fixed**: `LicensingConfiguration` (Core) with `environment`. Production requires HTTPS and a non-loopback host. Development mode is shown permanently in the title bar. |
| 3 | The test kit made customers start a local license server | packaging | **Fixed** for release: the release build ships no `licensing.json` and no secrets. The dev kit is separate and labelled DEVELOPMENT. A Docker/Caddy deployment for a remote HTTPS server was added. |
| 4 | The WPF app had never been compiled | `SloomyTweaks.App` | **Fixed**: now compiled with the official .NET SDK (Windows targeting). |
| 5 | The license was only checked at startup, so a revocation never reached a long-running session | `MainViewModel` | **Fixed**: background refresh every 6 h; the app locks if the license becomes invalid. |

## P1 — Controls and correctness

| # | Issue | 0.2.0 |
|---|---|---|
| 6 | Apply/Restore buttons instead of per-tweak ON/OFF controls | **Fixed**: a verified toggle per actionable tweak. It turns ON only after the read-back verification, and OFF means restoring the saved original. |
| 7 | "Restart pending" replaced the real state, so the UI could not show both "ON" and "pending restart" | **Fixed**: `OptimizationStatus` now has a separate `ConfigState` and a `RestartPending` flag. |
| 8 | The power plan (a multi-state setting) was forced into a binary "High performance" toggle | **Fixed**: replaced by a power-plan **selector** with backup and restore of the original plan. |
| 9 | Settings already enabled by Windows (e.g. Game Mode) showed an active Apply/Restore with nothing to restore | **Fixed**: the toggle is disabled with the explanation "already on before Sloomy Tweaks; nothing to restore". |
| 10 | Tweaks needing admin rights could be clicked unelevated and failed afterwards | **Fixed**: the toggle is disabled with a "requires administrator" reason. The title bar offers a restart as administrator. |
| 11 | Optimization History was a separate page outside the requested navigation | **Fixed**: merged into Restore Center. The navigation matches the requested 15 sections. |

## P2 — Content gaps

| # | Issue | 0.2.0 |
|---|---|---|
| 12 | Storage page: drive list only | Added a TRIM (delete notification) tweak through the documented `fsutil behavior` interface, plus a TRIM diagnostic. |
| 13 | Input: a single tweak | Added "Disable accessibility shortcut prompts" (Sticky/Filter/Toggle Keys hotkeys, SystemParametersInfo), plus pointer-speed information and an explanation of why polling rate cannot be read from Windows. |
| 14 | CPU page: no live data on power policy | Added processor power diagnostics: minimum/maximum processor state, core parking minimum and boost mode of the active plan (read-only, PowrProf). |
| 15 | RAM page: usage only | Added commit charge/limit and the page-file configuration, with a warning if the page file is disabled. |
| 16 | Gaming page: guidance only | Added display refresh-rate detection and a read-only Fortnite `GameUserSettings.ini` reader. |
| 17 | Power page: single tweak | Added the power-plan selector, USB selective suspend (Experimental) and processor-policy diagnostics. |
| 18 | No direct links to the matching Windows Settings pages | Added validated `ms-settings:` shortcuts (Game Mode, Graphics, Captures, Startup apps, Power). |

## P3 — UI

| # | Issue | 0.2.0 |
|---|---|---|
| 19 | Small radii (8/12), single-column cards, large empty areas | New radius system: window 14, containers 16, cards 14, controls 10, dialogs 16. Responsive card grid (`AdaptiveGrid`). |
| 20 | Square window corners | DWM rounded corners on Windows 11 (`DWMWA_WINDOW_CORNER_PREFERENCE`) for the main window and dialogs. |
| 21 | Palette did not match the brand spec | Updated: sidebar #101018, cards #181820, elevated #20202B, borders #30303D. |
| 22 | Not-offered items were rendered as full cards, adding clutter | Moved to a compact "Not offered (and why)" list at the bottom of each page. |

## Known limitations that remain

- **GPU driver support for HAGS cannot be queried.** The toggle stays "Unverified" and needs explicit confirmation.
- **No real Windows runs yet.** Every Win32 call is compiled, and its logic is covered through fakes, but none has been executed on real hardware. See STATUS.md.
