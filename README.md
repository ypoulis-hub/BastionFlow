<div align="center">

<img src="docs/assets/logo.svg" alt="BastionFlow logo" width="120"/>

# BastionFlow

**The admin RDP client for Azure Bastion that Microsoft forgot to build.**

Connect to any Azure VM in your tenant — Entra-joined, AD-joined, or stand-alone — through Azure Bastion, with Entra ID (AAD-RDP) authentication that actually works when the OS hostname doesn't match the Azure resource name.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows)](https://www.microsoft.com/windows)
[![Release](https://img.shields.io/github/v/release/ypoulis-hub/BastionFlow?include_prereleases&sort=semver)](../../releases)
[![Downloads](https://img.shields.io/github/downloads/ypoulis-hub/BastionFlow/total.svg)](../../releases)

[Download installer](../../releases/latest) · [Report bug](../../issues) · [Request feature](../../issues)

</div>

---

## Why BastionFlow

Microsoft's official **Windows App** (formerly Microsoft Remote Desktop) targets AVD end-users on assigned desktops. It does **not** handle the admin workflow of *"connect to any VM in my tenant via Azure Bastion, regardless of AVD configuration, with Entra ID auth that survives hostname mismatches and stale device records."*

If you've ever hit any of these errors while admin-RDP'ing to Azure VMs through Bastion, BastionFlow fixes them automatically:

- `AADSTS293004: The target-device identifier in the request <vmname> was not found in the tenant` (caused by Azure resource name ≠ Entra device displayName)
- `This RDP File is corrupted. The remote connection cannot be started.` (mstsc rejecting Bastion-signed `.rdp` files after edits)
- *"Your credentials did not work"* on Workplace-joined PCs where AAD-RDP requires a fresh PRT
- The Bastion native client (`az network bastion rdp`) auto-launching the wrong RDP client

## Features

- **Auto-discovery** of every Windows VM you can reach across all subscriptions in your tenant
- **Smart Entra device name resolution** with four fallback strategies:
  1. Per-tenant JSON cache (instant after first warm-up)
  2. AVD session host name lookup
  3. Azure VM `osProfile.computerName` + Entra Graph search
  4. Live `hostname` via `az vm run-command` (~30 s, bullet-proof, result is cached)
- **Bastion picker per VM** — chooses a Bastion in the same vnet, then peered vnet, then any reachable
- **One-click Connect** that orchestrates the full Bastion → `.rdp` edit → `msrdc.exe` launch sequence
- **Automatic 293004 fallback** — watches Entra sign-in logs after each connect and surfaces a *"retry with local admin"* banner when AAD-RDP fails
- **Right-click context menu** for explicit AAD-RDP vs CredSSP (local-admin) connections
- **Single-monitor fullscreen** by default — no more 3-screen takeover
- **Dark theme** WPF UI with live search/filter, status indicators, and an About dialog

## Screenshots

<!-- TODO: add screenshots/main.png, screenshots/about.png -->

## Installation

Download the latest installer from [Releases](../../releases/latest) and run `BastionFlow-Setup-x.y.z.exe`.

The installer will detect and install the three prerequisites if missing (via `winget`):

| Component | Purpose |
| --- | --- |
| .NET 8 Desktop Runtime | Runs BastionFlow.exe (WPF) |
| Azure CLI | Drives `az network bastion rdp` under the hood |
| Microsoft Remote Desktop client (`msrdc.exe`) | Renders the AAD-RDP session |

Manual prerequisite install (alternative):

```powershell
winget install Microsoft.DotNet.DesktopRuntime.8 Microsoft.AzureCLI Microsoft.RemoteDesktopClient `
  --accept-package-agreements --accept-source-agreements
```

## Usage

1. Launch BastionFlow from the Start Menu.
2. Click **Sign in** — a Windows account broker (WAM) popup opens. Pick your Azure account.
3. The VM list populates. AVD VMs are auto-resolved to their Entra device names; non-AVD VMs are resolved lazily on first Connect (or via right-click → *Resolve hostname*).
4. Click **Connect** on any VM. BastionFlow:
   - Picks the right Bastion (same vnet → peered → any),
   - Asks `az` to generate a Bastion-signed `.rdp` file,
   - Suppresses the auto-launched legacy mstsc,
   - Rewrites the `.rdp` with the correct AAD device target + single-monitor settings + signature strip,
   - Launches `msrdc.exe` with the corrected file.
5. Accept the *"unknown publisher"* warning, sign in with your Entra account, you're in.

If AAD-RDP fails with `293004` (target device not found), BastionFlow detects it from Entra sign-in logs within ~60 s and offers a one-click retry with local admin / CredSSP.

## Architecture

```
BastionFlow.sln
├── src/
│   ├── BastionFlow.App/      # WPF UI (.NET 8) — views, view-models, theme
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   └── App.xaml
│   └── BastionFlow.Core/     # Headless, fully testable library
│       ├── Auth/             # MSAL.NET + WAM broker
│       ├── Azure/            # ARM (VMs, AVD, run-command), Graph (devices, sign-ins)
│       ├── Bastion/          # Bastion discovery, vnet picker, .rdp orchestrator
│       └── Cache/            # Per-tenant device-name persistence
├── tests/
│   └── BastionFlow.Core.Tests/
├── tools/
│   └── Generate-Icon.ps1     # Renders the logo to multi-resolution app.ico
└── installer/
    ├── BastionFlow.iss        # Inno Setup script
    └── build-installer.ps1
```

## Building from source

```powershell
# Prerequisites (build-time only)
winget install Microsoft.DotNet.SDK.8 JRSoftware.InnoSetup `
  --accept-package-agreements --accept-source-agreements

# Build + run
git clone https://github.com/ypoulis-hub/BastionFlow
cd BastionFlow
dotnet build
dotnet test
dotnet run --project src/BastionFlow.App

# Produce a Setup.exe under dist/
pwsh installer\build-installer.ps1
```

## How it works (the technical bits)

This project codifies a debugging marathon worth of Bastion AAD-RDP gotchas:

- **Azure resource name ≠ Entra device displayName.** AVD session hosts in many tenants use a separate naming convention (e.g. `vmavdbizapp-2` → `HBGAZ-THEOV-AVD-1`). mstsc sends the `full address` from the `.rdp` to Entra; if it doesn't match a device record, you get `AADSTS293004`. BastionFlow resolves the right name and rewrites `full address` before launch.
- **`targetisaadjoined:i:1` is mandatory.** Without it, `msrdc` falls back to deriving the target name from the gateway JWT (which carries the Azure resource name) and the lookup fails again.
- **Bastion signs `full address` in its `.rdp`.** Once we edit a signed field, the signature is invalid. Legacy `mstsc` rejects with *"This RDP File is corrupted"*. The fix is to strip `signature` and `signscope` lines AND set `targetisaadjoined:i:1` so the modern client uses `full address` over the JWT-derived name.
- **`az network bastion rdp --configure` auto-launches mstsc** despite the flag name. BastionFlow runs `az` as a background job, kills the spawned mstsc, applies edits, and launches `msrdc.exe` itself via file association.

Full write-up of every gotcha lives in commit history and [docs](docs/).

## Contributing

PRs welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the development setup, code style, and what makes a good PR.

## Security

If you find a security issue, please follow [SECURITY.md](SECURITY.md) — do **not** open a public issue.

## License

[MIT](LICENSE) © JohnBird

## Acknowledgments

Built on the shoulders of Microsoft's open Azure SDKs ([Azure SDK for .NET](https://github.com/Azure/azure-sdk-for-net), [MSAL.NET](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet), [Microsoft Graph SDK](https://github.com/microsoftgraph/msgraph-sdk-dotnet)) and the [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) source generators. Installer powered by [Inno Setup](https://jrsoftware.org/isinfo.php).

---

<sub>Keywords: Azure Bastion native client, AAD-RDP, Entra ID RDP, Windows admin RDP, msrdc, Azure VM remote desktop, AVD admin tool, Bastion AAD authentication, Microsoft Entra device login, RDP client for Azure, Azure DevOps admin tools, Windows desktop RDP Bastion.</sub>
