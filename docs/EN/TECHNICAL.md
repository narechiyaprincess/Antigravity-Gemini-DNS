# Architecture 1.1.0

`Program.cs` validates CLI arguments and starts WinForms. `MainForm` partial classes manage five tabs, cancellation, RU/EN, DPI, tray and preferences. UI status is distinct from the machine DNS journal.

## Services and scope

Antigravity exact hosts, Gemini exact hosts and Google/browser suffix namespaces are independent groups; at least one must be selected. Their exact contents come from [profiles.json](../../src/app/profiles.json). Host sets are unchanged from 1.0.0; independent combinations are new. The current catalog has six selectable profiles and two retired entries; see [release notes](../RELEASE-NOTES-1.1.0-EN.md).

`Benchmark` uses bounded concurrent DoH/TLS probes, public pins, answer validation and fresh-result admission. Saved history is diagnostic data, not current permission to connect. `Doctor` separates network, provider, policy and application readiness; transport success does not prove authenticated service access.

`Connect` / `Switch` / `ConnectionTrust` coordinate candidate selection and direct-first. `AssistedCoordinator`, `AssistedNative` and `PolicyCompiler` are part of the ordinary GUI prepare/apply/verify/recover path. Direct CLI `--enable` and `--restore` invoke the engine without the coordinator. Built-in trust consent covers the catalog; custom DoH consent is bound to endpoint/scope. Revocation clears both.

`ScriptStore` stages embedded engine scripts/catalog with ACL and SHA-256 checks. `Engine.ps1` writes the Schema 2 ownership journal under `%ProgramData%/AntigravityDnsUnlocker/state.json`, serializes writes and verifies NRPT fingerprints / DoH ownership. Restoration preserves foreign, changed, shared or ambiguous state. It does not rewrite adapter DNS, hosts or proxy settings.

Preferences/history remain under `%LocalAppData%/AntigravityDnsUnlocker`. Import/export carries preferences, not machine ownership. Support reports are local and have a redacted editable preview. [Compatibility and upgrade](../COMPATIBILITY-IDENTIFIERS.md).

Broker service and local signed catalogs are separate experimental facilities. See [assisted](../../src/ASSISTED.md), [broker](../../src/BROKER.md), [catalog](../../src/CATALOG.md), and [scenario probes](../../src/SCENARIO-PROBES.md).

## Known limits

Native context still includes secondary NIC state, so its changes can conservatively refuse operations. The normal coordinator does not use the result-confirmation fast path and runs full diagnosis; no connection speedup is claimed. Windows 10/ARM64, clean Windows, live UAC/apply/restore/reboot/logon and authenticated journeys are not proven by offline tests. [Build and checks](../BUILDING.md) · [Troubleshooting](TROUBLESHOOTING.md).
