# Ревью CORDON / StalkerModLauncher

Дата: 2026-11-05. Объект: текущее состояние проекта, а не только последний commit.

Исходники, настройки анализаторов и постоянные тесты не изменялись. Проверки выполнены на Windows x64, .NET SDK 8.0.421. Временный smoke-проект вне репозитория вызывал реальные production-сервисы и view-model; сетевые ответы задавались локальными детерминированными фикстурами.

Итог: **7 ошибок воспроизведены на production-коде**, включая один P1 с удалением исходника; отдельно отмечены один статический риск обновления и два замечания по производительности/сопровождаемости.

## Результаты автоматических проверок

| Проверка | Результат |
| --- | --- |
| `git diff --check` | Без замечаний на момент проверки |
| `dotnet format StalkerModLauncher.sln --verify-no-changes --no-restore` | Успешно |
| `dotnet build StalkerModLauncher.sln -c Release --no-restore` | Успешно; 0 предупреждений, 0 ошибок |
| `dotnet test StalkerModLauncher.sln -c Release --no-restore --no-build` | 814 пройдено; 0 ошибок; 0 пропущено |
| `dotnet list StalkerModLauncher.sln package --vulnerable --include-transitive` | Для обоих проектов NuGet не сообщил известных уязвимых прямых или транзитивных пакетов |
| `pwsh -NoProfile -File scripts/Test-UsvfsRuntime.ps1` | По 5 успешных итераций x64 и x86; также пройдены проверки writable directory, appdata alias и launcher-settings round-trip |
| `pwsh -NoProfile -File scripts/Test-RepositoryGuards.ps1` | Пройдены release guards, изоляция USVFS-тестов, CI coverage guards, native diagnostics и packaging fixtures |

Линт здесь — существующие Roslyn/.NET-анализаторы и `dotnet format`, не отдельный сторонний аудит. В `Directory.Build.props:3-7` включены .NET-анализаторы, `8.0-recommended`, проверка code style при сборке и warnings-as-errors.

Первый запуск repository guards пересёкся с runtime smoke и штатно остановился на общем mutex USVFS. После завершения smoke последовательный запуск guards прошёл. Это не дефект проекта.

## Подтверждённые ошибки

Приоритеты: **P1** — высокий риск потери данных/неработоспособности основного сценария; **P2** — функциональный дефект с конкретным условием; **P3** — сопровождаемость или ограниченная проблема производительности.

### P1. Удаление профиля может удалить исходный мод через junction в родительском пути

**Места:** `src/StalkerModLauncher/Services/WorkspaceBuilder.cs:53-79,160-175,197-215`; `src/StalkerModLauncher/Services/FileSystemSafety.cs:58-64,81-95`.

Проверки границ и пересечения с исходниками сравнивают нормализованные строки, но не фактический путь через reparse points. Если `Workspaces` — junction на каталог модов, `Workspaces\profile-<id>` и реальный `Mods\profile-<id>` считаются разными каталогами. Восстановление отсутствующей метки workspace записывает её в исходный каталог, а последующее рекурсивное удаление проходит через junction родителя и удаляет настоящий исходник. Это не случай удаления самой ссылки: ссылка находится выше удаляемого каталога.

**Проверка:** только временная фикстура вне репозитория:

1. Создан `Sources\Mods\profile-junctionprobe\original.txt`.
2. Создан junction `Managed\Workspaces` → `Sources\Mods`.
3. В профиле `Id = junctionprobe`, `WorkspacePath = Managed\Workspaces\profile-junctionprobe`, а `ModEntry.SourcePath` указывает на настоящий `Sources\Mods\profile-junctionprobe`.
4. Вызван production `ProfileManager.Delete` с настоящим `WorkspaceBuilder`, как в `AppServices.cs:16-18`.

```text
JUNCTION_DELETE error=none originalFileExists=False sourceDirectoryExists=False profileRemoved=True
```

Исходный файл и каталог удалены без отказа защиты. Фикстура и junction после проверки удалены; пользовательские исходники не использовались.

**Исправление:** до создания/восстановления меток, `Detach` и рекурсивного удаления запрещать reparse points во всей цепочке родителей либо разрешать фактические пути и повторять проверки владения и пересечения с источниками. Уже есть `WorkspaceBuilder.HasDirectoryLinks` (`:325-330`), но ветка удаления его не использует. Защиту необходимо применять и в ответственном файловом слое, а не только в UI.


### P2. Исключённый файл остаётся видимым в USVFS, если исчез последний разрешённый provider

**Места:** `src/StalkerModLauncher/Services/UsvfsMappingPlanBuilder.cs:31-43,204-228`.

Каталог мода сначала подключается целиком рекурсивным mapping, включая `ExcludedFiles`. Затем исключения компенсируются точечным mapping разрешённого provider. Если `FindFinalFile(relativePath)` возвращает `null`, код делает `continue`, но первоначальный directory mapping остаётся. Сценарий: конфликтный файл исключён в моде B, затем мод A отключён/удалён, а базовая игра этого файла не содержит. План слоёв считает файл отсутствующим; runtime продолжает показывать файл B.

**Проверка:** production `FileLayerPlan` → `OverlayManifestBuilder` → `UsvfsMappingPlanBuilder` → `UsvfsRuntime`/`OfficialUsvfsNativeApi`; настоящий x64 `cmd.exe` запущен с внедрённым USVFS и проверил существование виртуального файла. Exit code 42 означает, что исключённый файл виден.

```text
EXCLUSION_NATIVE planProviderAbsent=True childExit=42 excludedFileVisible=True physicalGameFileExists=False
```

**Исправление:** не включать исключённый файл в исходный mapping слоя, когда разрешённого fallback нет. Нужен точный mapping с учётом исключений, а не только восстановление предыдущего provider. Исходники модов не переименовывать и не удалять. Проверить переходы «исключить B → отключить A» и «fallback есть → fallback исчез».

### P2. USVFS-запуск получает физически отсутствующую рабочую папку

**Места:** `src/StalkerModLauncher/Services/UsvfsLaunchBackend.cs:124-126,157-175,294-322`; `src/StalkerModLauncher/Services/ProfileLaunchPlanResolver.cs:225-234`.

Для вручную выбранного `mod\config\custom.ltx` рабочая папка вычисляется как `game\config`. Если EXE принадлежит базе, а data-only мод не содержит EXE/DLL рядом с ним, выбирается физический корень игры без bootstrap. `config` существует лишь в виртуальном mapping. Создание процесса требует существующую физическую рабочую папку ещё до внедрения USVFS и завершается ошибкой.

**Проверка:** временная игровая фикстура с x64 PE (`cmd.exe`, скопированный как `bin\xr_3da.exe`) и data-only модом; выполнены настоящие `UsvfsLaunchBackend.PrepareAsync` и `LaunchPlan.ProcessStarter`.

```text
WORKING_DIRECTORY preparedExists=False relative=config
WORKING_DIRECTORY started=False nativeError=267
WORKING_DIRECTORY sourceConfigCreated=False
```

Ошибка Windows 267 — недопустимое имя каталога. В исходную игровую фикстуру каталог не создавался.

**Исправление:** выбирать согласованный bootstrap с физически подготовленной рабочей папкой, когда вычисленный каталог не существует в базе. Не исправлять созданием папок в исходной игре. Проверить также маршрутизацию x86, которая получает ту же рабочую папку.

### P2. Копирование workspace в локальное хранилище пропускает последние bootstrap-настройки

**Места:** `src/StalkerModLauncher/Services/WorkspaceManagementService.cs:173-192`; UI-вызов — `src/StalkerModLauncher/ViewModels/ProfileSettingsViewModel.cs:367-375`.

Ветка `MoveAsync` вызывает `CaptureFromWorkspace`, но `CopyToLocalAsync` её пропускает и копирует только `userdata`. После аварийного завершения или ошибки финального capture актуальные `AnomalyLauncher.cfg`, `commandline.txt`, `launcherconfig.toml` могут оставаться в `.usvfs-bootstrap`, а persistent store содержит прежнюю версию. Профиль переключается на новую копию с устаревшими настройками.

**Проверка:** в старом workspace записана исходная версия в persistent store и bootstrap; записан baseline, затем изменён только bootstrap. Выполнен настоящий `WorkspaceManagementService.CopyToLocalAsync`.

```text
COPY_CONFIG moved=True sourceBootstrap='latest-bootstrap-edit' copiedStored='stored-before-crash'
```

Старая версия workspace остаётся на диске; это потеря актуальности настроек активной копии, а не доказанное безвозвратное удаление старой версии.

**Исправление:** после копирования `userdata` в staging захватить изменения из старого bootstrap в новую копию через `CaptureFromWorkspace(oldBootstrap, temporary, progress)`, сохранив конфликтные backups и не изменяя исходный workspace.

### P2. Потоковое чтение HTTP-тела не ограничено установленным таймаутом

**Места:** `src/StalkerModLauncher/Services/LauncherReleaseDownloadService.cs:31-49,158-175,194-199`; `src/StalkerModLauncher/Services/ApProCatalogService.cs:211-226,229-265,314-319`.

- `GetAsync(..., ResponseHeadersRead, token)` завершает HTTP-операцию после заголовков. `HttpClient.Timeout` больше не ограничивает чтение `response.Content`.
- Если сервер вернул успешные заголовки, но перестал передавать тело и не закрыл соединение, `ReadAsync` ждёт без собственного дедлайна.
- Скачивание обновления из `LauncherSettingsViewModel.cs:369` не передаёт пользовательский отменяемый токен. Команда остаётся выполняющейся; файл `.partial` и его дескриптор остаются открытыми до отмены/завершения процесса.
- В каталоге зависшее чтение страницы удерживает `_catalogRequestLock`; новые запросы страниц не смогут пройти. Зависшие миниатюры занимают разрешения `_thumbnailDownloadLimit`.

**Проверка:** production `ApProCatalogService` получил через внедряемый `HttpMessageHandler` успешный ответ с потоком, который ожидал отмены. Через 21 секунду операция не завершилась, хотя в сервисе задан `Timeout = 20 s`; после явной отмены токена завершилась с отменой.

```text
CATALOG_BODY_STALL httpTimeoutSeconds=20 observedSeconds=21,0 completed=False
CATALOG_BODY_STALL manuallyCancelled=True
```

Для загрузчика обновлений вывод основан на том же прочитанном пути `ResponseHeadersRead` → `CopyWithLimitAsync`; зависание скачивания настоящего release-архива не запускалось.

**Исправление:** связанный `CancellationTokenSource` с ограничением времени чтения тела; передать его токен во все потоковые чтения/копирование. Ограничения размера сохранить. Для больших архивов выбрать разумный общий срок или таймаут отсутствия прогресса.

Контракт API: [Microsoft: HttpCompletionOption, Remarks](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-8.0#remarks).

### P2. Отменённый refresh снимает занятость нового анализа конфликтов

**Места:** `src/StalkerModLauncher/ViewModels/ConflictExplorerViewModel.cs:187-228`; потребители флага — `:46-49,139-147`.

- При смене выбранного мода вызывается новый `RefreshAsync`, предыдущий получает отмену.
- Новый refresh устанавливает `IsBusy = true`, но `finally` старого refresh безусловно записывает `false`.
- Пока новый анализ ещё идёт, команды refresh и исключения файлов снова становятся доступными; отображаемые списки ещё могут относиться к прежнему анализу.

**Проверка:** настоящий WPF `Dispatcher` в STA-потоке; два мода по 3000 файлов; refresh из конструктора немедленно заменён следующим. Зафиксировано `IsBusy == false` при `Summary == Conflict_Analyzing` до окончания нового расчёта.

```text
CONFLICT_REFRESH_RACE staleBusyClearedDuringAnalysis=True finalFiles=9000
```

**Исправление:** только текущий экземпляр refresh, определяемый локальным CTS/поколением, вправе сбрасывать `IsBusy` и публиковать ошибку в `Summary`. Проверку актуальности применить также в `catch`.

### P2. Каталог затирает сообщения о сетевой ошибке состоянием «пусто»

**Места:** `src/StalkerModLauncher/ViewModels/ModCatalogViewModel.cs:156-169,208-222,298-314`.

- `catch` задаёт `Catalog_Unavailable` или `Catalog_PageFailed`.
- Следующий `finally` вызывает `UpdateEmptyStatus()`, который не различает ошибку и нормальный пустой результат.
- При сбое первой страницы пользователь видит отсутствие модов вместо ошибки соединения. При сбое следующей страницы и наличии ранее загруженных элементов ошибка стирается полностью; `HasMorePages` уже сброшен, поэтому автоматическая догрузка прекращается.

**Проверка:** вызовы реального `ModCatalogViewModel` с ответом HTTP 503 на первую страницу и отдельно на вторую после успешной первой.

```text
CATALOG_INITIAL_503
expected='AP-PRO is unavailable or not responding. Check your internet connection and refresh the catalog.'
actual='No mods have been found in this section yet.'
isLoading=False

CATALOG_PAGE2_503
items=1 hasMore=False
expected='Could not load the AP-PRO page. Try refreshing the catalog.'
actual=''
```

**Исправление:** сохранять состояние ошибки отдельно от вычисляемого состояния пустого списка либо вызывать `UpdateEmptyStatus()` только при успешном завершении. Новый запрос должен явно очищать прежнюю ошибку.

## Оптимизация

### P3. Анализ конфликтов многократно материализует одинаковые связи провайдеров

**Места:** `src/StalkerModLauncher/Services/ModConflictAnalyzer.cs:116-145`; главный список тоже получает подробный результат в `src/StalkerModLauncher/ViewModels/MainViewModel.ModAnalysis.cs:50-70`.

Для каждого файла каждого мода создаются массивы нижних/верхних провайдеров, затем отдельные массивы их ID и имён. После этого те же связи повторно проходят через `SelectMany`, `Distinct` и `ToArray` для агрегатов. При `M` модах с одинаковыми `F` относительными путями объём хранимых ссылок растёт как `O(F × M²)`, хотя исходный набор файлов растёт как `O(F × M)`.

**Замер текущего кода:** Release; отдельные каталоги модов; по 500 небольших `.ltx` с одинаковыми относительными именами во всех модах. Один прогрев, затем медиана 5 запусков с прогретым файловым кешем анализатора. Время создания фикстуры не включено. Аллокации — `GC.GetTotalAllocatedBytes(true)` вокруг вызова, не retained heap и не working set.

| Модов | Файлов на мод | Медиана, мс | Аллоцировано за анализ, MiB |
| --- | --- | --- | --- |
| 20 | 500 | 53,14 | 15,24 |
| 40 | 500 | 89,53 | 42,30 |

Это стресс-фикстура со 100% пересечением, а не замер пользовательского modpack. Она подтверждает затраты на материализацию отношений, но не доказывает заметный фриз реальной игры/интерфейса. Расчёт уже вынесен в `Task.Run`; не предлагается переносить его с UI повторно.

**Рекомендация:** хранить провайдеров один раз на относительный путь; главному списку вычислять агрегаты без полной материализации всех подробностей; подробности выбранного мода формировать по запросу. Перед изменением публичного контракта проверить потребителей и закрепить правила приоритета/excluded files. Ускорение после исправления не заявляется: оптимизация в рамках ревью не выполнялась.

## Качество кода и тестов

### P3. Часть UI-тестов фиксирует текст XAML вместо пользовательского поведения

**Место:** `tests/StalkerModLauncher.Tests/LauncherShellUiTests.cs:24-65`.

Проверки вида `Assert.Contains("Width=\"20\" Height=\"20\"", settings.ToString())`, буквального `FontSize`, имён команд и отступов зависят от представления исходника. Перестановка атрибутов может сломать такой тест без изменения UI; само присутствие имени команды в XAML не доказывает корректный `DataContext`, доступность действия или его выполнение.

**Рекомендация:** удалить проверки текстовой формы/случайных стилевых значений; сохранять поведенческие проверки реального WPF-контрола и привязок, а геометрию проверять лишь там, где она защищает от обрезания или недоступности элементов. В этом же файле уже есть runtime-проверки интерфейса — новая тестовая инфраструктура не нужна.

Общее замечание не распространяется на все 814 тестов: проверка контрактов ресурсов, безопасности путей, восстановления настроек и runtime-сценариев имеет другую цель.

## Дополнительный риск, подтверждённый трассировкой кода

### P2. Проверка занятости перед обновлением устаревает за время скачивания

**Места:** `src/StalkerModLauncher/ViewModels/LauncherSettingsViewModel.cs:353-372`; `src/StalkerModLauncher/ViewModels/MainViewModel.LauncherSettings.cs:40-41`; `src/StalkerModLauncher/Services/LauncherReleaseDownloadService.cs:55-56`.

`_canInstallUpdate()` проверяется только перед сетевым ожиданием. Общая блокировка запуска игры не устанавливается. Tray продолжает вызывать `MainViewModel.CanLaunchProfile`/`LaunchProfileAsync` (`Views/TrayProfilePanel.xaml.cs:169-182`, `MainViewModel.Launch.cs:14-29`), где признак установки обновления отсутствует. После скачивания сервис уже запускает updater, затем view-model вызывает `_requestExit`.

`App.ExitLauncherAsync` (`App.xaml.cs:304-321`) и `MainViewModel.CleanupAsync` (`MainViewModel.Persistence.cs:681-692`) не ожидают окончания игровой сессии. [INFERENCE] Если во время медленного скачивания запустить standalone-профиль из tray, обновление закроет лаунчер при работающей игре; после перезапуска потеряется отслеживание этой сессии и её времени, станет возможен повторный запуск профиля. Основание: `GameSessionTracker.Dispose` лишь очищает Discord-состояние (`:90-96`), а время записывается после завершения сессии в старом процессе (`MainViewModel.Launch.cs:127-153`).

**Проверка:** статическая трассировка текущих обработчиков и потребителей. Реальное самообновление одновременно с игрой не воспроизводилось; этот пункт не приравнивается к подтверждённым runtime-дефектам выше.

**Исправление:** общая резервация update-busy на время операции либо разделение скачивания и установки с атомарной перепроверкой перед `PrepareAndLaunch`. Одна проверка после `await _downloadReleasePackage(...)` запоздает: updater к этому моменту уже запущен.

## Сильные стороны текущей реализации

- Анализаторы и форматирование включены в проект и CI, warnings-as-errors действительно проходят.
- Полный набор тестов проходит; отдельно выполняется настоящий native USVFS smoke для обеих архитектур.
- Проверки упаковки покрывают сохранение `Data`, изоляцию runtime и происхождение native-компонентов.
- В загрузчике обновлений проверяются ожидаемый GitHub release URL и SHA-256 release asset; утверждение о произвольном источнике обновления не подтверждено.

## Границы и порядок исправлений

1. В первую очередь закрыть удаление исходников через junction; добавить регрессию на ссылку в родителе workspace и неизменность исходного файла/отсутствие новых меток.
2. Исправить runtime-семантику исключений USVFS и запуск с виртуальной рабочей папкой; проверить отсутствие provider, существующий fallback и затронутые архитектуры.
3. Исправить копирование актуальных bootstrap-настроек; проверить восстановление после незавершённого capture и сохранение конфликтных версий.
4. Исправить таймаут потокового чтения, владение состоянием refresh и отображение ошибок каталога; закрепить соответствующие воспроизведения регрессиями.
5. Проверить и закрыть пересечение обновления с запуском игры, не запуская updater до финальной резервации состояния.
6. Затем оптимизировать анализатор по замерам реального большого профиля; отдельно почистить хрупкие текстовые UI-проверки.

Это выборочный углублённый аудит основных подсистем, не доказательство отсутствия всех дефектов. Визуальный ручной обход всех трёх оболочек, запуск настоящих игр/модпаков, реальная установка обновления поверх пользовательской установки и полное native-ревью внешнего USVFS не выполнялись. Native smoke и packaging fixtures не равны этим сценариям. LSP для проекта не настроен; использованы чтение кода, анализаторы, тесты и изолированные воспроизведения.
