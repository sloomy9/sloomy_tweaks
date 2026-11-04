# Sloomy Tweaks

Evidence-based Windows gaming optimization for Windows 11. Every optimization Sloomy Tweaks offers states its mechanism, evidence level, sources and risks. Each one is applied only after the current configuration has been read and saved. The result is verified by reading it back, and any change can be reversed from the Restore Center, with or without a license.

> **Status: 0.2.0-rc1 — not production-ready.** Everything compiles; 95 automated tests pass; the WPF app was run under Wine. It has not yet been tested on a Windows VM or real hardware, and no production license server is deployed. See [docs/STATUS.md](docs/STATUS.md) and [docs/AUDIT.md](docs/AUDIT.md).

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
tools/
  SloomyTweaks.UiPreview            QA-only harness for running the UI under Wine. Not in the solution,
                                    never shipped (uses a managed ECDSA verifier because Wine lacks NCrypt).
deploy/license-server               Dockerfile + docker-compose + Caddy (automatic HTTPS)
docs/
  ARCHITECTURE.md  AUDIT.md  EVIDENCE.md  STATUS.md  LICENSE-SERVER.md
installer/
  SloomyTweaks.iss                  Inno Setup script for the signed installer
```

## Building

Requirements: .NET 8 SDK. The WPF app needs Windows. Everything else builds on Windows, Linux or macOS.

```powershell
# Windows: full build, tests, self-contained publish, optional signing
./build.ps1 -Configuration Release
./build.ps1 -Configuration Release -SignThumbprint <certificate thumbprint>

# Any OS: everything, including the WPF shell (Windows targeting pack)
dotnet build src/SloomyTweaks.App -c Release -p:EnableWindowsTargeting=true
dotnet publish src/SloomyTweaks.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableWindowsTargeting=true -p:DebugType=none -o out/release

# Any OS: everything except the WPF shell
dotnet build SloomyTweaks.CrossPlatform.slnf
dotnet run --project tests/SloomyTweaks.Tests          # all tests
dotnet run --project tests/SloomyTweaks.Tests -- Engine  # filter by name
```

The test project uses a small built-in runner with an xUnit-style API (`[Fact]`, `Assert`), so it needs no NuGet packages.

## Licensing setup (short version)

The app is usable only with a valid license: the first screen is activation. Without a license only the Restore Center is reachable (to undo earlier changes).

1. Deploy the server on a real host (see [docs/LICENSE-SERVER.md](docs/LICENSE-SERVER.md)) and generate the signing key, admin key and pepper **on the server**.
2. Copy `src/SloomyTweaks.App/licensing.example.json` to `licensing.json`. Keep `"environment": "production"`, set your `https://` server URL and paste the **public** key. The app refuses http, localhost, the example domain and any private key in production.
3. Build the app. If `licensing.json` is missing or invalid, the activation screen says licensing is not configured and nothing can be unlocked.

**Development mode** (testing only): `"environment": "development"` with an `http://127.0.0.1:…` URL. The title bar then shows **DEVELOPMENT MODE**. Never ship such a build to customers.

## Principles

- No invented benchmarks, numbers or sources. Unknowns are stated as unknown.
- No registry tweak-pack settings, placebo memory cleaners, timer hacks or TCP tweaks.
- Never disables Windows Update, Microsoft Defender, Windows Security or CPU security mitigations.
- Least privilege: runs unelevated; asks for elevation only for system-wide changes.
- Every change: detect → compatibility → privileges → conflicts → back up → explain → apply → verify → log → restore.
