# Авто-прокси: адаптивная маршрутизация (design)

Дата: 2026-08-24 · Статус: approved (brainstorming) · Версия: 0.1

## Контекст и цель

`geosite:ru-blocked` не покрывает все ресурсы: есть «серые» хосты, которые
деградируют без прокси (DPI-троттлинг по SNI), но формально не заблокированы
и уходят в `direct` (catch-all в xray-конфиге). Нужен модуль, который
автоматически обнаруживает такие хосты, выводит их в `proxy`, закрепляет
на будущее, а при выздоровлении возвращает в `direct`.

Ключевое архитектурное ограничение (проверено по докам обоих ядер):
**ни sing-box, ни xray не отдают телеметрию по исходам соединений и не дают
hot-подменить routing-правила без рестарта процесса.** Поэтому:
- «понять по отклику» = **активное прощупывание** (HTTP через два пути),
- применить изменение = **per-core рестарт** затронутого ядра
  (для доменных правил — только xray).

## Архитектура

```
sing-box stdout (OnLog)
   → CandidateCollector: парсит DNS-запросы, фильтрует, кладёт в очередь
   → ProbeService: HTTP HEAD через xray SOCKS 10810 (direct) и 10811 (force-proxy)
   → AutoProxyEngine: классификация (гистерезис) → write rule → reload xray
   → TTL-цикл (24ч): перепроверка авто-хостов → revert → reload xray
```

Пути замера: у xray уже есть два SOCKS-инбаунда — `socks-in` (10810,
blocked-only: не-заблокированное → direct) и `force-in` (10811, всегда → VPN).
Сравнение через них единообразно и в TUN-, и в прокси-режиме.

## Компоненты

### 1. `Services/CandidateCollector.cs` (новый)

- Подписывается на `App.Core.OnLog` (stdout sing-box уже перехватывается
  `CoreManager`). Подписка при старте ядра, отписка при stop.
- Парсит строки DNS-запросов sing-box. **Формат и log-уровень — spike-пункт №1**
  (см. Риски): в конфиге sing-box log level поднимается `warn → info`;
  если запросы не печатаются на `info` — пробуем `debug`, иначе другой источник.
- Из строки извлекается hostname (regex, точный паттерн — после spike).
- Фильтрация кандидатов (skip):
  - IP-литералы, localhost, `*.local`;
  - уже известные хосты (есть правило в `routing-rules.json`);
  - домен VPN-сервера (`App.Servers[SelectedServerIndex].Address`);
  - известные DNS-провайдеры (`dns.google`, `one.one.one.one`, `cloudflare-dns.com`
    и т.д. — набор из `DnsHosts` конфигов);
  - поддомены уже известного хоста (суффиксное совпадение).
- Дедуп по времени: один хост — не чаще раза в 5 минут (in-memory).
- Выход: `ConcurrentQueue<string>` кандидатов.

### 2. `Services/ProbeService.cs` (новый)

- `Task<ProbeResult> ProbeAsync(string host, CancellationToken ct)`.
- `ProbeResult { Host, DirectOk, DirectMs, ProxyOk, ProxyMs }`.
- Два HTTP-клиента через SOCKS5-прокси `127.0.0.1:10810` (direct) и
  `127.0.0.1:10811` (proxy). Запрос: `HEAD https://host/`, таймаут 5с.
- SOCKS5 через `WebProxy("socks5://127.0.0.1:port")` (`SocketsHttpHandler`
  поддерживает socks5). Если не работает — spike-пункт №2: сырой SOCKS5
  через `TcpClient` или запасной путь.
- Если оба пути недоступны (и direct, и proxy) — результат
  «неопределённый»: не учим, не откатываем.
- Параллельность: до 4 одновременных проб.
- `PingService` не трогаем (остаётся для ручного пинга); подход переиспользуем.

### 3. `Services/AutoProxyEngine.cs` (новый)

Оркестратор. Фоновые циклы, всё в try/catch — ошибки не роняют приложение.

**Цикл кандидатов:**
- Достаёт кандидата из очереди → `ProbeAsync` → вердикт:
  - `bad`: `!DirectOk`, **или** (`ProxyOk && DirectMs > 3 × ProxyMs`);
  - `good`: `DirectOk` (и не `bad`);
  - `inconclusive`: оба пути упали → skip.
- Гистерезис: хост учится после **2 подряд bad** (счётчик in-memory,
  `ConsecutiveBad`).
- Learn: `RoutingRule { MatchType=Domain, Action=Proxy, Value=host,
  IsAutoLearned=true, IsEnabled=true, Description="auto: <дата>",
  LastCheckedAt=now }` → `App.Rules.Save(...)` → reload xray.
- Кулдаун и лимиты: 5 мин между пробами хоста; максимум 500 авто-хостов
  (при превышении вытесняем по `LastCheckedAt`).

**TTL-цикл (auto-heal):**
- Каждые 24ч пересматривает авто-правила:
  - `ProbeAsync` по direct-пути (для этого хоста достаточно direct-замера);
  - good = `DirectOk` (direct стабильно отвечает; **без** сравнения с proxy —
    откат консервативен: возвращаем только когда direct явно работает);
  - good → `ConsecutiveGood++` (in-memory); `ConsecutiveGood >= 3` → удалить
    правило → reload xray;
  - bad/inconclusive → `ConsecutiveGood = 0`, `LastCheckedAt` обновляется;
  - сохраняет `LastCheckedAt` после прохода.

### 4. `Models/RoutingRule.cs` (изменить)

```csharp
public bool IsAutoLearned { get; set; }   // флаг «найдено модулем»
public DateTime? LastCheckedAt { get; set; } // для TTL-цикла
```

`ConsecutiveBad` / `ConsecutiveGood` — **in-memory** в движке (после рестарта
приложения счётчики обнуляются — это ок: повторные 2/3 пробы за сессию).

### 5. `Services/RoutingRuleService.cs` — без изменений

Авто-правила живут в том же `routing-rules.json`. `ConfigGenerator` уже
вставляет кастомные Domain-правила до catch-all `direct` — без изменений.
`RoutingRulesViewModel` фильтрует по `IsAutoLearned`, чтобы не смешивать с
ручными.

### 6. UI: секция «Авто-прокси»

- `RoutingRulesWindow` (XAML + VM): вторая вкладка/список «Авто-прокси» —
  авто-хосты: host, последняя проверка, кнопка «Удалить» (от ложных
  срабатываний). Удаление → save → reload xray.
- `RoutingRulesViewModel`: `ObservableCollection<RoutingRule> AutoRules`
  (фильтр `IsAutoLearned`), `DeleteAutoRuleCommand`.

### 7. Reload: `App.ReloadXrayAsync()` (изменить `App.xaml.cs`, `CoreManager.cs`)

- `CoreManager.RestartXrayAsync()`: стоп xray → старт с текущим конфигом →
  ждём порт 10810 (`WaitForPortAsync`). sing-box не трогаем (его outbound
  указывает на xray SOCKS — переживёт рестарт xray).
- Валидация перед рестартом: `xray -test -c config.json` (exit 0). Провал →
  лог, старое ядро продолжает работать.
- `App.ReloadXrayAsync()`: перегенерить xray-конфиг
  (`ConfigGenerator.Generate` с кастомными правилами) → записать → рестарт xray.
  Только если `Core?.IsRunning == true`; иначе — просто записать файл
  (применится при следующем коннекте).
- Статус трея/окна не трогаем.

## Изменение конфигов

- `SingBoxConfigGenerator`: log level `warn → info` (для DNS-запросов).
  Spike подтверждает, что этого достаточно.

## Пороги (стартовые, константы в `AutoProxyEngine`)

| Параметр | Значение |
|---|---|
| Таймаут пробы | 5 с |
| Порог троттлинга | `DirectMs > 3 × ProxyMs` (и `ProxyOk`) |
| Обучение | 2 подряд bad |
| Откат | 3 подряд good |
| TTL перепроверки | 24 ч |
| Кулдаун хоста | 5 мин |
| Лимит авто-хостов | 500 |
| Параллельных проб | 4 |

## Тестирование

- Классификатор — чистая функция (вердикт по `ProbeResult`): минимальный
  runnable-чек (assert-самопроверка или один `test_*.cs`) на bad/good/
  inconclusive-ветки.
- Ручной сценарий: подключиться → открыть медленный/заблокированный сайт →
  хост появляется в «Авто-прокси», правило в `routing-rules.json`, xray
  перезапущен; удалить вручную — правило исчезает.

## Риски

1. **Формат DNS-лога sing-box** — может печататься на `debug`, не `info`.
   Spike №1 до написания `CandidateCollector`. Fallback: `debug`, либо
   разбор через другой сигнал.
2. SOCKS5 через `WebProxy` может не завестись на хосте — spike №2.
3. Ложные срабатывания — гасятся гистерезисом (2×), ручным удалением и
   auto-heal (3× good).
4. Нагрузка/приватность DNS-лога — дедуп, кулдауны, лимиты, исключения.

## Вне скоупа

- Hot-reload правил sing-box (`process_name`) — невозможно на уровне ядра,
  не делаем. Модуль пишет только Domain-правила (xray).
- Статистика/графики в UI.
- Авто-детект при выключенном подключении.
