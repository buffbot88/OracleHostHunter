# Changelog

## 2.0.0 (2026-09-23)

### Authentication
- **Removed browser login entirely.** Deleted `Forms/LoginForm.cs`, `Services/OciBrowserLogin.cs`,
  and `Helpers/SessionTokenProtector.cs`. There is no login screen, no OAuth, and no session tokens.
- **AccountInfo-only auth.** All credentials come from the local `AccountInfo/` folder:
  `information.md` (OCIDs) plus exactly one private `.pem`. Startup adopts the bundle, shows a
  single clear error if it is incomplete, and exits.
- **Startup report dialog** (`AccountInfoReportForm`): when the AccountInfo bundle is incomplete,
  a dialog lists every checked field as found/missing with fix hints.
- **Fingerprint handling simplified**: the fingerprint is derived from the key pair
  (`CryptoHelper.ComputeOciFingerprint`); an optional `Fingerprint` field in `information.md` is
  verification-only. OpenSSH-format keys are no longer usable — keys must be classic RSA/PKCS#8 PEM.
- Removed session-token config keys (`session_config_path`, `oci_identity_domain`,
  `oci_oauth_client_id`, `encrypt_session_tokens`) and the `HasSession` flow.
- Fixed `EnsureSshPublicKey`: a missing `.pub` half is now derived from the existing private key
  instead of leaving `SshPublicKeyPath` pointing at a nonexistent file.

### Shape selection & sizing
- **Added `VM.Standard.A1.Flex` (ARM)** alongside `VM.Standard.E2.1.Micro`, selectable in Settings.
- **OCPUs and Memory GB settings**, editable for both shapes and persisted for both. At launch they
  are sent to OCI only for A1.Flex (the micro shape is fixed and rejects a shape config).
- **Boot volume GB slider** (50–200 GB) replaces the hardcoded 50 GB.
- **Always Free guardrails updated**: A1 capped at **2 OCPU / 12 GB** (Oracle reduced the allotment
  from 4 OCPU / 24 GB); validation, launch clamps, and preflight counting are all shape-aware
  (`standard-a1-core-count` vs `standard-e2-micro-core-count`).
- Fixed a startup bug where the saved shape was silently reset to E2.1.Micro on every launch
  (`MigrateLegacyShape` is now a no-op) and where AccountInfo adoption dropped the saved
  Shape/OCPUs/Memory from config.

### Settings wizard
- **Removed Compartment, Subnet, and Region fields** plus the "Auto-Detect Compartments & Subnets"
  button — these values come only from `AccountInfo/information.md`.
- The wizard is now purely instance settings: shape, OCPUs, memory, boot volume, display name,
  retry intervals.
- Saving no longer overwrites OCIDs (it preserves the AccountInfo-derived values).

### UI (v2 refresh)
- Modernized dark theme: Segoe UI Variable (Display/Text) typography, unified orange accent
  (`#F97322`), softer card panels, orange accent strip on the status card, borderless activity log,
  uppercase "ACTIVITY" caption, and a **v2.0** badge in the header.
- Setup Wizard retitled "⚙ Settings".
- Removed the shape/region summary line under the main title.

### Housekeeping
- `config.json` is only rewritten when the AccountInfo bundle actually changes (SHA-256 bundle
  fingerprint stored in config).
- Version bumped to `2.0.0`; docs updated to match.

## 1.0.0 (2026-08-04)

- Initial release: WinForms Always Free micro hunter with preflight, retry-vs-abort error
  classification, AD rotation, tray minimize, and success notifications.
