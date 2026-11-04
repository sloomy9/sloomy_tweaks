# Sloomy Tweaks

Evidence-based Windows gaming optimization for Windows 11. Every optimization Sloomy Tweaks offers states its mechanism, evidence level, sources and risks. Each one is applied only after the current configuration has been read and saved. The result is verified by reading it back, and any change can be reversed from the Restore Center, with or without a license.

> **Status: 0.1.0 preview.** The core engine, Windows integration, licensing client and server, and tests are implemented. The WPF interface is written but has not yet been compiled or run on Windows. See [docs/STATUS.md](docs/STATUS.md).

## Repository layout

```
SloomyTweaks.sln
src/
  SloomyTweaks.Licensing.Contracts  Signed license-token format and API contracts (client + server)
  SloomyTweaks.Core                 Optimization framework, evidence registry, safe workflow engine,
                                    benchmark statistics, network diagnostics, licensing client
  SloomyTweaks.Data                 Local SQLite store (uses Windows' built-in winsqlite3.dll)
  SloomyTweaks.Platform.Windows     Documented Win32 integration: registry, PowrProf, SystemParametersInfo,
                                    hardware detection, DPAPI, System Restore, process/service inventory
  SloomyTweaks.Presentation         MVVM view models (UI-framework independent, unit tested)
  SloomyTweaks.App                  WPF shell: custom title bar, sidebar, pages, design system
  SloomyTweaks.LicenseServer        ASP.NET Core license server + admin dashboard (/admin)
tests/
  SloomyTweaks.Tests                Unit and integration tests (dependency-free runner)
docs/
  ARCHITECTURE.md  EVIDENCE.md  STATUS.md  LICENSE-SERVER.md
installer/
  SloomyTweaks.iss                  Inno Setup script for the signed installer
```

## Building

Requirements: .NET 8 SDK. The WPF app needs Windows. Everything else builds on Windows, Linux or macOS.

```powershell
# Windows: full build, tests, self-contained publish, optional signing
./build.ps1 -Configuration Release
./build.ps1 -Configuration Release -SignThumbprint <certificate thumbprint>

# Any OS: everything except the WPF shell
dotnet build SloomyTweaks.CrossPlatform.slnf
dotnet run --project tests/SloomyTweaks.Tests          # all tests
dotnet run --project tests/SloomyTweaks.Tests -- Engine  # filter by name
```

The test project uses a small built-in runner with an xUnit-style API (`[Fact]`, `Assert`), so it needs no NuGet packages.

## Licensing setup (short version)

1. Deploy the server (see [docs/LICENSE-SERVER.md](docs/LICENSE-SERVER.md)) and run `generate-signing-key`, `generate-admin-key`, `generate-pepper`.
2. Copy `src/SloomyTweaks.App/licensing.example.json` to `licensing.json`. Set your HTTPS server URL and paste the **public** key.
3. Build the app. If `licensing.json` is missing, the app runs in an explicit "Licensing not configured" state: browsing and restoring are allowed, applying is not.

## Principles

- No invented benchmarks, numbers or sources. Unknowns are stated as unknown.
- No registry tweak-pack settings, placebo memory cleaners, timer hacks or TCP tweaks.
- Never disables Windows Update, Microsoft Defender, Windows Security or CPU security mitigations.
- Least privilege: runs unelevated; asks for elevation only for system-wide changes.
- Every change: detect → compatibility → privileges → conflicts → back up → explain → apply → verify → log → restore.
