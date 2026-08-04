# OracleHost — Project Knowledge

## What this is
**OracleHost** is a C# WinForms desktop app (Windows-only, dark theme) that
auto-retries launching a **free Oracle Cloud "Always Free" Ampere A1 (ARM)
instance** until capacity frees up. It retries only on capacity/transient
errors and aborts on real problems (quota, bad config) — that retry-vs-abort
logic is the core safety feature.

## Where key code lives
| Path | Purpose |
|---|---|
| `Program.cs` | Entry point: load config → LoginForm → SetupWizard → MainForm flow |
| `Services/OciBrowserLogin.cs` | Browser-based OCI sign-in: OAuth2 auth-code + PKCE flow, localhost callback listener, session persistence + refresh |
| `Forms/LoginForm.cs` | OCI credential entry, API-key generation, opens Oracle console |
| `Forms/SetupWizardForm.cs` | Interactive wizard; auto-detects compartments/subnets |
| `Forms/MainForm.cs` | Hunting dashboard: preflight, try-once, start/stop, activity log |
| `Services/OciService.cs` | OCI SDK wrapper (compute, identity, networking, limits) |
| `Services/ErrorClassifier.cs` | Retry vs abort classification (mirrors Python logic) |
| `Services/ConfigService.cs` | Load/save `~/OracleHost/config.json`, read `~/.oci/config` |
| `Models/AppConfig.cs` | Config model matching the Python `config.json` schema (+ session-token fields) |
| `Models/HuntStatus.cs` | Runtime hunt state |
| `Helpers/CryptoHelper.cs` | RSA key generation + OCI fingerprint computation |
| `Helpers/SessionTokenProtector.cs` | Optional Windows DPAPI protection, migration, and private SDK temp files |

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
  OCI credentials are also auto-detected from `~/.oci/config`.
- **Gotchas**:
  - `System.Drawing.Image` vs `Oci.CoreService.Models.Image` conflict is
    resolved with `using Image = Oci.CoreService.Models.Image;` in OciService.cs
    — keep the alias if touching image code.
  - Inline credentials are written to a temp OCI config under
    `%TEMP%\OracleHost\` and cleaned up on dispose — don't leave that dir lying
    around with keys.
  - **Browser login (session tokens)**: when no saved browser session exists,
    startup opens the login form and Oracle Cloud in the browser. The app can
    capture an Oracle login via
    OAuth2 (Auth Code + PKCE) against an identity domain. The user must register
    an OAuth app (Authorization Code + PKCE, redirect `http://localhost:8181/`)
    in their identity domain. Sessions are stored CLI-compatibly under
    `~/.oci/sessions/<region>/sessions/<id>/` (`config` with `security_token_file`
    + `token` + `refresh_token` + `private_key`) and consumed via the SDK's
    `SessionTokenAuthenticationDetailsProvider` (KeyId is `ST$<token>`).
    `OciService.CreateAuthProvider` prefers sessions over API keys. Existing
    sessions created by the OCI CLI remain readable when explicitly selected,
    while startup prioritizes capturing a browser session.
    Explicit or protected-session failures are surfaced instead of silently
    switching identities; discovered plaintext CLI sessions retain compatibility
    fallback behavior.
  - Windows-only; `NU1101` build errors usually mean the .NET 8 SDK is missing.
  - **AccountInfo credentials**: a private checkout may contain
    `AccountInfo/information.md` plus API PEM files. `Program.cs` derives the
    fingerprint from the public PEM and stores only the private PEM path; it
    never prints or copies the private key. If no SSH public key exists, startup
    creates a separate local `~/.ssh/oraclehost_id_rsa` pair for instance access.
  - **Session-token protection**: `AppConfig.EncryptSessionTokens` (JSON key
    `encrypt_session_tokens`, default `false`) enables Windows DPAPI
    (`DataProtectionScope.CurrentUser`) for the persisted `token` and
    `refresh_token` files. Plaintext OCI CLI sessions remain compatible; when
    enabled, OracleHost migrates them atomically on use. The SDK receives only
    a temporary plaintext token copy, which `OciService.Dispose()` removes.
    Protected files are tied to the same Windows user/install and cannot be
    read by the OCI CLI; a failed DPAPI decrypt requires signing in again.
    Temporary SDK plaintext
    copies use a current-user-only directory, are removed on normal disposal,
    and stale unlocked directories are cleaned on later startup.

  - `OracleHost/` subdirectory in this checkout is **empty** — the project
    lives at the repo root, even though the README says `cd OracleHost`.

## Key behavior notes
- Config defaults: shape `VM.Standard.A1.Flex`, 1 OCPU / 4 GB RAM per hunter launch
  (conservative app setting), randomized 30–60 s retry intervals; account-wide
  A1 limits are detected separately during preflight,
  rotates availability domains (honoring the `availability_domains` setting:
  `"all"`, one AD, or a comma-separated list, resolved once per hunt),
  `stop_on_limit` aborts on `LimitExceeded`.
- Error classification: `InternalError`/"Out of host capacity"/429/5xx/timeouts
  → retry; `LimitExceeded`, invalid params, auth errors → abort immediately.
  `ErrorClassifier` reads the typed `Oci.Common.Model.OciException`
  (`ServiceCode`/`StatusCode`) — no reflection.
- `Helpers/SessionTokenProtector.cs` owns the marked DPAPI file format and
  atomic persistence. Do not pass protected token paths directly to the OCI
  SDK; `OciService` must materialize and clean up its temporary SDK copy.
