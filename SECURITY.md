# Security Policy

BastionFlow holds and brokers Azure access tokens on behalf of the signed-in user. We take security seriously.

## Reporting a vulnerability

Please **do not** open a public GitHub issue for security problems. Instead, email the maintainer directly:

- **Contact:** open a GitHub Security Advisory via the [Security tab](../../security/advisories/new) (preferred), or DM the maintainer through GitHub.

Include:
- A clear description of the issue and how to reproduce
- The BastionFlow version affected
- Suggested severity (low / medium / high / critical) and your reasoning

You can expect:
- Acknowledgement within 72 hours
- A fix or mitigation plan within 14 days for high/critical issues
- Credit in the release notes (unless you prefer anonymity)

## Supported versions

BastionFlow is pre-1.0; only the latest release receives security fixes.

| Version | Supported |
| ------- | --------- |
| latest  | yes       |
| older   | no        |

## What BastionFlow does *not* do

- It does not store passwords. Authentication uses MSAL.NET via the Windows account broker (WAM); tokens live in OS-protected storage.
- It does not bypass Conditional Access. All policies your tenant enforces still apply.
- It does not phone home. No telemetry. The app talks only to Azure ARM, Microsoft Graph, and Azure Bastion in your tenant.

## Scope

In scope:
- BastionFlow application code (`src/`, `installer/`, `tools/`)
- Build / release pipeline (`.github/workflows/`)

Out of scope:
- Vulnerabilities in upstream dependencies (please report to the upstream project — but feel free to ping us so we can pin/update faster)
- Misconfiguration of the user's Azure tenant or PC
