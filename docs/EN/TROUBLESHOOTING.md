[🇷🇺 Русский](../TROUBLESHOOTING.md) • [🇬🇧 English](TROUBLESHOOTING.md)

# Troubleshooting the bridge

[README](README.md) · [Technical details](TECHNICAL.md) · [Report a bug](https://github.com/narechiyaprincess/Antigravity-DNS/issues/new/choose)

## The app does not start or the bridge will not connect

Check the requirements: Windows 11 x64, Windows PowerShell 5.1, and native DoH. Windows 10 compatibility is unverified. Use the EXE from an [official Release](https://github.com/narechiyaprincess/Antigravity-DNS/releases/latest) and compare its SHA-256 with the release data. No separate .NET runtime is needed.

Connecting and restoring require administrator rights: an ordinary launch requests UAC for the operation; separately enabled elevated startup uses its existing privileges without a new prompt. If an operation fails, record the code from Logs or Details. `DOH_UNSUPPORTED_BY_OS` means the necessary native DoH commands are unavailable; see [Microsoft's requirements](https://learn.microsoft.com/en-us/windows-server/networking/dns/doh-client-support). Do not substitute manual adapter DNS changes for this check.

`WMI_NOT_RESPONDING` means the CIM/WMI preflight did not answer, so the app does not start a change. If an operation was already in progress and its outcome is unconfirmed, follow the recovery section below. Do not delete the journal to dismiss the message.

## Providers fail measurement

Open Test and measure again on your network, then inspect TLS/DoH errors in Logs. Availability depends on the network and time of the test. Experimental labels and dated catalog notes do not guarantee current availability.

Choose a passing profile and confirm provider trust when connecting. Picking the best profile or selecting one manually does not connect the bridge. For custom DoH, check the HTTPS URL with a hostname (not an IP literal) and optional addresses in Settings. Saving a profile is not a successful probe.

Keep certificate validation enabled and do not install unknown root certificates to make a test pass. Record TLS error codes and try another available profile.

## Connected, but Antigravity, Gemini, or the browser still fails

- Check the independent Bridge groups: Antigravity, Gemini and Google/browser; at least one must be selected. See [services and scope](TECHNICAL.md#services-and-scope).
- Browser Secure DNS/DoH can bypass the Windows resolver. With Google/browser services selected, use the selected browser's diagnostics and recommendations. The app does not rewrite browser settings.
- VPN/TUN and other network software can use a different resolver or route. Compare their settings with the diagnostics. For NRPT/DoH conflicts, inspect Details and preserve other software's or corporate rules. Coordinate changes with the administrator of a managed computer.
- If security software blocks the EXE or PowerShell, retain its message and the app's error code for diagnosis. Do not disable protection wholesale.

A passing DNS/TLS test does not verify account login, regional restrictions, or a model response. The bridge does not modify Antigravity files. Open Antigravity Patcher is a separate linked project; it is not bundled or launched automatically, and its compatibility must be assessed separately.

## Disconnecting and restoring

Use Disconnect / Restore and approve UAC if prompted. Closing the window, exiting the tray, or deleting the EXE does not remove DNS rules. The app does not perform a blanket network reset.

Restoration uses the ownership journal. Shared entries or entries changed by other software can be retained; read the result. For `RESTORE_UNCONFIRMED`, open Details and the offered recovery instructions. They are generated from that operation's journal; do not use someone else's registry-key list. Follow the steps and reboot requirement if shown, then check restoration again in the app.

Do not delete `%ProgramData%\AntigravityDnsUnlocker\state.json` or blindly clear NRPT/DoH: the journal identifies owned entries. Live integration tests are not a repair tool.

## Logs and a safe report

Generate a support report in Logs and review its preview. For an ordinary bug, provide app and Windows build versions, built-in profile, scope, error code, reproduction steps, and whether VPN/TUN or browser-specific DNS is in use. Private addresses are unnecessary.

Settings, history, and UI errors live in `%LocalAppData%\AntigravityDnsUnlocker`; the ownership journal and staged PowerShell resources live in `%ProgramData%\AntigravityDnsUnlocker`. These paths help diagnosis; they are not instructions to delete the folders.

Automatic redaction still needs review: remove personal paths, computer/network names, tokens, and private DoH URLs. Do not attach raw `settings.json`, `state.json`, or user logs. Then use the [bug report form](https://github.com/narechiyaprincess/Antigravity-DNS/issues/new/choose). Follow [SECURITY.md](../../SECURITY.md) for suspected vulnerabilities, without posting public details.

More information: [architecture, security, and diagnostic limits](TECHNICAL.md), [build and offline checks](../BUILDING.md).

## Upgrade and network-context refusal

Before upgrading from 1.0.0, disconnect with the old version, confirm restoration and close it; then start 1.1.0. Before considering a downgrade, disconnect with 1.1.0 and confirm restoration. The restored journal remains Schema 2, which 1.0.0 rejects even with phase `Restored`. Automatic downgrade is unsupported; no reverse journal migration is provided. Preserve the journal, its access permissions and the 1.1.0 EXE for recovery. Returning to 1.0.0 requires a separately reviewed migration procedure; never delete or edit the journal.

Run Doctor for diagnostics. Secondary-adapter changes may conservatively refuse an operation (known P2). Wait for stable network state and retry. [v1.1.0 limits](../RELEASE-NOTES-1.1.0-EN.md).
