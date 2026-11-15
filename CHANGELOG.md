# Changelog

## 0.5.0 (2026-11-15)

### SSH diagnostics
- **Read-only SSH readiness preflight**: validates public-key format, private-key pairing, the
  selected image/cloud-init setting, and TCP 22 ingress across the applicable subnet security lists
  and only the NSGs the launch actually attaches (`VnicNsgIds`). Reports `Allowed`, `Blocked`, or
  `Unknown` (never `Blocked` when a permission gap prevents full inspection), and keeps public-IP
  problems separate from ingress problems.
- **Launch results now distinguish RUNNING from reachable**: after RUNNING the app probes TCP 22 and
  then attempts an authenticated login. Wording is explicit — "SSH port reachable (login not
  verified)" is never reported as a verified login.
- **Persistent host-key trust**: first contact shows the `ssh-keyscan` fingerprints and warns that
  keyscan does not authenticate the server; approval is required before any login. Keys are stored
  per instance OCID in `%LOCALAPPDATA%\OracleHost\known_hosts` (valid OpenSSH syntax) and enforced
  with `StrictHostKeyChecking=yes`. A changed key blocks verification until explicitly reviewed, and
  an OCID's entries can never overwrite another instance's.
- **Optional Oracle Linux cloud-init** (`EnableCloudInit`, off by default) records `sshd` and
  `firewalld` step exit codes to `/var/log/oraclehost-provision.log`; after a verified login the app
  reads it back read-only and reports each step as Success, Failed, or Unknown. A missing log is
  reported as Not Applicable/Missing, never as an authentication failure.
- **Settings** gained a `5 · SSH` section for the private-key path and both toggles.
- Diagnostics never terminate, retry, or modify an instance.

## 0.4.0 (2026-10-11)

### Onboarding & UI
- **First-run walkthrough**: a three-step tour (Check setup → Try once → Start hunting) spotlights
  each real button and explains it. Skip or finish saves `HasSeenOnboarding` to `config.json`, so it
  shows once. Replay anytime with the **?** button in the header.
- **Refreshed dark theme**: rounded inputs with orange focus rings, styled dropdowns and sliders,
  and reusable section/chip/badge styles.
- **Simpler dashboard**: numbered step buttons, a 2×2 stat grid, and compact environment chips.
- **Grouped Settings**: four numbered cards (servers, shape & size, storage & name, retry pace)
  in a scrollable window with a sticky save button.

### Always Free limits
- Encoded the current allowances as constants: A1 2 OCPU / 12 GB per month (1,500 OCPU hours and
  9,000 GB hours), 20 GB object storage, up to 5 volume backups, and the database and NoSQL/MySQL
  caps.

### Fixes
- Fixed build errors: duplicate `DisplayNameBase`, missing `HasCredentials`, a stray parenthesis,
  a duplicate local name in the hunt loop, a missing try-once helper, and an invalid theme template.

### Release & CI
- GitHub Actions CI builds the project in Release on every push and pull request to `main`.
- Pushing a `v*` tag builds, verifies the tag matches the project version, publishes a win-x64 zip,
  and creates a GitHub Release. Credentials and `config.json` are never packaged.

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
