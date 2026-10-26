# Сборка и безопасная проверка

Требуются Windows 11 x64, Windows PowerShell 5.1 и .NET SDK 10.0.402
(либо более новый patch той же feature band согласно `global.json`). Windows 10
в этом выпуске не проверялась. Для restore нужны NuGet.org или уже заполненный
локальный cache runtime packs. Для обычной сборки права администратора не нужны.

Из корня отдельно скопированного проекта:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\app\Build.ps1
```

Результат: `src/.work/build/AntigravityGeminiDns.exe`. `Build.ps1` не изменяет
пользовательский PATH, ярлык или `release/`. Промежуточные `bin/obj` находятся
в отдельном временном каталоге; явно заданный `-BuildRoot` сохраняется.
Другой output: `-OutputDirectory <путь>`. Offline restore с заполненным cache:
`-RestoreSource "$env:USERPROFILE/.nuget/packages"`. Проверяется минимум 10 GiB
свободного места; `AGDNS_MIN_FREE_GB` может изменить порог.

Финальный рецепт: Release, `net10.0-windows`, `win-x64`, self-contained,
single-file, compression on, ReadyToRun off, trimming off, COM/reflection on,
без PDB. Версия берётся из `<Version>` в `.csproj`. Trimming для WinForms
[не поддерживается SDK](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities#windows-forms).
PathMap и deterministic compilation сокращают зависимость от пути проекта;
побайтовая идентичность между разными SDK/runtime packs не обещается.

Полный безопасный gate (уникальный Tag сохраняет прежние логи):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\tools\verify.ps1 -Tag local-unique -NoPublish
```

Он запускает build, self-test, offline E2E, UI smoke/states/stress/surfaces,
RU/EN при 96/120/144/192 DPI и EXE отдельно в пустой папке. Логи и JSON receipts:
`src/.work/logs/<Tag>/`. `-NoBuild -ExeOverride <absolute.exe>` проверяет готовый
EXE. `-Publish` заменяет локальный `release/` и ярлык; для тестирования его не
передавать. Offline suite использует только изолированный `HKCU/Software/AgDnsTest`
и остановится, если он уже существует. Не запускать два таких набора одновременно.

Дополнительные offline suites:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\app\Engine.S05.Tests.ps1 -ScratchPath .\src\.work\engine-fixture
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\app\Engine.Gaps.Tests.ps1 -ScratchPath .\src\.work\gaps-fixture
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\app\AssistedNative.Tests.ps1 -ScratchPath .\src\.work\assisted-fixture
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\tests\Tooling.Tests.ps1 -EvidenceDirectory .\src\.work\tooling-fixture
```

`--integration`/`Integration.ps1`, `--enable`, `--restore`, broker installation,
real startup changes и live assisted leases меняют систему. Они требуют отдельного
разрешения и подходящего изолированного Windows окружения; offline PASS их не заменяет.
## Короткий CI gate

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\tools\verify-ci.ps1
```

Реальные workflows: [CI](../.github/workflows/ci.yml) на push main/codex branches и PR; [ручная проверка source](../.github/workflows/release.yml) только workflow_dispatch. Оба используют pinned SDK из global.json и `verify-ci.ps1`: Build.ps1, --version, --self-test, --verify-scripts, mock Engine.Tests и offline tier 14 с проверкой Completed/HostUnchanged. Полный локальный gate выше включает все tiers 1–15. CI не передаёт -Publish, не запускает UI/live integration и не публикует Release. Build.ps1 внутри вызывает dotnet publish для создания изолированного EXE; это локальная компиляция, не публикация на GitHub и не замена работающего приложения.

Логи: `src/.work/logs/ci/`; E2E receipt: `src/.work/offline_e2e_receipt.json`. `src/tools/ci/build.yml` — безопасный manual-only шаблон, не второй активный workflow. Локальный PASS не доказывает успешный hosted run.
