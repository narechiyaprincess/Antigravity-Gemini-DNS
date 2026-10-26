# Архитектура 1.1.0

`Program.cs` проверяет аргументы, обслуживает CLI и запускает WinForms.
`MainForm` и partial-файлы управляют пятью вкладками, состояниями операций,
отменой, RU/EN, DPI, треем и настройками. UI не является вторым журналом DNS.

`profiles.json` — встроенный каталог, exact hosts и Google suffix namespaces.
`Catalog`/`RuntimeCatalog` строят профили; custom DoH проверяется теми же правилами
HTTPS/public pins. `SignedCatalog` — отдельный opt-in локальный тестовый контракт.

`Benchmark` использует bounded concurrent `LiveTransport`, DoH с TLS по hostname
и pinned IP, проверки ответов и TLS до targets. Замеры содержат availability,
median/P95, TLS, stability, reach и результат по доменам. Выбор требует валидных
завершённых данных. Persisted history показывается как старая; она не даёт свежего
разрешения применять профиль. `Speed`, `ScenarioProbes` и `Doctor` разделяют
доступность сети, провайдера, политики и готовность приложения.

`Connect`/`Switch`/`ConnectionTrust` выполняют ограниченный подбор разрешённых
профилей, direct-first и переходы с readback. `Health` выполняет read-only проверки;
opt-in fallback предлагает замену с подтверждением пользователя.

`ScriptStore` извлекает embedded Engine/Integration/EmergencyRestore/catalog в
отдельную staging-папку, сверяет SHA-256 и ACL. `Engine.ps1` применяет только
проверенный scope, пишет durable ownership journal в
`%ProgramData%/AntigravityDnsUnlocker/state.json`, блокирует одновременную mutation,
проверяет fingerprint NRPT и ownership DoH перед restore. Неопределённый child
и промежуточные phases требуют recovery. Чужие/изменённые/общие записи сохраняются.
`Recovery` формирует инструкции ручного восстановления; `EmergencyRestore.ps1`
использует тот же engine. Адаптерный DNS, hosts и системный proxy не переписываются.

`AppSettings`, benchmark/operation history, grants и UI state находятся в
`%LocalAppData%/AntigravityDnsUnlocker`. `ConfigFile` переносит предпочтения,
не журнал или адреса компьютера. `SupportReport` формирует локальный редактируемый
отчёт с redaction и preview, без автоматической отправки.

`ElevatedStartup` обслуживает отдельный opt-in protected-copy logon task.
`AssistedCoordinator`/`AssistedNative` и `PolicyCompiler` участвуют в обычном
GUI-пути подключения, проверки и восстановления; прямые CLI `--enable`/`--restore`
обращаются к движку без координатора. `Broker` и локальные подписанные каталоги
остаются отдельными experimental возможностями. Их контракты: [BROKER](../src/BROKER.md),
[ASSISTED](../src/ASSISTED.md), [CATALOG](../src/CATALOG.md),
[SCENARIO-PROBES](../src/SCENARIO-PROBES.md).

Имена каталогов/registry/task старых выпусков сохранены ради восстановления:
[compatibility identifiers](COMPATIBILITY-IDENTIFIERS.md).

## Известные ограничения

Native-контекст ещё включает второстепенные NIC: их изменение может консервативно отменить операцию. Ускоренное подтверждение по результату engine не используется обычным координатором: выполняется полная диагностика. Это два открытых P2, не исправленные в v1.1.0; ускорение подключения на 6–8 секунд не заявляется. [Полные ограничения и upgrade](RELEASE-NOTES-1.1.0.md).
