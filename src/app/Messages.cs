using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AntigravityGeminiDns {
 // What the single action next to a message does in the window.
 public enum ActionKind { None, Retry, Restore, Details, Fix, Switch }

 public sealed class CodeInfo {
  public string Code,Title,Text,Action;public ActionKind Kind;
 }

 // every Code the engine (Engine.ps1, Integration.ps1) or the exe (Program.cs, Recovery.cs, Connect.cs) can report -> one plain Russian sentence
 // and exactly one action. The log stays for details; a routine failure does not need it. A test walks the source for codes and fails on a missing entry.
 public static class CodeTable {
  static readonly Regex CodeToken=new Regex("^[A-Z][A-Z0-9_]+",RegexOptions.Compiled);
  static readonly Dictionary<string,CodeInfo> map=Build();

  static CodeInfo I(string code,string title,string text,string action,ActionKind kind)=>new CodeInfo{Code=code,Title=title,Text=text,Action=action,Kind=kind};
  static Dictionary<string,CodeInfo> Build() {
   var l=new List<CodeInfo>{
    // states and successful results
    I("NOT_CONFIGURED","Мост отключён","Мост не настроен, настройки DNS не менялись.","Нажмите «Подключить мост».",ActionKind.None),
    I("DNS_CONFIGURED_APPLICATION_NOT_TESTED","Мост подключён","DNS для доменов Google настроен. Работу Antigravity программа не проверяет.","Откройте Antigravity и проверьте вход.",ActionKind.None),
    I("ROLLED_BACK","Мост отключён","Записи программы удалены.","Действий не требуется.",ActionKind.None),
    I("ROLLED_BACK_SHARED_DOH_RETAINED","Мост отключён","Записи программы удалены. Общую DoH-запись, нужную другим правилам, оставили.","Действий не требуется.",ActionKind.None),
    I("LIVE_INTEGRATION_PASS","Проверка пройдена","Проверка на этом компьютере пройдена.","Действий не требуется.",ActionKind.None),
    // journal and recovery
    I("ASSISTED_SNAPSHOT_LIMIT","Маршрут не подготовлен","Проверка маршрута не завершена. Изменения не запускались.","Откройте «Подробнее» и вкладку «Логи».",ActionKind.Details),
    I("ASSISTED_PROVIDER_CHANGED","Проверка устарела","Сеть или настройки изменились. Маршрут не применялся.","Повторите подключение.",ActionKind.Retry),
    I("ASSISTED_PREVIEW_STALE","Проверка устарела","Сеть или настройки изменились. Маршрут не применялся.","Повторите подключение.",ActionKind.Retry),
    I("ASSISTED_PREVIEW_INVALID","Маршрут не подготовлен","Проверка маршрута не завершена. Изменения не запускались.","Откройте «Подробнее» и вкладку «Логи».",ActionKind.Details),
    I("ASSISTED_PREVIEW_EXPIRED","Проверка устарела","Сеть или настройки изменились. Маршрут не применялся.","Повторите подключение.",ActionKind.Retry),
    I("ASSISTED_PREPARE_REFUSED","Маршрут не подготовлен","Проверка маршрута не завершена. Изменения не запускались.","Откройте «Подробнее» и вкладку «Логи».",ActionKind.Details),
    I("ALL_PROVIDERS_UNAVAILABLE","Не удалось подключиться","Разрешённые профили не ответили. Мост не менялся.","Повторите подключение или откройте диагностику.",ActionKind.Retry),
    I("CONNECTION_SELECTION_TIMEOUT","Не удалось подключиться","Проверка провайдера заняла слишком много времени. Мост не менялся.","Повторите подключение или откройте диагностику.",ActionKind.Retry),
    I("ASSISTED_BASELINE_RESTORED","Настройки восстановлены","Маршрут не подтверждён. Настройки до операции восстановлены и проверены.","Проверьте сеть или выберите другого провайдера.",ActionKind.Retry),
    I("ASSISTED_CONTEXT_CHANGED","Проверка устарела","Сеть или настройки изменились. Маршрут не применялся.","Повторите подключение.",ActionKind.Retry),
    I("ASSISTED_NETWORK_SUSPENDED","Проверка приостановлена","Не удалось подтвердить текущий сетевой путь. Настройки DNS не менялись.","Проверьте сеть и повторите.",ActionKind.Retry),
    I("RECOVERY_REQUIRED","Требуется восстановление","Прошлая операция не завершена, часть настроек могла остаться.","Нажмите «Восстановить».",ActionKind.Restore),
    I("FAILED_RESTORABLE","Требуется восстановление","Подключение не удалось, откат неполный.","Нажмите «Восстановить».",ActionKind.Restore),
    I("OWNED_RULES_REMAIN","Требуется восстановление","Правила программы не удалились.","Нажмите «Восстановить» ещё раз.",ActionKind.Restore),
    I("DOH_RESTORE_FAILED","Требуется восстановление","DoH-запись программы не удалилась.","Нажмите «Восстановить» ещё раз.",ActionKind.Restore),
    I("RESTORE_BEFORE_SCOPE_UPGRADE","Требуется восстановление","Старая настройка охватывает не все домены.","Нажмите «Восстановить», затем подключите мост снова.",ActionKind.Restore),
    I("RESTORE_UNCONFIRMED","Отключение не подтверждено","Windows не подтвердила удаление записей вовремя. Журнал не изменён.","Выполните шаги из «Подробнее».",ActionKind.Details),
    I("WMI_NOT_RESPONDING","Служба WMI не отвечает","Служба WMI не отвечает, изменения не начаты.","Перезапустите службу WMI или компьютер и повторите.",ActionKind.Retry),
    I("STATE_INVALID","Журнал нечитаем","Журнал программы не читается, настройки не менялись.","Сохраните state.json и откройте «Подробнее».",ActionKind.Details),
    I("STATE_VERSION_UNSUPPORTED","Журнал другой версии","Эта версия программы не может обработать журнал; журнал и настройки сохранены.","Откройте журнал совместимой версией программы.",ActionKind.Details),
    I("DNS_CACHE_FLUSH_UNCONFIRMED","Восстановление не завершено","Правила удалены, но очистка кэша DNS не подтверждена; журнал сохранён для повторного восстановления.","Повторите восстановление через broker.",ActionKind.Details),
    I("LEASE_STATUS","Состояние аренды","Получено состояние ссылок владения настройками; изменений не выполнено.","Проверьте список действующих аренд.",ActionKind.None),
    I("LEASE_CONFIGURATION_CONFLICT","Настройки заняты","Настройки заняты другой конфигурацией или включены вручную.","Отключите настройки через их владельца.",ActionKind.Details),
    I("LEASE_EXPIRY_INVALID","Недопустимый срок аренды","Срок аренды должен быть в будущем и не превышать 24 часа.","Запросите аренду с допустимым сроком.",ActionKind.Details),
    I("LEASE_ID_REUSED","Идентификатор аренды уже занят","Этот идентификатор относится к другому владельцу или сроку аренды.","Используйте новый идентификатор для новой аренды.",ActionKind.Details),
    I("LEASE_IDENTITY_INVALID","Недопустимый владелец аренды","Идентификатор аренды или владельца не соответствует протоколу.","Повторите запрос через совместимый клиент broker.",ActionKind.Details),
    I("LEASE_LIMIT","Достигнут предел аренд","Все 128 ссылок владения заняты; новая аренда не добавлена.","Освободите ненужные аренды через их владельцев.",ActionKind.Details),
    I("LEASE_OWNER_MISMATCH","Аренда другого владельца","Чужая аренда не освобождена; настройки сохранены.","Освободите аренду из создавшей её учётной записи и сеанса.",ActionKind.Details),
    I("LEASE_PROVIDER_CHANGED","Провайдер изменился","Проверенные адреса отличаются от запрошенных; аренда не создана.","Повторите проверку провайдера перед запросом аренды.",ActionKind.Retry),
    I("STATE_PATH_REPARSE_POINT","Папка журнала небезопасна","Папка журнала заменена ссылкой, программа её не использует.","Удалите ссылку вручную, см. «Подробнее».",ActionKind.Details),
    I("STATE_DIR_UNTRUSTED","Папка журнала небезопасна","Папка журнала принадлежит не администратору.","Удалите папку вручную, см. «Подробнее».",ActionKind.Details),
    // conflicts and foreign settings
    I("NRPT_OR_HOSTS_CONFLICT","Конфликт правил","Эти домены уже заданы другим правилом DNS или записью в hosts.","Уберите чужое правило или запись и повторите.",ActionKind.Details),
    I("NRPT_CONFLICT","Конфликт правил","Эти домены уже заданы другим правилом DNS или записью в hosts. Ничего не изменено.","Уберите чужое правило или запись и повторите.",ActionKind.Details),
    I("DOH_REGISTRATION_CONFLICT","Конфликт DoH-записи","Для адреса провайдера в Windows уже есть другая DoH-запись. Ничего не изменено.","Выберите другой профиль или удалите чужую запись.",ActionKind.Details),
    I("DOH_CHANGED_INDEPENDENTLY","Запись DoH изменена","DoH-запись программы изменили без неё, она оставлена.","Проверьте запись, см. «Подробнее».",ActionKind.Details),
    I("DOH_OWNERSHIP_UNCERTAIN","Запись DoH неясна","Неясно, создала ли DoH-запись программа, она оставлена.","Проверьте запись, см. «Подробнее».",ActionKind.Details),
    I("OWNED_RULE_CHANGED","Правило изменено","Правило программы изменили вручную, оно оставлено.","Проверьте правило, см. «Подробнее».",ActionKind.Details),
    I("NRPT_NOT_EFFECTIVE","Правила не действуют","Правила созданы, но не действуют: мешает кэш DNS, VPN или приложение со своим DNS.","Нажмите «Диагностика» во вкладке «Замер».",ActionKind.Details),
    I("APPLY_READBACK_FAILED","Состояние изменилось","Windows не подтвердила применённые правила. Запущено восстановление.","Проверьте результат восстановления в «Подробнее».",ActionKind.Details),
    // provider and network
    I("DOH_NO_VALIDATED_ENDPOINT","Провайдер не прошёл проверку","Ни один адрес провайдера не ответил по DoH с верным сертификатом для всех доменов.","Выберите другой профиль или повторите позже.",ActionKind.Retry),
    I("DNS_TIMEOUT_OR_UNREACHABLE_OR_TLS_FAILURE","Провайдер не отвечает","Провайдер не отвечает или его сертификат не прошёл проверку.","Выберите другой профиль или повторите позже.",ActionKind.Retry),
    I("NETWORK_PROBE_FAILED","Сеть не проверена","Проверить сеть не удалось.","Проверьте интернет и повторите.",ActionKind.Retry),
    I("NO_ADDRESSES_FOR_PROFILE","Нет адресов","У профиля нет известных IP-адресов.","Нажмите «Подключить мост»: адреса будут найдены.",ActionKind.Retry),
    I("BOOTSTRAP_FAILED","Адреса не найдены","Найти адреса провайдера не удалось: нет связи или имя не определилось. Мост не менялся.","Проверьте интернет и повторите.",ActionKind.Retry),
    I("BOOTSTRAP_TIMEOUT","Адреса не найдены вовремя","Поиск адресов не уложился в 20 секунд. Мост не менялся.","Повторите позже или выберите другой профиль.",ActionKind.Retry),
    I("BOOTSTRAP_NO_ADDRESS","Адреса не прошли проверку","Ни один найденный адрес не прошёл проверку сертификата и DoH. Мост не менялся.","Выберите другой профиль.",ActionKind.Retry),
    I("DOH_TEMPLATE_INVALID","Неверный адрес DoH","Адрес DoH должен быть ссылкой https:// с именем хоста.","Исправьте адрес в «Настройках».",ActionKind.None),
    I("PROFILE_SWITCH_FAILED_PREVIOUS_RESTORED","Изменение не применилось","Новая настройка не применилась, прежний мост включён снова и проверен.","Выберите другой профиль или повторите.",ActionKind.None),
    I("APPLY_FAILED_ROLLED_BACK","Подключение не удалось","Все изменения отменены.","Повторите или выберите другой профиль.",ActionKind.Retry),
    I("DOH_ADD_VERIFICATION_FAILED","Подключение не удалось","Windows не подтвердила добавленную DoH-запись.","Повторите, а если не помогло — «Диагностика».",ActionKind.Retry),
    // Windows support, permissions, concurrency
    I("NRPT_UNSUPPORTED_BY_OS","Windows не поддерживается","Эта версия Windows не поддерживает правила NRPT.","Нужна Windows 10 или 11 x64 с обновлениями.",ActionKind.None),
    I("DOH_UNSUPPORTED_BY_OS","Windows не поддерживается","Эта версия Windows не поддерживает DNS поверх HTTPS.","Обновите Windows до 10 (2004+) или 11.",ActionKind.None),
    I("ADMIN_REQUIRED","Нужны права администратора","Для изменения DNS нужны права администратора.","Подтвердите запрос Windows (UAC).",ActionKind.Retry),
    I("UAC_CANCELLED","Операция отменена","Запрос Windows отклонён, изменений нет.","Повторите и подтвердите запрос.",ActionKind.Retry),
    I("TRUST_DECISION_REQUIRED","Нужно ваше решение","Нужно подтвердить доверие провайдеру.","Нажмите «Подключить мост» и подтвердите.",ActionKind.Retry),
    I("OPERATION_IN_PROGRESS","Операция уже идёт","Другая операция программы ещё выполняется.","Подождите минуту и повторите.",ActionKind.Retry),
    I("AUTOSTART_TASK_QUERY_FAILED","Автозапуск не проверен","Не удалось проверить владельца задачи автозапуска. Задача сохранена.","Проверьте планировщик заданий и повторите.",ActionKind.Retry),
    I("AUTOSTART_TASK_NOT_OWNED","Чужая задача сохранена","Задача с таким именем принадлежит другой программе или пользователю.","Проверьте задачу в планировщике заданий.",ActionKind.Details),
    I("AUTOSTART_TASK_DELETE_FAILED","Автозапуск не отключён","Удалить проверенную задачу автозапуска не удалось.","Проверьте планировщик заданий и повторите.",ActionKind.Retry),
    // no usable address for this provider; another one is offered, never applied by itself
    I("PROVIDER_SWITCH_OFFERED","Есть рабочий провайдер","Другой профиль каталога прошёл проверку всех доменов. Ничего не изменено.","Нажмите «Подключить через …» и подтвердите доверие.",ActionKind.Switch),
    I("NO_WORKING_PROVIDER","Нет рабочего провайдера","Ни один профиль каталога не прошёл проверку всех доменов на этой сети. Мост не менялся.","Повторите позже или добавьте свой профиль.",ActionKind.Retry),
    I("BOGUS_ANSWER","Ответ не из публичной сети","Провайдер вернул адрес из локальной, служебной или частной сети. Такой профиль не считается рабочим.","Выберите другой профиль или повторите замер.",ActionKind.Retry),
    // tray health check (1.5-1): read-only; the one action is "Починить" (one UAC) or nothing
    I("HEALTH_OK","Мост в порядке","Правила на месте, провайдер отвечает, Windows возвращает его адреса.","Действий не требуется.",ActionKind.None),
    I("HEALTH_DRIFT_RULES","Мост изменился","Правила или DoH-записи программы изменились либо не действуют.","Нажмите «Починить» (один запрос Windows).",ActionKind.Fix),
    I("HEALTH_PROVIDER_DOWN","Провайдер не отвечает","Адреса провайдера не отвечают по DoH.","Нажмите «Починить»: адреса найдутся заново или будет предложен другой провайдер.",ActionKind.Fix),
    I("HEALTH_RESOLVER_MISMATCH","Windows отвечает иначе","Правила на месте, но Windows отвечает не адресами провайдера.","Откройте «Логи» и нажмите «Диагностика».",ActionKind.Details),
    I("HEALTH_NEEDS_MANUAL","Нужна ручная проверка","Правило программы изменили или появилось чужое правило.","Откройте «Подробнее» и проверьте правило.",ActionKind.Details),
    I("HEALTH_UNKNOWN","Проверка не удалась","Состояние моста проверить не удалось. Мост не менялся.","Проверка повторится позже.",ActionKind.Retry),
    // support report (1.5-2)
    I("REPORT_SAVED","Отчёт сохранён","Файл сохранён на этом компьютере и никуда не отправлен. Имя пользователя, имя компьютера, личные пути и имена сетей вырезаны.","Проверьте файл и при желании отправьте его сами.",ActionKind.None),
    I("REPORT_SAVE_FAILED","Отчёт не сохранён","Записать файл не удалось.","Выберите другую папку и повторите.",ActionKind.Retry),
    // engine and exe plumbing
    I("ENGINE_TIMEOUT","Windows не ответила","Windows не ответила вовремя: возможно, завис WMI или DNS-клиент.","Повторите или перезапустите службу WMI.",ActionKind.Retry),
    I("ELEVATION_TIMEOUT","Слишком долго","Операция идёт слишком долго и может ещё выполняться.","Подождите минуту и проверьте состояние.",ActionKind.Retry),
    I("ELEVATION_START_FAILED","Права не получены","Запросить права администратора не удалось.","Запустите программу заново.",ActionKind.Retry),
    I("NO_MATCHING_OPERATION_RECEIPT","Итог неизвестен","Итог операции неизвестен.","Проверьте состояние или нажмите «Диагностика».",ActionKind.Retry),
    I("ENGINE_ERROR","Сбой служебного скрипта","Служебный скрипт завершился с ошибкой.","Откройте «Подробнее» и вкладку «Логи».",ActionKind.Details),
    I("ENGINE_START_FAILED","PowerShell не запущен","Запустить Windows PowerShell не удалось.","Проверьте, что PowerShell доступен, и повторите.",ActionKind.Retry),
    I("ENGINE_TARGETS_MISSING","Файлы повреждены","Встроенная конфигурация программы повреждена.","Скачайте программу заново.",ActionKind.None),
    I("SCRIPT_TAMPERED","Файлы изменены","Служебный скрипт изменён, запуск отменён.","Перезапустите программу, а если не помогло — скачайте заново.",ActionKind.None),
    I("SCRIPT_STORE_FAILED","Файлы не подготовлены","Подготовить служебные файлы не удалось.","Освободите место на диске и повторите.",ActionKind.Retry),
    I("LIVE_INTEGRATION_FAILED","Проверка не пройдена","Проверка на этом компьютере не пройдена.","Откройте «Подробнее» и вкладку «Логи».",ActionKind.Details),
   };
   var d=new Dictionary<string,CodeInfo>(StringComparer.Ordinal);foreach(var i in l)d[i.Code]=i;return d;
  }

  // Wording the window composes from a table entry; kept here so the text-fit test measures it as well.
  public static string SwitchOfferText(string from,string to)=>L.F("Профиль «{0}» не дал рабочих адресов, «{1}» прошёл проверку. Ничего не изменено.",from,to);
  public const string ProfileNotAppliedTitle="Профиль не применён";
  public const string ProfileNotAppliedText="Новый профиль не применился, прежний вернуть не удалось. Мост отключён, записей программы нет.";
  public const string ProfileNotAppliedAction="Повторите или выберите другой профиль.";
  // With a wider scope (+ Gemini, all Google), a provider that has no address for the extra hosts fails like any provider without addresses; the way out is the scope selector.
  public const string GeminiAction="Выберите ниже «Antigravity» или другой профиль.";
  public static string ActionFor(CodeInfo info,bool gemini)=>gemini&&(info.Code=="BOOTSTRAP_NO_ADDRESS"||info.Code=="DOH_NO_VALIDATED_ENDPOINT"||info.Code=="DNS_TIMEOUT_OR_UNREACHABLE_OR_TLS_FAILURE")?L.T(GeminiAction):info.Action;
  public static IReadOnlyCollection<CodeInfo> All=>map.Values;
  public static string TokenOf(string raw)=>string.IsNullOrWhiteSpace(raw)?"":(CodeToken.Match(raw.Trim()) is var m&&m.Success?m.Value:"");
  public static bool Known(string raw)=>map.ContainsKey(TokenOf(raw));
  // Engine messages look like "CODE: detail"; only the leading token selects the entry. An unknown code still gets a sentence and the Details action.
  public static CodeInfo Lookup(string raw) {
   string token=TokenOf(raw);
   if(token.Length>0&&map.TryGetValue(token,out var info))return L.Code(info);   // the window's language; the table itself (All) stays Russian
   var unknown=L.Code(new CodeInfo{Code="",Title="Неожиданный ответ",Text="Программа получила неизвестный ответ.",Action="Откройте «Подробнее» и вкладку «Логи».",Kind=ActionKind.Details});
   return new CodeInfo{Code=token,Title=unknown.Title,Text=unknown.Text,Action=unknown.Action,Kind=unknown.Kind};
  }
 }
}
