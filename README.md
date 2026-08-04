# OracleHost — Always Free ARM Instance Hunter (Windows Desktop)

Oracle's Always Free tier offers an **Ampere A1 (ARM) instance within its published Always Free limits** —
but capacity is chronically sold out and Oracle's limits/policies can change. The community answer is an
**auto-retry client**: keep asking Oracle to create the instance every few minutes until capacity opens up.
This project is exactly that, as a **C# WinForms desktop application** for Windows.

> **⚠️ 2026 change:** reports indicate Oracle reduced the Always Free A1 allocation in mid-2026 (commonly
> **2 OCPUs / 12 GB RAM total**, was 4/24) — but it can vary by account and may be grandfathered. That's why
> the tool **auto-detects your real limit** during preflight and warns if your config asks for more.
> Always Free egress is **10 TB/month**, not 1 GB.

---

## Why this instead of the web console

- **No browser, no clicking.** Uses the official OCI SDK.
- **Smart retry.** Retries only on *capacity / transient* errors; aborts immediately on real problems
  (quota limits, bad config) so it never hammers the API pointlessly.
- **Rotates availability domains.** Capacity often frees up in only one AD.
- **Polite pacing.** Randomized 30–60 s intervals by default, so repeated capacity checks are not sent at a fixed cadence.
- **Preflight checks** validate credentials, detect limits, and warn before hunting.
- **Conservative hunter protection** blocks non-A1 shapes, caps each launch at **1 OCPU / 4 GB RAM**,
  fixes the boot volume at 50 GB (OCI's required minimum), and never requests a reserved public IP.
  The account-wide preflight still checks your detected Always Free allowance and existing usage.
- **Dark-themed Windows GUI** with a login form, setup wizard, and a live hunting dashboard.

> **Pro tips from the community:**
> - **Do not upgrade to Pay-As-You-Go just to improve capacity odds** unless you understand OCI billing.
>   Always Free resources are intended to remain free within the published limits, but PAYG accounts can be
>   charged for resources or usage outside those limits.
> - **The free x86 micro (VM.Standard.E2.1.Micro, 1/8 OCPU / 1 GB) is only available in AD-1** in most regions.
>   It's much easier to grab than an A1, and can run a small service while you hunt for ARM.

---

## Quick start

### 1. Prerequisites

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer)
- An Oracle Cloud account (free tier)

### 2. Build & run

From the project root:

```bash
dotnet build
dotnet run
```

The application launches with a dark-themed GUI. If no captured browser session is saved, it opens the
login form and **opens Oracle Cloud in your browser** so you can complete the browser sign-in flow.

### 3. Sign in with Oracle (pick one)

**Option A — Browser sign-in (recommended, no API keys needed).** The browser flow uses OAuth2
(Authorization Code + PKCE) against your identity domain and stores the captured security token as a
reusable session:

1. In your identity domain, register an OAuth app
   (Identity & Security → Identity domains → your domain → Applications → Add application):
   grant type **Authorization Code + PKCE**, redirect URI **`http://localhost:8181/`**.
2. In the login form, paste the **identity domain URL** and the OAuth **client ID**.
3. Click **Sign in with Oracle** and complete the sign-in in your browser.

**Option B — API keys.** Enter your Tenancy OCID, User OCID, Fingerprint, and API key path, or click
**Generate API Keys** to create them. Click **Open Oracle Console** for the OCI user API-key settings;
do not use the separate Analytics & AI API-keys page. If you already have `~/.oci/config`, the login form
auto-detects it and pre-fills these fields for you.

**Option C — the `AccountInfo` folder.** A private checkout convention for dropping your OCI credentials
next to the source tree. See [AccountInfo folder](#accountinfo-folder-optional) below.

### 4. Run the setup wizard (first run only)

The Setup Wizard auto-detects your compartments and subnets from your OCI account. Pick from the dropdowns
and click Save. If you don't have a VCN yet:

1. Networking → Virtual cloud networks → **Create VCN** (any name, defaults OK).
2. Open the VCN → **Create subnet** (public subnet).
3. Add an **ingress security rule** allowing **TCP 22 from 0.0.0.0/0** so you can SSH in later.

### 5. Run the hunt

On the main dashboard, hit **✓ Preflight Check** first (validates credentials and shows your detected
Always Free limits), then **▶ Start Hunting**. On success you'll get the instance OCID and public IP,
ready to `ssh opc@<ip>`.

The dashboard provides:

- **✓ Preflight Check** — Validates credentials, detects limits, shows existing instances
- **⚡ Try Once** — Single launch attempt
- **▶ Start Hunting** — Continuous retry loop with live status updates
- **⏹ Stop** — Cancel the current hunt

---

## AccountInfo folder (optional)

The `AccountInfo` folder is an **optional shortcut**: put your OCI credentials there and OracleHost
loads them automatically at startup — no typing OCIDs into the login form.

> **⚠️ This folder is private. Never commit it, zip it, or share it.** It contains your API private key
> and your account OCIDs. The whole `AccountInfo/` folder is gitignored by default so `git add .`
> will never pick it up — but double-check before any manual `git add` that nothing from it is staged.
> The only exception is `information.example.md`, a sanitized, shareable template committed with the repo.

### Folder layout

```
AccountInfo/
├── information.md            # your OCIDs — see template below
├── information.example.md    # committed sanitized template — rename to information.md and fill in
├── <name>.pem                # private key — exactly ONE, filename must NOT contain "public"
└── <name>_public.pem         # optional — public key, filename MUST contain "public"
```

### `information.md` format

Label on its own line, value on the next line:

```
Tenancy OCID
ocid1.tenancy.oc1..<your-value>

Home region
IAD

User OCID
ocid1.user.oc1..<your-value>

Compartment OCID
ocid1.compartment.oc1..<your-value>

Subnet OCID
ocid1.subnet.oc1..<your-value>

Fingerprint
aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99
```

| Field | Required? | Notes |
|---|---|---|
| `Tenancy OCID` | ✅ yes | |
| `User OCID` | ✅ yes | |
| `Compartment OCID` | ✅ yes | |
| `Subnet OCID` | ✅ yes | |
| `Home region` | no | e.g. `IAD`. Maps to a region code for the API |
| `Fingerprint` | no | If present, OracleHost verifies it matches the key |

### The rules (what makes it work)

- **Exactly one private `.pem`** must be present. Its filename must **not** contain the word `public`.
- **At most one public `.pem`** is allowed. Its filename **must** contain `public`.
- All four required OCIDs above must be filled in, or OracleHost ignores the folder.
- OracleHost **derives the fingerprint from the key** — you don't strictly need to write it down.

### How OracleHost finds it

On startup, OracleHost walks up the directory tree from the working directory (and the EXE location)
looking for a folder named `AccountInfo` that contains both `information.md` and a `*.pem`. Place it
next to the project root and it just works.

### Prefer doing it manually?

You don't need this folder at all. The equivalent manual setup is:

1. Go to **cloud.oracle.com** → click your **profile icon** → **User settings** and open the OCI user
   API-key settings (not **Analytics & AI → API keys**).
2. Use the OCI account's supported key-pair option and keep the downloaded private PEM on this computer.
3. Copy the OCI fingerprint shown for that signing key (e.g. `a1:2b:3c:...`).
4. Place the private key at `C:\Users\<you>\.oci\oci_api_key.pem` and enter the values in the login form.

### Protect session token files (optional but recommended)

Enable the **encrypt session tokens** checkbox in the browser login or setup wizard. The encrypted files
can only be decrypted by the same Windows user on the same Windows installation. Existing plaintext
session files are migrated the next time OracleHost uses them. OracleHost decrypts only a temporary token
copy for the OCI SDK and removes it when the OCI service is disposed.

---

## Project structure

```
OracleHost.csproj          # .NET 8 WinForms project, OCI SDK NuGet refs
Program.cs                 # Entry point: login → wizard → main form flow
Forms/
├── LoginForm.cs           # OCI credential entry + key generation + browser launch
├── SetupWizardForm.cs     # Interactive config wizard with auto-detect
└── MainForm.cs            # Hunting dashboard with live status + activity log
Services/
├── OciService.cs          # OCI SDK wrapper (compute, identity, networking, limits)
├── OciBrowserLogin.cs     # OAuth2 auth-code + PKCE browser sign-in, session persistence
├── ErrorClassifier.cs     # Retry vs abort error classification
└── ConfigService.cs       # JSON config load/save + ~/.oci/config reader
Models/
├── AppConfig.cs           # Configuration model (config.json schema)
└── HuntStatus.cs          # Runtime hunt state tracking
Helpers/
├── CryptoHelper.cs        # RSA key generation + OCI fingerprint computation
├── SessionTokenProtector.cs  # Optional Windows DPAPI token protection + temp-file handling
└── DiagnosticLog.cs       # Diagnostic logging
```

---

## Config reference (`config.json`)

The app stores its config at `%USERPROFILE%\OracleHost\config.json`.

| Key | Default | Meaning |
|---|---|---|
| `region` | `null` | Region override (defaults to `~/.oci/config`) |
| `session_config_path` | auto | Browser-captured session config (falls back to newest session under `~/.oci/sessions/`) |
| `oci_identity_domain` | `null` | Identity domain URL used for browser sign-in |
| `oci_oauth_client_id` | `null` | OAuth client ID used for browser sign-in |
| `encrypt_session_tokens` | `false` | Protect browser-session `token` and `refresh_token` files with Windows DPAPI for the current user |
| `compartment_ocid` | — | **required** — compartment to launch into |
| `subnet_ocid` | — | **required** — subnet to attach the instance to |
| `ssh_public_key_path` | `~/.ssh/id_rsa.pub` | Your public key (must start with `ssh-rsa`) |
| `shape` | `VM.Standard.A1.Flex` | Instance shape (the free ARM one) |
| `ocpus` | `1` | OCPUs per hunter launch (conservatively capped at 1) |
| `memory_in_gb` | `4` | RAM per hunter launch (conservatively capped at 4 GB) |
| `image_os` | `Oracle Linux` | OS to resolve (`Oracle Linux`, `Canonical Ubuntu`, …) |
| `image_version` | `latest` | `latest` or a substring of the image display name |
| `display_name` | `free-tier-arm` | Instance name |
| `assign_public_ip` | `true` | Attach a public IP |
| `boot_volume_size_gb` | `50` at launch | OCI-required minimum boot volume; OracleHost blocks values above 50 GB and the published Always Free pool is 200 GB total across boot/block volumes |
| `availability_domains` | `"all"` | `"all"`, one AD, or a list of ADs |
| `min_interval_seconds` | `30` | Minimum randomized retry delay in seconds |
| `max_interval_seconds` | `60` | Maximum randomized retry delay in seconds |
| `stop_on_limit` | `true` | Abort on `LimitExceeded` instead of retrying |
| `max_attempts` | `0` | `0` = unlimited |
| `allow_existing` | `false` | Skip the "instance already exists" sanity check |

---

## Session-token protection

Session encryption is **optional and disabled by default** to preserve compatibility with the OCI CLI.
When `encrypt_session_tokens` is `true`:

- OracleHost wraps `token` and `refresh_token` with Windows DPAPI using `DataProtectionScope.CurrentUser`.
- The setting is stored in `%USERPROFILE%\OracleHost\config.json`.
- Existing plaintext files are migrated atomically when the session is used.
- A session protected for another Windows account or installation cannot be recovered by OracleHost;
  sign in again to create a new one.
- The OCI CLI and other tools cannot read OracleHost's DPAPI-wrapped files. Disable encryption only for a
  newly created/shared plaintext session; already protected files remain protected until you explicitly
  recreate the session.
- The private key remains in its existing session file because OCI session signing still requires the SDK
  to read it.
- The temporary SDK copy is protected by a current-user-only directory and is removed during normal
  shutdown. Cleanup is best effort; if the process crashes, stale `OracleHost/session-*` directories are
  removed on a later startup when they are not locked by another running instance.

The encrypted file format has an OracleHost marker and is not a replacement for Windows account security
or full-disk encryption.

---

## How retry-vs-abort works

| Error | Action |
|---|---|
| `InternalError` / "Out of host capacity" | **Retry** (this is the one you're waiting for) |
| `TooManyRequests` (429), 500/503 | **Retry** |
| Network timeouts / connection errors | **Retry** |
| `LimitExceeded` | **Abort** (you've used your free quota — retrying is pointless) |
| `InvalidParameter`, `BadRequest`, auth errors | **Abort** (config bug — fix and rerun) |

This is the key safety feature: a naive `while true; launch; sleep 60` loop will happily spam a broken
request forever. OracleHost spaces capacity checks with a randomized 30–60 second delay and stops to tell
you what to fix instead.

---

## Tips to actually get an instance

- **Upgrade to PAYG** (stays free within Always Free limits) — dramatically better capacity odds, but not required.
- **Pick a less busy region** — communities report better luck in regions like `ap-mumbai-1`,
  `eu-frankfurt-1`, or newer home regions than in the crowded US-East.
- **Run off-peak** — late night / early morning local time.
- **Keep it running** — leave the app hunting in the background; capacity can free up at any moment.
- **Rotate ADs** — the default `"all"` handles this for you. A1 is available in any availability domain
  (per the console), so all ADs are fair game.
- **Watch your total** — the hunter requests only 1 OCPU / 4 GB per launch, while the account-wide A1
  allowance can vary. The preflight shows your detected limit and existing usage before hunting.
- **Grab a micro meanwhile** — the free `VM.Standard.E2.1.Micro` x86 instance (AD-1 only) can be created
  almost immediately and gives you a working VPS while the A1 hunt continues.

> **Keep your free instance busy!** Oracle reclaims Always Free instances that stay idle
> (95th-percentile CPU/network/memory all below 20% for 7 straight days). Run a real workload or a
> keep-alive so your free box isn't reclaimed.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `OCI credential problem` | `~/.oci/config` missing/invalid — redo credentials setup or re-enter them in the login form |
| `SSH public key not found` | `ssh-keygen -t rsa -b 2048 -f ~/.ssh/id_rsa` then re-run |
| `No ... images found` | Try `"image_os": "Canonical Ubuntu"` or check the region |
| `None of the requested ADs exist` | Delete the `availability_domains` key or use `"all"` |
| `You already have ... A1 instance(s)` | Delete an instance, reduce `ocpus`/`memory_in_gb`, or set `"allow_existing": true` |
| Preflight says config exceeds detected limit | The hunter defaults to 1 OCPU / 4 GB; verify your account's detected A1 allowance and existing usage |
| Instance created but SSH fails | Check the security list allows TCP 22; wait ~2 min for boot |
| Build error `NU1101: Unable to find package` | Ensure you have .NET 8+ SDK installed (`dotnet --version`) |
| `SimpleAuthenticationDetailsProvider` errors | Ensure your `~/.oci/config` file is valid or re-enter credentials in the login form |
| OracleHost ignores my `AccountInfo` folder | Check the folder rules in [AccountInfo folder](#accountinfo-folder-optional): one private PEM, four required OCIDs |

---

## Disclaimer

Auto-retry against Oracle's free tier is a widely-used community practice, but it is not officially
supported by Oracle. Keep intervals polite (the defaults are fine) and stay within the Always Free limits
to reduce charge risk. No client application can guarantee a zero bill if the account is upgraded, other
paid resources already exist, or Oracle changes its pricing/policies.
