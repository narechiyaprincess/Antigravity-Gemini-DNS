# Compatibility identifiers

The product is **Antigravity-Gemini DNS**. Earlier builds were published as *Antigravity DNS Unlocker* / *Antigravity DNS*
(ProductName `AntigravityDnsUnlocker`; public 1.0.0 and later local internal iterations). The display name, executable, assembly, project file,
namespace, scripts and documentation use the new name. The identifiers below deliberately keep the old values. They are
shared with copies that are already installed and with system state those copies wrote; changing them would make this
version blind to that state.

| Identifier | Value | Why it stays |
|---|---|---|
| Data folder (per user) | `%LocalAppData%\AntigravityDnsUnlocker\` | settings, benchmark history, operation history, crash marker. Upgrading keeps the user's choices without a migration step that could half-fail. |
| Data folder (machine) | `%ProgramData%\AntigravityDnsUnlocker\` | the restore journal (`state.json`) and operation receipts. A bridge connected by an older version must be found and removed by this one. |
| Autostart Run value | `HKCU\...\Run` → `AntigravityDnsUnlocker` | an older copy's entry is recognised ("autostart points to another copy") and replaced, never duplicated. |
| Legacy logon task | `AntigravityDnsUnlockerLogon` | detected and removed only after the executable, current principal and task action prove ownership; foreign/unknown tasks are retained. |
| Trust store | `HKCU\Software\AntigravityDnsUnlocker\ConnectionTrustV2` | storage identity persists; historical name-only grants do not authorize current connections. |
| NRPT rule tags | `Comment = AgDns:<owner>`, `DisplayName = AgDns <owner>` | ownership proof for rules already in the system; restore removes only rules with this tag. |
| Config export schema | `agdns-config` | files exported by older versions keep importing. |
| Window mutex / show event | `Local\AntigravityDnsUnlockerUI-…`, `…Show-…` | internal; one window per copy. |
| Optional broker (experimental) | service `AntigravityDnsBrokerV1`, pipe `AntigravityDnsUnlocker.Broker.v1`, `%ProgramData%\AntigravityDnsUnlocker\broker-v1` | an installed broker service must remain addressable for removal. |
| Previous-copy check | ProductName `AntigravityDnsUnlocker` is accepted next to `Antigravity-Gemini DNS` | "Delete previous copy" keeps working for copies of the old builds. |
| Test-only names | `HKCU\Software\AgDnsTest`, `AGDNS_*` environment variables | developer tooling; `AgDns` / `AGDNS` reads as **A**ntigravity-**G**emini **DNS**. |

In code these values live in `AppIdentity` (`src/app/Program.cs`); the PowerShell engine (`src/app/Engine.ps1`) repeats
the folder, Run value and task names with a comment pointing here.

## Upgrading from 1.0.0 or another older build

1. Disconnect the bridge using the version that created it. Confirm restoration and close that copy before starting 1.1.0. Do not run both versions together.
2. Settings/history storage identifiers are preserved; current validation and schema migration still apply. Built-in catalog consent and custom endpoint/scope consent follow the current trust rules; retaining a registry path does not make historical name-only grants valid.
3. Normal GUI startup can repoint an enabled, recognised previous-copy autostart entry to the current executable. Synthetic tests do not do this; foreign entries are retained.
4. The shared journal lets the app detect existing state; it is not a supported active-bridge upgrade procedure. Version 1.1.0 writes Schema 2, which the old Schema 1 engine cannot read. Disconnect with 1.1.0 before considering a downgrade, but restoration retains the Schema 2 journal with phase `Restored`; it does not convert it to Schema 1. The 1.0.0 engine rejects this journal even after restoration, so automatic downgrade is unsupported. This version provides no reverse journal migration. Preserve `state.json`, its access permissions and the 1.1.0 EXE for recovery; returning to 1.0.0 requires a separately reviewed migration procedure. Never delete or edit the journal or clear NRPT/DoH manually.

See [release notes](RELEASE-NOTES-1.1.0.md) and [current assisted trust contract](../src/ASSISTED.md).
