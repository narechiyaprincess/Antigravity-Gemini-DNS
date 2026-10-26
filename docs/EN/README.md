# Antigravity-Gemini DNS

A Windows DNS/DoH bridge for **Antigravity, Gemini and selected Google services**. It checks providers and creates name-resolution rules for the selected services. Portable EXE with Russian and English UI.

**[Download the latest release](https://github.com/narechiyaprincess/Antigravity-DNS/releases/latest)** · [Русский](../../README.md) · [Changelog](../../CHANGELOG.md) · [Troubleshooting](TROUBLESHOOTING.md)

These sources are prepared for **v1.1.0**. Until it is published, the download link points to the previous release; check its version. v1.1.0 targets Windows 11 x64 with Windows PowerShell 5.1 and working CIM/WMI. The runtime is included; no separate .NET installation is needed to run it. The EXE is unsigned.

## Quick start

1. Download the EXE from Releases, compare its SHA-256 with `SHA256SUMS.txt`, and keep it in a permanent folder.
2. **Before upgrading from 1.0.0, disconnect the bridge with the old version, confirm restoration, and close it.** Then start 1.1.0. Do not run both versions together.
3. Select the services and provider in Bridge. Optionally run Benchmark and choose a working profile.
4. Connect, confirm provider trust and approve Windows UAC. If direct access works, the app may leave DNS policy unchanged.
5. Check the actual service. Disconnect / Restore with UAC to undo changes. Closing the window or quitting the tray does not restore DNS.

## Features and changes

Independent service selection, built-in and custom DoH profiles, availability/latency measurements, fresh-result ranking, history, ownership-aware restoration, Doctor diagnostics, preference import/export, and local support-report preview.

v1.1.0 reworks connection coordination and service selection, adds direct-first and Doctor, strengthens freshness and system-setting ownership checks, updates profiles and polishes the UI. RU/EN, tray, ordinary autostart, custom DoH, benchmarking and basic restoration already existed in 1.0.0. See [v1.1.0 notes](../RELEASE-NOTES-1.1.0-EN.md).

## Trust and limitations

The bridge uses Windows NRPT and DoH for catalog domains. A SmartDNS provider may return its SNI proxy addresses. The third-party provider receives selected-service DNS queries and may relay their traffic. DoH encrypts DNS queries to that provider; the app does not tunnel all computer traffic.

One consent covers built-in catalog providers; custom DoH requires separate consent for its endpoint and selected services. Consent can be revoked in Settings. TLS certificate checks remain enabled. Connection and restoration need administrator rights; an ordinary process does not trigger background UAC for automatic repair.

- This is not a VPN. Successful DNS/TLS checks do not guarantee login, account/model access or service availability.
- VPN/TUN, corporate policy and browser Secure DNS may bypass the Windows resolver; the app does not rewrite their settings.
- Windows 10, ARM64 and clean Windows without the development environment remain unverified. Windows may warn about the unsigned EXE.
- Two P2 limitations remain: secondary-adapter changes can conservatively refuse an operation; the normal coordinator does not use its result-confirmation shortcut. No 6–8 second connection speedup is claimed.
- Before upgrading or downgrading, disconnect using the version that created the bridge. Keep `state.json`; do not clear NRPT manually. [Compatibility](../COMPATIBILITY-IDENTIFIERS.md).

## Help and development

Run Doctor and read Logs when a service fails. Preview and redact support reports before sharing. [Troubleshooting](TROUBLESHOOTING.md) · [Report a bug](https://github.com/narechiyaprincess/Antigravity-DNS/issues/new/choose) · [Security](../../SECURITY.md).

[Build and offline checks](../BUILDING.md) · [Architecture](TECHNICAL.md) · [Release process](../RELEASING.md) · [Contributing](../../CONTRIBUTING.md) · [Attribution](../ATTRIBUTION.md).

[MIT license](../../LICENSE), without warranty. The project does not control third-party providers or service availability.
