# Sloomy Tweaks — Architecture

## Technology choices

| Concern | Choice | Why |
|---|---|---|
| Language/runtime | C# 12, .NET 8 (LTS) | Long-term support, mature Windows interop. |
| UI | **WPF** | WinUI 3 needs the Windows App SDK runtime and has had a less stable desktop story. WPF ships in the .NET SDK, has full control over templating (custom title bar via `WindowChrome`, custom controls), low dependencies and a proven performance record. |
| Pattern | MVVM; view models in a separate, UI-agnostic assembly | View models are unit-tested on any OS; views are pure XAML data templates with no code-behind logic. |
| DI | Constructor injection with a manual composition root (`App.xaml.cs`) | Keeps the client free of third-party packages; every dependency is visible in one place. |
| Local storage | SQLite through Windows' built-in `winsqlite3.dll` | No bundled native binaries. The same wrapper uses `libsqlite3` on Linux for the server and CI. |
| Windows integration | Documented Win32 APIs via P/Invoke; no PowerShell, no WMI dependency | `PowerSetActiveScheme`, `SystemParametersInfo`, `GetLogicalProcessorInformationEx`, `EnumDisplayDevices`, `SRSetRestorePointW`, `CryptProtectData`, `EnumServicesStatusEx`, `Microsoft.Win32.Registry` (64-bit view). |
| Licensing | ASP.NET Core minimal API + SQLite; ECDSA P-256 signed tokens | Server is the only authority. The client holds public keys only. |
| Logging | JSON Lines files, daily rotation, retention | Structured, greppable, no dependency. |

## Layers

```
App (WPF views, host services) ──► Presentation (view models)
        │                                   │
        ▼                                   ▼
Platform.Windows (Win32 implementations) ─► Core (abstractions, engine, catalog, stats, licensing client)
Data (SQLite stores) ──────────────────────► Core
LicenseServer ─► Licensing.Contracts ◄─ Core
```

Core never touches the operating system directly. It depends on `IRegistryAccess`, `IPowerManagement`, `IMouseSettings`, `IPrivilegeService`, `IHardwareInfoProvider` and similar interfaces. That is what makes the apply/verify/rollback engine testable with fakes.

## Optimization model

- **`OptimizationDefinition`**: evidence-registry metadata (mechanism, benefit without numbers, evidence level, what is unknown, sources, risks, compatibility, admin/restart flags, conflicts, manual steps).
- **`ISettingPrimitive`**: one atomic, reversible change, with `Read`, `IsDesired` (true/false/unknown), `ApplyDesired`, `Restore(original)` and `IsRestored`. Implementations:
  - `RegistryDwordPrimitive`: records absence so restore deletes the value instead of guessing a default.
  - `RegistryStringTokenPrimitive`: edits one `Key=Value;` token (e.g. `DirectXUserGlobalSettings`) and preserves the others, so a restore never undoes unrelated Windows graphics settings.
  - `PowerSchemePrimitive`, `PointerAccelerationPrimitive`.
- **`Optimization`**: definition + primitives + compatibility function. The kinds are `Actionable`, `Guidance` (no supported API, so the card shows instructions) and `NotOffered` (explains why something popular is excluded).

## Safe workflow (`OptimizationEngine.ApplyAsync`)

1. Read current values of every primitive.
2. Compatibility check against `SystemContext` (build, edition/LTSC, CPU vendor/X3D/hybrid, GPUs). `Unsupported` blocks the change. `CompatibleWithWarnings` and `Unverified` require explicit acknowledgement.
3. Administrator check (no write is attempted without rights).
4. Conflict check against `ConflictsWith`.
5. If already in the desired state, stop with no writes.
6. Save original values (the earliest backup is kept if one exists). Optionally create a System Restore point once per session before the first system-wide change.
7. Apply each primitive. On any exception, roll back the primitives already applied in reverse order.
8. Read back and verify. If Windows did not keep the value (policy, another tool), roll back and report `VerificationFailed`.
9. Append a history record with before/after values. Log a structured event.
10. Restore is available from the card and the Restore Center (license not required).

`RestartPending` is derived from history: a successful operation after the last boot time on an optimization that needs a restart.

## Licensing

- Key format `SLMY-XXXXX-XXXXX-XXXXX-XXXXX` (Crockford base32, 95 random bits, one checksum symbol for typo detection only).
- The server stores only HMAC-SHA256(pepper, key) and the last 5 characters as a hint.
- Activation binds a device id. On the client this is SHA-256 of an app prefix plus the Windows `MachineGuid`, so it uses no hardware serials or MAC addresses. Device limits apply, and the same device re-activating does not consume a slot.
- Token `ST1.<payload>.<signature>`, ECDSA P-256/SHA-256. The claims are license id, device id, plan, issued, token expiry (default 72 h) and license expiry. Key ids allow key rotation.
- The client verifies the signature and device binding locally. It refreshes every 12 h. If the server is unreachable it works offline until the token's own expiry, then locks premium features. Revocation and expiry are learned on the next refresh.
- The admin API (`/admin/api`, `X-Admin-Key` header) and the dashboard (`/admin`) exist only on the server. The admin key is stored as a SHA-256 hash and compared in constant time. The admin endpoints and the client endpoints have separate rate limits.

## Benchmarking

Sloomy Tweaks does not generate measurements. Users capture frame times with PresentMon (or a compatible tool) and import the CSV. `FrameTimeAnalyzer` computes average FPS, 1% and 0.1% lows (mean of slowest frames), median/P95/P99/max frame time and a histogram. `BenchmarkComparer` requires ≥3 runs per configuration and uses Welch's t-test (α = 0.05) with a 95% confidence interval. Results are never declared from a single run. End-to-end latency requires external hardware, and the UI says so.
