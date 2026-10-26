# Antigravity-Gemini DNS 1.1.0

Prepared on 2026-10-26; publication date is pending. GitHub publication follows repository acceptance. [Русский](RELEASE-NOTES-1.1.0.md).

Compared with 1.0.0: independent service groups, direct-first connection, coordinated prepare/apply/verify/recover, Doctor and separate policy/provider/application status, stronger freshness and NRPT/DoH ownership checks, Schema 2 journal, preference import/export, protected-copy elevated startup option, refreshed profiles and UI/DPI polish. The benchmark network budget is 10 seconds instead of 30; cancellation and rendering add time. This is not a connection-duration promise.

RU/EN, tray, ordinary autostart, custom DoH, benchmarks and basic restoration already existed in 1.0.0.

Catalog 2026.10.26.1 offers `dns.malw.link` (default), `dns.comss.one`, `dns.bezmezhau.com`, and experimental `geohide.ru`, `dns.zapret.moe`, `eu.geohide.ru`. COMSS/Bezmezhau addresses are updated. `xbox-dns.ru` and `dns.dns-ai.ru` are retired without reusing their IDs. A saved retired selection normalizes to default; an active retired bridge remains visible and disconnectable. Availability depends on your network.

## Upgrade and downgrade

Disconnect with the old version that created the bridge, confirm restoration, close it, then start 1.1.0. Do not run both versions together. Data identifiers remain compatible, but this does not authorize switching versions with an active bridge. Once 1.1.0 writes Schema 2, the old Schema 1 engine cannot read it. Disconnect with 1.1.0 before downgrading. Never delete `state.json` or clear NRPT/DoH manually. [Compatibility](COMPATIBILITY-IDENTIFIERS.md).

## Limitations

Windows 11 x64, PowerShell 5.1, working CIM/WMI. The unsigned self-contained EXE includes the full WinForms runtime without trimming, with single-file compression and ReadyToRun disabled. Windows 10, ARM64 and clean-Windows acceptance are unverified. This is not a VPN; DNS/TLS success does not prove login/model access. Third-party DNS/SNI providers require trust. VPN/TUN and browser Secure DNS may bypass system policy.

Two P2 remain open: secondary-adapter changes may conservatively refuse native operations; the normal coordinator falls back to full diagnosis instead of its result-confirmation shortcut. No 6–8 second connection speedup is claimed.

The retained approved-kit audit reports 2075 self-tests, 451/451 offline E2E and synthetic UI/DPI/standalone PASS; those are previous local results, not GitHub Actions status. Integration checks are recorded separately. Live apply/restore, UAC, reboot/logon, clean Windows and authenticated service journeys are not established by this pass.

The coordinator is part of normal GUI operation. Direct `--enable`/`--restore` call the engine separately. Broker and local signed catalogs remain experimental; broker stop error 1061 has not been revalidated. [Architecture](EN/TECHNICAL.md) · [Troubleshooting](EN/TROUBLESHOOTING.md).
