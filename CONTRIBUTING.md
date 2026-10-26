# Contributing

Issues and pull requests may be written in Russian or English. For vulnerabilities, follow [SECURITY.md](SECURITY.md) before posting details.

## Build and check

Use Windows x64, Windows PowerShell 5.1, the .NET SDK pinned in [global.json](global.json), NuGet access for the first restore, and at least 10 GiB free on source/temp drives. An interactive, non-elevated desktop is needed for the full local verification. The application itself does not need a separately installed .NET runtime.

Run from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\tools\verify-ci.ps1
```

This builds the portable EXE and runs its self-test, embedded-script verification, mocked engine tests, and offline tier 14. It is the same technical gate used in Actions. For the full local gate (tiers 1–15, UI, RU/EN DPI checks, and standalone EXE checks):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\tools\verify.ps1 -Tag contribution -NoPublish
```

For a build alone:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\app\Build.ps1
```

Build output is `src/.work/build/AntigravityGeminiDns.exe`; logs and receipts stay under `src/.work/`. Pass `-Dotnet` to the build/verification scripts, or use `AGDNS_DOTNET` with the verification wrappers, if the SDK is outside PATH. See [BUILDING.md](docs/BUILDING.md) for individual tiers, limitations, packaging, and the distinction between local and hosted checks.

Offline tests use synthetic responses, temporary files, loopback networking, and the disposable `HKCU:\Software\AgDnsTest` namespace. Do not put personal data in that namespace. They check that live DNS policy, the ownership journal, and real autostart stay unchanged. Do not run live integration, connect, restore, or provider benchmarks as ordinary CI checks.

## Pull requests

1. Start a focused branch from `main` in your fork or clone; keep unrelated changes out.
2. Inspect existing behavior and tests, make the smallest compatible change, and run the affected checks. Changes to packaging, the engine, or the SDK also need the full local gate on a non-elevated desktop.
3. State the problem, resulting behavior, exact commands/results, and any untested scenario in the PR. Include GUI screenshots when relevant and keep Russian/English user documentation consistent.

Do not include builds, ZIPs, logs, local settings, credentials, or signing material. Do not change provider trust, certificate checks, ownership/restoration rules, branding, or licensing incidentally. SDK or dependency changes need a reason and verification; this project currently has no third-party `PackageReference` entries. A passing technical CI run does not establish live provider availability or release acceptance.
