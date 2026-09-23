# OracleHost v2 — Always Free Instance Hunter (Windows Desktop)

Oracle's Always Free tier includes two VM shapes this app can hunt:

- **VM.Standard.E2.1.Micro** (x86) — fixed at 1/8 OCPU / 1 GB RAM, up to 2 instances per tenancy
- **VM.Standard.A1.Flex** (ARM Ampere) — flexible sizing, Always Free allotment of **2 OCPU / 12 GB**
  (Oracle reduced this from the original 4 OCPU / 24 GB)

Capacity for both can be sold out in busy regions. The community answer is an **auto-retry client**:
keep asking Oracle to create the instance every few minutes until capacity opens up. This project is
exactly that, as a **C# WinForms desktop application** for Windows.

> The hunter **auto-detects your shape quota** (`standard-e2-micro-core-count` or
> `standard-a1-core-count`) during preflight and **probes which availability domains actually offer
> the shape** via `ListShapes`, so attempts are never wasted on ADs that cannot serve it. Always Free
> egress is **10 TB/month**, not 1 GB.

See [CHANGELOG.md](CHANGELOG.md) for what changed in v2.

---

## Why this instead of the web console

- **No browser, no clicking.** Uses the official OCI SDK.
- **Smart retry.** Retries only on *capacity / transient* errors; aborts immediately on real problems
  (quota limits, bad config) so it never hammers the API pointlessly.
- **Rotates availability domains.** Capacity often frees up in only one AD.
- **Polite pacing.** Randomized 30–60 s intervals by default, so repeated capacity checks are not sent at a fixed cadence.
- **Preflight checks** validate credentials, detect limits, and warn before hunting.
- **Conservative hunter protection** allows only the two Always Free shapes above. A1 sizing is
  capped at 2 OCPU / 12 GB and the boot volume at 200 GB (the account-wide Always Free
  block-storage pool); a 50 GB boot volume minimum is enforced. Ephemeral public IP only.
  Preflight checks your detected Always Free allowance and existing usage per shape.
- **Modern dark-themed Windows GUI** with a Settings dialog and a live hunting dashboard.

> **Pro tips from the community:**
> - **Do not upgrade to Pay-As-You-Go just to improve capacity odds** unless you understand OCI billing.
>   Always Free resources are intended to remain free within the published limits, but PAYG accounts can be
>   charged for resources or usage outside those limits.
> - Community reports say the micro is often served by **AD-1 only**, but Oracle does not publish this
>   per region — that's why OracleHost probes every AD with `ListShapes` instead of hardcoding it.

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

The application launches with a dark-themed GUI.

### 3. Credentials — the `AccountInfo` folder (required)

OracleHost reads all credentials from a local `AccountInfo` folder next to the project (or the built EXE).
There is no login screen and no browser sign-in — if the folder is missing or incomplete, OracleHost
shows one clear error and exits.

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
- All four required OCIDs above must be filled in, or OracleHost exits with an error.
- OracleHost **derives the fingerprint from the key** — you don't strictly need to write it down.

### How OracleHost finds it

On startup, OracleHost walks up the directory tree from the working directory (and the EXE location)
looking for a folder named `AccountInfo` that contains both `information.md` and a `*.pem`. Place it
next to the project root and it just works.

### 4. Instance settings (⚙ Settings)

When the AccountInfo bundle is complete, startup skips straight to the dashboard — compartment,
subnet, and region all come from `information.md` and are never asked for. Open **⚙ Settings** to
choose the instance shape (**E2.1.Micro** or **A1.Flex**), OCPUs, memory, boot volume size, display
name, and retry intervals.

> OCPUs and memory are saved for both shapes but only sent to OCI for **A1.Flex** — the micro shape
> is fixed at 1/8 OCPU / 1 GB and rejects a shape config.

If you don't have a VCN yet (needed before any launch):

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

## Project structure

```
OracleHost.csproj          # .NET 8 WinForms project, OCI SDK NuGet refs, v2.0.0
CHANGELOG.md               # release history
Program.cs                 # Entry point: load config → adopt AccountInfo → validate → main form
Forms/
├── SetupWizardForm.cs     # ⚙ Settings dialog: shape, sizing, boot volume, retries
├── MainForm.cs            # Hunting dashboard with live status + activity log
└── AccountInfoReportForm.cs # Startup dialog listing found/missing AccountInfo fields
Services/
├── OciService.cs          # OCI SDK wrapper (compute, identity, networking, limits)
├── ErrorClassifier.cs     # Retry vs abort error classification
├── ConfigService.cs       # JSON config load/save + AccountInfo reader + ~/.oci/config reader
└── AccountInfoReport.cs   # Field-by-field AccountInfo bundle inspection
Models/
├── AppConfig.cs           # Configuration model (config.json schema)
└── HuntStatus.cs          # Runtime hunt state tracking
Helpers/
├── CryptoHelper.cs        # RSA key generation + OCI fingerprint computation
└── DiagnosticLog.cs       # Diagnostic logging
```

---

## Config reference (`config.json`)

The app stores its config at `%USERPROFILE%\OracleHost\config.json`.

| Key | Default | Meaning |
|---|---|---|
| `region` | `null` | Region override (defaults to `~/.oci/config`) |
| `compartment_ocid` | — | **required** — compartment to launch into |
| `subnet_ocid` | — | **required** — subnet to attach the instance to |
| `ssh_public_key_path` | `~/.ssh/id_rsa.pub` | Your public key (must start with `ssh-rsa`) |
| `shape` | `VM.Standard.E2.1.Micro` | `VM.Standard.E2.1.Micro` or `VM.Standard.A1.Flex` |
| `ocpus` | `2` (A1) | A1.Flex OCPU count (1–2, Always Free cap). Saved for both shapes but only sent for A1.Flex |
| `memory_in_gbs` | `12` (A1) | A1.Flex memory in GB (1–12; OCI allows 1–6 GB per OCPU). Only sent for A1.Flex |
| `image_os` | `Oracle Linux` | OS to resolve (`Oracle Linux`, `Canonical Ubuntu`, …) |
| `image_version` | `latest` | `latest` or a substring of the image display name |
| `display_name` | `free-tier-micro` | Instance name |
| `assign_public_ip` | `true` | Attach a public IP |
| `boot_volume_size_gb` | `50` | 50–200 GB; OCI-required minimum is 50, and the published Always Free pool is 200 GB total across boot/block volumes |
| `account_info_fingerprint` | — | SHA-256 of the AccountInfo bundle; config.json is only rewritten when the bundle changes |
| `availability_domains` | `"all"` | `"all"`, one AD, or a list of ADs |
| `min_interval_seconds` | `30` | Minimum randomized retry delay in seconds |
| `max_interval_seconds` | `60` | Maximum randomized retry delay in seconds |
| `stop_on_limit` | `true` | Abort on `LimitExceeded` instead of retrying |
| `max_attempts` | `0` | `0` = unlimited |
| `allow_existing` | `false` | Skip the "instance already exists" sanity check |

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
- **Rotate ADs** — the default `"all"` handles this for you, and OracleHost only rotates across ADs
  that `ListShapes` confirms actually offer the micro in your tenancy.
- **Watch your total** — the Always Free allowance is 2 micro instances (`standard-e2-micro-core-count` = 2)
  and 2 A1 OCPUs / 12 GB (`standard-a1-core-count`). Preflight counts existing usage per shape before hunting.

> **Keep your free instance busy!** Oracle reclaims Always Free instances that stay idle
> (95th-percentile CPU/network/memory all below 20% for 7 straight days). Run a real workload or a
> keep-alive so your free box isn't reclaimed.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `No OCI credentials found` / AccountInfo error | Check the folder rules above: one private PEM, four required OCIDs, folder named `AccountInfo` next to the project or EXE |
| `SSH public key not found` | `ssh-keygen -t rsa -b 2048 -f ~/.ssh/id_rsa` then re-run |
| `No ... images found` | Try `"image_os": "Canonical Ubuntu"` or check the region |
| `None of the requested ADs exist` | Delete the `availability_domains` key or use `"all"` |
| `You already have ... micro instance(s)` | Delete an instance or set `"allow_existing": true` (the Always Free allowance is 2 micro instances) |
| A1 launch rejected or preflight complains about OCPUs/memory | A1 sizing must stay within 2 OCPU / 12 GB with 1–6 GB per OCPU; adjust in ⚙ Settings |
| Preflight says the shape is offered in no AD | Your region may not serve that shape; check Limits, Quotas and Usage for the shape's limit name (`standard-e2-micro-core-count` / `standard-a1-core-count`) |
| Instance created but SSH fails | Check the security list allows TCP 22; wait ~2 min for boot |
| Build error `NU1101: Unable to find package` | Ensure you have .NET 8+ SDK installed (`dotnet --version`) |
| OracleHost ignores my `AccountInfo` folder | Check the folder rules above: one private PEM, four required OCIDs |

---

## Disclaimer

Auto-retry against Oracle's free tier is a widely-used community practice, but it is not officially
supported by Oracle. Keep intervals polite (the defaults are fine) and stay within the Always Free limits
to reduce charge risk. No client application can guarantee a zero bill if the account is upgraded, other
paid resources already exist, or Oracle changes its pricing/policies.
