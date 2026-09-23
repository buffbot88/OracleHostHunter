# OracleHost — Project Knowledge

## What this is
**OracleHost** (v2) is a C# WinForms desktop app (Windows-only, modern dark theme using
Segoe UI Variable and an orange `#F97322` accent) that auto-retries launching a free Oracle Cloud
**Always Free** instance until capacity frees up. Two supported shapes, selectable in ⚙ Settings:
`VM.Standard.E2.1.Micro` (x86, fixed 1/8 OCPU / 1 GB, up to 2 instances) and `VM.Standard.A1.Flex`
(ARM, flexible, Always Free cap **2 OCPU / 12 GB** — Oracle reduced this from 4/24). It retries only
on capacity/transient errors and aborts on real problems (quota, bad config) — that retry-vs-abort
logic is the core safety feature.

## Where key code lives
| Path | Purpose |
|---|---|
| `Program.cs` | Entry point: load config → adopt AccountInfo credentials → validate → MainForm flow (no wizard loop) |
| `Forms/SetupWizardForm.cs` | ⚙ Settings dialog: shape, OCPUs, memory, boot volume, display name, retries. No compartment/subnet/region fields — those come from AccountInfo only |
| `Forms/MainForm.cs` | Hunting dashboard: preflight, try-once, start/stop, activity log, v2 styling |
| `Forms/AccountInfoReportForm.cs` | Startup dialog listing found/missing AccountInfo fields |
| `Services/OciService.cs` | OCI SDK wrapper (compute, identity, networking, limits); sends ShapeConfig only for A1.Flex |
| `Services/ErrorClassifier.cs` | Retry vs abort classification (mirrors Python logic) |
| `Services/ConfigService.cs` | Load/save `~/OracleHost/config.json`, read `AccountInfo/information.md`, AccountInfo bundle fingerprint |
| `Services/AccountInfoReport.cs` | Field-by-field AccountInfo inspection for the report dialog |
| `Models/AppConfig.cs` | Config model matching the Python `config.json` schema |
| `Models/HuntStatus.cs` | Runtime hunt state |
| `Helpers/CryptoHelper.cs` | RSA key generation + OCI fingerprint computation |
| `Helpers/DiagnosticLog.cs` | Diagnostic logging |

## Commands
```bash
dotnet build   # build (needs .NET 8+ SDK on Windows)
dotnet run     # launch the GUI
```
No test or lint infrastructure exists in this repo.

## Conventions & constraints
- **Target**: `net8.0-windows`, WinForms, `Nullable` + `ImplicitUsings` enabled.
- **NuGet**: `OCI.DotNetSDK.*` v69.* (Common/Core/Identity/Limits) and
  `Newtonsoft.Json` 13.* — all floating versions (`69.*`, `13.*`).
- **Config storage**: `%USERPROFILE%\OracleHost\config.json` (not project-local).
- **Authentication is AccountInfo-only**: there is no login form, no browser
  sign-in, and no session-token support. Startup requires a local
  `AccountInfo/` folder (see below); if credentials are missing the app shows
  a field-by-field report dialog and exits.
- **Shape rules**: only E2.1.Micro and A1.Flex pass validation. A1 sizing is
  capped at 2 OCPU / 12 GB (`AppConfig.AlwaysFreeA1OcpuCount`/`AlwaysFreeA1MemoryGb`
  — single source of truth used by the wizard UI, validation, launch clamps,
  and preflight). OCPUs/memory persist for both shapes but are transmitted
  only for A1.Flex.
- **Settings saves preserve OCIDs**: the wizard never writes Compartment,
  Subnet, or Region — `Program.cs` adopts those from AccountInfo on every
  startup, and `MigrateLegacyShape` is a deliberate no-op (it used to
  force-reset the shape to Micro each launch — do not reintroduce that).
- **Gotchas**:
  - `System.Drawing.Image` vs `Oci.CoreService.Models.Image` conflict is
    resolved with `using Image = Oci.CoreService.Models.Image;` in OciService.cs
    — keep the alias if touching image code.
  - Inline credentials are written to a temp OCI config under
    `%TEMP%\OracleHost\` and cleaned up on dispose — don't leave that dir lying
    around with keys.
  - Private keys must be classic PEM (`BEGIN RSA PRIVATE KEY`) or PKCS#8
    (`BEGIN PRIVATE KEY`); OpenSSH-format keys (`BEGIN OPENSSH PRIVATE KEY`)
    fail. The fingerprint is derived from the key pair; the `Fingerprint`
    field in information.md is verification-only.
  - Windows-only; `NU1101` build errors usually mean the .NET 8 SDK is missing.
  - **AccountInfo credentials (required)**: the checkout must contain
    `AccountInfo/information.md` plus API PEM files. `Program.cs` searches up
    the directory tree from both the working directory and the EXE location,
    derives the fingerprint from the public PEM, and stores only the private
    PEM path; it never prints or copies the private key. If an explicit
    AccountInfo bundle exists but is unusable, startup stops with an error.
    If no SSH public key exists, startup creates a separate local
    `~/.ssh/oraclehost_id_rsa` pair for instance access.
  - `OciService.CreateAuthProvider` uses inline API keys first, then falls
    back to `~/.oci/config` if present.
  - `OracleHost/` subdirectory in this checkout is **empty** — the project
    lives at the repo root, even though the README says `cd OracleHost`.

## Key behavior notes
- Config defaults: shape `VM.Standard.E2.1.Micro`; randomized 30–60 s retry intervals; the quota
  (`standard-e2-micro-core-count` or `standard-a1-core-count`) is detected during preflight, and
  `ListShapes` per AD determines which availability domains offer the shape;
  rotates availability domains (honoring the `availability_domains` setting:
  `"all"`, one AD, or a comma-separated list, resolved once per hunt),
  `stop_on_limit` aborts on `LimitExceeded`.
- config.json is only rewritten at startup when the AccountInfo bundle's SHA-256
  fingerprint (stored as `account_info_fingerprint`) differs from the saved one.
- Error classification: `InternalError`/"Out of host capacity"/429/5xx/timeouts
  → retry; `LimitExceeded`, invalid params, auth errors → abort immediately.
  `ErrorClassifier` reads the typed `Oci.Common.Model.OciException`
  (`ServiceCode`/`StatusCode`) — no reflection.
