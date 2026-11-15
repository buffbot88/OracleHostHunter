<div align="center">

# OracleHost

### ☁️ Always Free Instance Hunter · Windows Desktop

*Finds Oracle Cloud capacity for you, then proves you can actually log in.*

[![Version](https://img.shields.io/badge/version-3.0.0-orange?style=for-the-badge)](CHANGELOG.md)
[![CI](https://github.com/buffbot88/OracleHostHunter/actions/workflows/ci.yml/badge.svg)](https://github.com/buffbot88/OracleHostHunter/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=for-the-badge)](#1-prerequisites)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue?style=for-the-badge)](LICENSE)

[**Quick start**](#-quick-start) · [**SSH verification**](#-verified-ssh-access) · [**Troubleshooting**](#-troubleshooting) · [**Changelog**](CHANGELOG.md)

</div>

---

```text
Provisioned  →  Running  →  Port 22 open  →  Login verified  →  Provisioning verified

     any step fails  →  🔒 instance preserved (never terminated, never retried)
```

> **The rule this project is built around:** a failed diagnostic is information, not a failure.
> OracleHost never terminates, retries, or modifies an instance because an SSH check did not pass.

---

## 🧭 What it does

Oracle's Always Free tier includes two VM shapes this app can hunt:

| Shape | Arch | Sizing | Always Free allowance |
|---|---|:---:|---|
| **`VM.Standard.E2.1.Micro`** | x86 | fixed 1/8 OCPU · 1 GB | up to **2 instances** per tenancy |
| **`VM.Standard.A1.Flex`** | ARM Ampere | flexible | **2 OCPU / 12 GB** *(Oracle reduced this from 4/24)* |

Capacity for both can be sold out in busy regions. The community answer is an **auto-retry client** —
keep asking Oracle to create the instance every few minutes until capacity opens up. This project is
exactly that, as a **C# WPF desktop application** for Windows.

> 🎯 The hunter **auto-detects your shape quota** (`standard-e2-micro-core-count` or
> `standard-a1-core-count`) during preflight and **probes which availability domains actually offer
> the shape** via `ListShapes`, so attempts are never wasted on ADs that cannot serve it.
> Always Free egress is **10 TB/month**, not 1 GB.

### Capability matrix

| Capability | Status |
|---|:---:|
| Capacity hunting, AD rotation, retry-vs-abort | ✅ shipped |
| Always Free safeguards (shape, OCPU, memory, boot pool) | ✅ shipped |
| SSH key-pair validation | ✅ shipped |
| Optional Oracle Linux 9 cloud-init | ✅ shipped · off by default |
| TCP port 22 probe | ✅ shipped |
| Authenticated SSH verification | ✅ shipped |
| Provision-log retrieval and parsing | ✅ shipped |
| NSG-aware ingress preflight (`Allowed` / `Blocked` / `Unknown`) | ✅ shipped |
| Persistent OCID-scoped host-key trust | ✅ shipped |
| Live Oracle Linux 9 end-to-end validation | 🧪 pending capacity |

---

## ✨ Why this instead of the web console

| | |
|---|---|
| 🤖 | **No browser, no clicking.** Uses the official OCI SDK. |
| 🧠 | **Smart retry.** Retries only on *capacity / transient* errors; aborts immediately on real problems (quota limits, bad config) so it never hammers the API pointlessly. |
| 🧭 | **Rotates availability domains.** Capacity often frees up in only one AD. |
| 🚦 | **Polite pacing.** Randomized 30–60 s intervals by default, so repeated capacity checks are not sent at a fixed cadence. |
| ✅ | **Preflight checks** validate credentials, detect limits, and warn before hunting. |
| 🛡️ | **Conservative hunter protection** allows only the two Always Free shapes above. A1 sizing is capped at 2 OCPU / 12 GB and the boot volume at 200 GB (the account-wide Always Free block-storage pool); a 50 GB boot volume minimum is enforced. Ephemeral public IP only. Preflight checks your detected Always Free allowance and existing usage per shape. |
| 🔐 | **Verified access, not assumed access.** Key pairing, OCI ingress, host identity, login, and first-boot configuration are each reported separately. |
| 🌙 | **Modern dark-themed Windows GUI** with a Settings dialog and a live hunting dashboard. |

> **Pro tips from the community**
> - **Do not upgrade to Pay-As-You-Go just to improve capacity odds** unless you understand OCI billing.
>   Always Free resources are intended to remain free within the published limits, but PAYG accounts can be
>   charged for resources or usage outside those limits.
> - Community reports say the micro is often served by **AD-1 only**, but Oracle does not publish this
>   per region — that's why OracleHost probes every AD with `ListShapes` instead of hardcoding it.

---

## 🚀 Quick start

<details>
<summary><b>Jump to a step</b></summary>

1. [Prerequisites](#1-prerequisites)
2. [Build & run](#2-build--run)
3. [Credentials — the `AccountInfo` folder](#3-credentials--the-accountinfo-folder-required)
4. [Instance settings](#4-instance-settings--settings)
5. [Run the hunt](#5-run-the-hunt)

</details>

### 1. Prerequisites

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer)
- An Oracle Cloud account (free tier)

### 2. Build & run

```bash
dotnet build
dotnet run
```

The application launches with a dark-themed GUI.

### 3. Credentials — the `AccountInfo` folder (required)

OracleHost reads all credentials from a local `AccountInfo` folder next to the project (or the built EXE).
There is no login screen and no browser sign-in — if the folder is missing or incomplete, OracleHost
shows one clear error and exits.

> [!WARNING]
> **This folder is private. Never commit it, zip it, or share it.** It contains your API private key
> and your account OCIDs. The whole `AccountInfo/` folder is gitignored by default so `git add .`
> will never pick it up — but double-check before any manual `git add` that nothing from it is staged.
> The only exception is `information.example.md`, a sanitized, shareable template committed with the repo.

**Folder layout**

```text
AccountInfo/
├── information.md            # your OCIDs — see template below
├── information.example.md    # committed sanitized template — rename to information.md and fill in
├── <name>.pem                # private key — exactly ONE, filename must NOT contain "public"
└── <name>_public.pem         # optional — public key, filename MUST contain "public"
```

**`information.md` format** — label on its own line, value on the next line:

```text
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
|---|:---:|---|
| `Tenancy OCID` | ✅ | |
| `User OCID` | ✅ | |
| `Compartment OCID` | ✅ | |
| `Subnet OCID` | ✅ | |
| `Home region` | — | e.g. `IAD`. Maps to a region code for the API |
| `Fingerprint` | — | If present, OracleHost verifies it matches the key |

**The rules (what makes it work)**

- **Exactly one private `.pem`** must be present. Its filename must **not** contain the word `public`.
- **At most one public `.pem`** is allowed. Its filename **must** contain `public`.
- All four required OCIDs above must be filled in, or OracleHost exits with an error.
- OracleHost **derives the fingerprint from the key** — you don't strictly need to write it down.

**How OracleHost finds it**

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

| Button | What it does |
|---|---|
| **✓ Preflight Check** | Validates credentials, detects limits, shows existing instances |
| **⚡ Try Once** | Single launch attempt |
| **▶ Start Hunting** | Continuous retry loop with live status updates |
| **⏹ Stop** | Cancel the current hunt |

---

## 🔐 Verified SSH access

A launch result distinguishes **RUNNING** from **reachable**. The chain is read-only at every step and
never terminates, retries, or modifies an instance because a check failed.

1. **Preflight** validates the public-key format and key-pair pairing, reports the image/cloud-init
   setting, and classifies TCP 22 ingress as `Allowed`, `Blocked`, or `Unknown` across the subnet
   security lists and only the NSGs listed in `vnic_nsg_ids`. It never reports `Blocked` when a
   permission gap prevented a full look, and reports a public-IP problem separately from an
   ingress-rule problem.
2. **After RUNNING** it probes TCP 22, then attempts a real key login (`BatchMode`, no password).
   A TCP connect alone is only ever reported as "SSH port reachable (login not verified)".
3. **Host-key trust** is required first. The approval dialog shows the `ssh-keyscan` fingerprints and
   states plainly that keyscan does **not** authenticate the server, so the fingerprint must be
   checked against a trusted source before accepting. Approved keys are stored per instance OCID in
   `%LOCALAPPDATA%\OracleHost\known_hosts` and enforced with `StrictHostKeyChecking=yes`. A changed
   key blocks verification until you review it.
4. **Provision log** (`enable_cloud_init`, Oracle Linux only): after a verified login the app reads
   `/var/log/oraclehost-provision.log` back and reports each step — SSH daemon, firewall service,
   SSH firewall rule — as Success, Failed, or Unknown. `Not Applicable` means cloud-init was off for
   that launch; a missing log is never reported as an authentication failure.

> [!NOTE]
> Ingress allow rules do not prove end-to-end SSH: the route table, the instance's own firewall and
> `sshd` still matter. That is exactly what steps 2–4 are for.

---

## 🗂️ Project structure

```text
OracleHost.csproj          # .NET 8 WPF project, OCI SDK NuGet refs, v3.0.0
CHANGELOG.md               # release history
Program.cs                 # Entry point: load config → adopt AccountInfo → validate → main window
Windows/
├── MainWindow.xaml(.cs)   # Hunting dashboard with live status + activity log
├── SettingsWindow.xaml(.cs) # ⚙ Settings: shape, sizing, boot volume, retries, SSH
├── NetworkDiagnosticWindow.xaml(.cs) # VNIC/subnet/NSG diagnostics for one instance
├── AccountInfoReportWindow.xaml(.cs) # Startup dialog listing found/missing AccountInfo fields
└── OciGuideWindow.xaml(.cs) # In-app OCI setup guide
Services/
├── OciService.cs          # OCI SDK wrapper (compute, identity, networking, limits)
├── ErrorClassifier.cs     # Retry vs abort error classification
├── ConfigService.cs       # JSON config load/save + AccountInfo reader + ~/.oci/config reader
└── AccountInfoReport.cs   # Field-by-field AccountInfo bundle inspection
Models/
├── AppConfig.cs           # Configuration model (config.json schema)
└── HuntStatus.cs          # Runtime hunt state tracking
Helpers/
├── CryptoHelper.cs        # SSH key format/pair validation + OCI fingerprint computation
├── SshProbe.cs            # TCP 22 probe, authenticated SSH, host-key trust, provision log
└── DiagnosticLog.cs       # Diagnostic logging
```

---

## ⚙️ Config reference (`config.json`)

Stored at `%USERPROFILE%\OracleHost\config.json`. Secrets never appear here — only paths.

<details>
<summary><b>Identity &amp; network</b></summary>

| Key | Default | Meaning |
|---|---|---|
| `region` | `null` | Region override (defaults to `~/.oci/config`) |
| `compartment_ocid` | — | **required** — compartment to launch into |
| `subnet_ocid` | — | **required** — subnet to attach the instance to |
| `vnic_nsg_ids` | `[]` | NSG OCIDs attached to the new VNIC at launch. Preflight evaluates only these NSGs — unrelated VCN NSGs never count as SSH ingress |
| `assign_public_ip` | `true` | Attach a public IP. Ephemeral only; no reserved IP is ever created |

</details>

<details>
<summary><b>SSH &amp; provisioning</b></summary>

| Key | Default | Meaning |
|---|---|---|
| `ssh_public_key_path` | `~/.ssh/id_rsa.pub` | Your public key (must start with `ssh-rsa`) |
| `ssh_private_key_path` | `null` | Private key used to *verify* login. When null it is derived by stripping `.pub` from `ssh_public_key_path` |
| `enable_ssh_health_check` | `true` | After RUNNING, probe TCP 22 and attempt an authenticated login |
| `enable_cloud_init` | `false` | Oracle Linux only: attach cloud-init `user_data` that configures `sshd`/`firewalld` and records exit codes to `/var/log/oraclehost-provision.log` (applies to newly provisioned instances only) |

</details>

<details>
<summary><b>Shape, image &amp; storage</b></summary>

| Key | Default | Meaning |
|---|---|---|
| `shape` | `VM.Standard.E2.1.Micro` | `VM.Standard.E2.1.Micro` or `VM.Standard.A1.Flex` |
| `ocpus` | `2` (A1) | A1.Flex OCPU count (1–2, Always Free cap). Saved for both shapes but only sent for A1.Flex |
| `memory_in_gbs` | `12` (A1) | A1.Flex memory in GB (1–12; OCI allows 1–6 GB per OCPU). Only sent for A1.Flex |
| `image_os` | `Oracle Linux` | OS to resolve (`Oracle Linux`, `Canonical Ubuntu`, …) |
| `image_version` | `latest` | `latest` or a substring of the image display name |
| `display_name` | `free-tier-micro` | Instance name |
| `boot_volume_size_gb` | `50` | 50–200 GB; OCI-required minimum is 50, and the published Always Free pool is 200 GB total across boot/block volumes |
| `account_info_fingerprint` | — | SHA-256 of the AccountInfo bundle; config.json is only rewritten when the bundle changes |

</details>

<details>
<summary><b>Pacing &amp; safety</b></summary>

| Key | Default | Meaning |
|---|---|---|
| `availability_domains` | `"all"` | `"all"`, one AD, or a list of ADs |
| `min_interval_seconds` | `30` | Minimum randomized retry delay in seconds |
| `max_interval_seconds` | `60` | Maximum randomized retry delay in seconds |
| `stop_on_limit` | `true` | Abort on `LimitExceeded` instead of retrying |
| `max_attempts` | `0` | `0` = unlimited |
| `allow_existing` | `false` | Skip the "instance already exists" sanity check |

</details>

---

## 🧯 How retry-vs-abort works

| Error | Action |
|---|---|
| `InternalError` / "Out of host capacity" | 🔁 **Retry** — this is the one you're waiting for |
| `TooManyRequests` (429), 500/503 | 🔁 **Retry** |
| Network timeouts / connection errors | 🔁 **Retry** |
| `LimitExceeded` | 🛑 **Abort** — you've used your free quota; retrying is pointless |
| `InvalidParameter`, `BadRequest`, auth errors | 🛑 **Abort** — config bug; fix and rerun |

This is the key safety feature: a naive `while true; launch; sleep 60` loop will happily spam a broken
request forever. OracleHost spaces capacity checks with a randomized 30–60 second delay and stops to tell
you what to fix instead.

---

## 🎣 Tips to actually get an instance

- **Upgrade to PAYG** (stays free within Always Free limits) — dramatically better capacity odds, but not required.
- **Pick a less busy region** — communities report better luck in regions like `ap-mumbai-1`,
  `eu-frankfurt-1`, or newer home regions than in the crowded US-East.
- **Run off-peak** — late night / early morning local time.
- **Keep it running** — leave the app hunting in the background; capacity can free up at any moment.
- **Rotate ADs** — the default `"all"` handles this for you, and OracleHost only rotates across ADs
  that `ListShapes` confirms actually offer the micro in your tenancy.
- **Watch your total** — the Always Free allowance is 2 micro instances (`standard-e2-micro-core-count` = 2)
  and 2 A1 OCPUs / 12 GB (`standard-a1-core-count`). Preflight counts existing usage per shape before hunting.

> [!TIP]
> **Keep your free instance busy!** Oracle reclaims Always Free instances that stay idle
> (95th-percentile CPU/network/memory all below 20% for 7 straight days). Run a real workload or a
> keep-alive so your free box isn't reclaimed.

---

## 🩺 Troubleshooting

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
| SSH host key prompt reappeared / "Host key changed" | Trusted host keys are stored per instance OCID in `%LOCALAPPDATA%\OracleHost\known_hosts` and bound to the address they were approved on. An instance whose public IP changed after stop/start must be re-approved. This is a deliberate fail-closed behaviour; strict checking is never weakened. |
| Build error `NU1101: Unable to find package` | Ensure you have .NET 8+ SDK installed (`dotnet --version`) |
| OracleHost ignores my `AccountInfo` folder | Check the folder rules above: one private PEM, four required OCIDs |

---

## ⚠️ Disclaimer

Auto-retry against Oracle's free tier is a widely-used community practice, but it is not officially
supported by Oracle. Keep intervals polite (the defaults are fine) and stay within the Always Free limits
to reduce charge risk. No client application can guarantee a zero bill if the account is upgraded, other
paid resources already exist, or Oracle changes its pricing/policies.

<div align="center">

**[⬆ back to top](#oraclehost)** · Apache-2.0 · built for the Always Free tier

</div>
