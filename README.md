<h1 align="center">CORDON</h1>

<p align="center"><strong>S.T.A.L.K.E.R. Mod Launcher</strong></p>

<p align="center">
  <img src="docs/assets/screenshots/PDAUI_main_window.png" alt="Главное окно CORDON" width="900">
</p>

<p align="center">
  <strong>Все ваши сборки S.T.A.L.K.E.R. в одном лаунчере.</strong>
</p>

<p align="center">
  <a href="https://github.com/ITzSYUK/CORDON/releases/latest"><strong>Скачать последнюю версию</strong></a>
  ·
  <a href="docs/USER_GUIDE_RU.md">Руководство пользователя</a>
  ·
  <a href="#screenshots">Screenshots</a>
  ·
  <a href="#english">English</a>
</p>

<p align="center">
  <a href="https://github.com/ITzSYUK/CORDON/releases/latest"><img src="https://img.shields.io/github/v/release/ITzSYUK/CORDON?display_name=tag&label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-1773cf" alt="Windows 10 and 11">
  <a href="LICENSE.md"><img src="https://img.shields.io/github/license/ITzSYUK/CORDON" alt="GPLv3 license"></a>
</p>

---

<a id="русский"></a>

## Зачем нужен этот лаунчер

CORDON помогает держать несколько модификаций и наборов аддонов рядом, не переустанавливая игру и не смешивая их файлы.

- Множество сборок: создавайте профили с разными модами, патчами и движками.
- Раздельные данные: у каждого профиля свои сохранения, настройки, логи и скриншоты.
- Без полной копии игры: Workspace подключает неизменяемые файлы NTFS-ссылками.

Подходит для классической трилогии, Anomaly, OGSR, iX-Ray и других проектов с типичной структурой X-Ray, а также для готовых автономных сборок.

## Главное

- Поддержка 32- и 64-битных движков X-Ray: классической трилогии, Anomaly, OGSR, iX-Ray и других типичных сборок.
- Обычные профили с базовой игрой и упорядоченным списком модов, а также автономные профили для готовых сборок со своим EXE.
- Отдельные сохранения, настройки, логи и скриншоты для каждого обычного профиля по умолчанию; общий каталог данных базовой игры можно выбрать в настройках профиля.
- Включение и отключение модов, изменение приоритета одиночным и групповым перетаскиванием; моды ниже в списке имеют больший приоритет.
- Сканирование папки с модами и установка из ZIP, 7Z, RAR и `.fomod`.
- Мастер FOMOD с выбором компонентов и картинками: отдельное окно в Classic UI, встроенная страница в обоих PDA UI. Поддерживаются архивы и уже распакованные пакеты.
- Анализ конфликтов: победившие, заменённые и уникальные файлы, итоговое дерево сборки и исключение отдельного файла без изменения исходного мода.
- Импорт готовых игровых профилей из `Mod Organizer 2`.
- Автоматический поиск итогового EXE с возможностью выбрать файл вручную; учитываются движки из включённых модов.
- Два режима обычного профиля: Workspace с NTFS-ссылками и USVFS от Mod Organizer 2 для виртуального наложения файлов.
- Проверка готовности профиля, состояния Workspace/USVFS, последнего игрового лога и crash dump; очистка кэша и копирование диагностического отчёта.
- Импорт, экспорт, копирование и переименование профилей.
- Классический интерфейс и две темы интерфейса в стиле КПК S.T.A.L.K.E.R., русский и английский языки.
- Встроенный браузер модификаций из каталога AP-PRO и просмотрщик скриншотов.
- Быстрый запуск профилей из системного трея, запуск лаунчера вместе с Windows и сворачивание в трей.
- Discord Rich Presence, журнал лаунчера с ротацией, проверка обновлений с системными уведомлениями и установка обновления из настроек.

## Режимы запуска

| Режим | Статус | Когда использовать |
| --- | --- | --- |
| **Workspace** | Стабильный | Собирает изолированный профиль с помощью NTFS-ссылок, не копируя игру целиком. Подходит для сборок, несовместимых с USVFS. |
| **USVFS** | Стабильный | Режим по умолчанию для новых обычных профилей. Виртуально объединяет файлы через компоненты Mod Organizer 2; поддерживает x64 и x86. |
| **Автономный профиль** | Стабильный | Запускает уже готовую самостоятельную сборку из её собственной папки. |

## Быстрый старт

1. Скачайте и распакуйте последний релиз.
2. Нажмите **Создать** и выберите обычный или автономный профиль.
3. Укажите базовую игру и папки модов либо папку готовой сборки.
4. Проверьте найденный EXE и порядок модов.
5. Нажмите **Запустить**.

Подробная настройка описана в [руководстве пользователя](docs/USER_GUIDE_RU.md).

## Скачать

Загрузки находятся на странице [последнего релиза](https://github.com/ITzSYUK/CORDON/releases/latest).

| Архив | Для кого |
| --- | --- |
| `CORDON-...-win-x64-standalone.zip` | Стабильный USVFS, содержит .NET Runtime. `CORDON-Standalone.exe` хранит настройки лаунчера рядом с EXE, в `Data\StalkerModLauncher`. |
| `CORDON-...-win-x64.zip` | Стабильный USVFS, компактная версия для системы с установленным .NET 8 Desktop Runtime x64; настройки лаунчера находятся в AppData. |
| `CORDON-...-win-x64-standalone-usvfs-experimental.zip` | Экспериментальный оптимизированный USVFS и встроенный .NET; настройки хранятся рядом с EXE. |
| `CORDON-...-win-x64-usvfs-experimental.zip` | Экспериментальный оптимизированный USVFS; нужен .NET 8 Desktop Runtime x64, настройки хранятся в AppData. |

Экспериментальные архивы могут прилагаться к релизу для проверки совместимости с игровыми сборками; они присутствуют не в каждом релизе. Для обычного использования рекомендуется стабильный пакет. Оптимизированный USVFS выбирается архивом, режим профиля остаётся **USVFS**. Встроенное обновление скачивает стабильный пакет и заменит экспериментальный runtime; для его сохранения обновляйте лаунчер вручную из соответствующего экспериментального архива.

Требуется Windows 10/11 x64. Для USVFS может потребоваться Microsoft Visual C++ 2015–2022 Redistributable x64 и x86.

## Безопасность и прозрачность

- Исходный код открыт и распространяется по лицензии GPLv3.
- Исходные файлы игры и модов используются только для чтения.
- Игровые данные обычного профиля по умолчанию хранятся отдельно в Workspace. Явный выбор общего каталога данных разрешает игре запись в этот каталог; автономная сборка использует собственное расположение данных.

<a id="screenshots"></a>

## Интерфейс

| Classic UI | PDA UI | PDA UI 2 |
| --- | --- | --- |
| [![Главное окно](docs/assets/screenshots/ClassicUI_main_window.png)](docs/assets/screenshots/ClassicUI_main_window.png) | [![Главное окно](docs/assets/screenshots/PDAUI_main_window.png)](docs/assets/screenshots/PDAUI_main_window.png) | [![Главное окно](docs/assets/screenshots/PDAUI2_main_window.png)](docs/assets/screenshots/PDAUI2_main_window.png) |
| [![Полный экран](docs/assets/screenshots/ClassicUI_full_screen_window.png)](docs/assets/screenshots/ClassicUI_full_screen_window.png) | [![Профиль](docs/assets/screenshots/PDAUI_profile_window.png)](docs/assets/screenshots/PDAUI_profile_window.png) | [![Профиль](docs/assets/screenshots/PDAUI2_profile_window.png)](docs/assets/screenshots/PDAUI2_profile_window.png) |
| [![Состояние](docs/assets/screenshots/ClassicUI_profile_status.png)](docs/assets/screenshots/ClassicUI_profile_status.png) | [![Состояние](docs/assets/screenshots/PDAUI_profile_status.png)](docs/assets/screenshots/PDAUI_profile_status.png) | [![Состояние](docs/assets/screenshots/PDAUI2_profile_status.png)](docs/assets/screenshots/PDAUI2_profile_status.png) |
| [![Скриншоты](docs/assets/screenshots/ClassicUI_screens_window.png)](docs/assets/screenshots/ClassicUI_screens_window.png) | [![Скриншоты](docs/assets/screenshots/PDAUI_screens_window.png)](docs/assets/screenshots/PDAUI_screens_window.png) | [![Скриншоты](docs/assets/screenshots/PDAUI2_screens_window.png)](docs/assets/screenshots/PDAUI2_screens_window.png) |
| [![Сканирование модов](docs/assets/screenshots/ClassicUI_mods_scan.png)](docs/assets/screenshots/ClassicUI_mods_scan.png) | [![Сканирование модов](docs/assets/screenshots/PDAUI_mods_scan.png)](docs/assets/screenshots/PDAUI_mods_scan.png) | [![Сканирование модов](docs/assets/screenshots/PDAUI2_mods_scan.png)](docs/assets/screenshots/PDAUI2_mods_scan.png) |
| [![Импорт MO2](docs/assets/screenshots/ClassicUI_MO2_import.png)](docs/assets/screenshots/ClassicUI_MO2_import.png) | [![Браузер модификаций](docs/assets/screenshots/PDAUI_APPRO_browser.png)](docs/assets/screenshots/PDAUI_APPRO_browser.png) | [![Браузер модификаций](docs/assets/screenshots/PDAUI2_APPRO_browser.png)](docs/assets/screenshots/PDAUI2_APPRO_browser.png) |
| [![Конфликты модов](docs/assets/screenshots/ClassicUI_mod_conflicts.png)](docs/assets/screenshots/ClassicUI_mod_conflicts.png) | [![Настройки](docs/assets/screenshots/PDAUI_settings_window.png)](docs/assets/screenshots/PDAUI_settings_window.png) | [![Настройки](docs/assets/screenshots/PDAUI2_settings_window.png)](docs/assets/screenshots/PDAUI2_settings_window.png) |

## Для разработчиков

Требуются .NET 8 SDK и Windows 10/11 x64.

```powershell
dotnet build .\StalkerModLauncher.sln
dotnet test .\StalkerModLauncher.sln -c Release
dotnet run --project .\src\StalkerModLauncher\StalkerModLauncher.csproj
```

- [Техническая документация на русском](docs/TECHNICAL_RU.md)
- [Technical documentation in English](docs/TECHNICAL_EN.md)
- [User guide in English](docs/USER_GUIDE_EN.md)
- [Перенос сборки из MO2](docs/MO2_IMPORT_GUIDE_RU.md)
- [Лицензии сторонних компонентов](THIRD_PARTY_NOTICES.md)

---

<a id="english"></a>

## English

### Why use it

CORDON helps you keep multiple S.T.A.L.K.E.R. modifications and addon sets together without reinstalling the game or mixing their files.

- Multiple setups: create profiles with different mods, patches and engines.
- Separate data: every profile has its own saves, settings, logs and screenshots.
- No full game copy: Workspace connects unchanged files through NTFS links.

It supports the original trilogy, Anomaly, OGSR, iX-Ray and other X-Ray projects with a typical structure, as well as ready-to-play standalone builds.

### Highlights

- Support for 32-bit and 64-bit X-Ray engines: the original trilogy, Anomaly, OGSR, iX-Ray and other typical builds.
- Regular profiles with a base game and ordered mod list, plus standalone profiles for builds with their own EXE.
- Separate saves, settings, logs and screenshots for every regular profile by default; the base game's shared data directory can be selected in profile settings.
- Enable and disable mods, and change priority with single-item or group drag-and-drop; lower mods in the list have higher priority.
- Scan mod folders and install from ZIP, 7Z, RAR and `.fomod` archives.
- FOMOD component selection and image previews: a separate window in Classic UI and an embedded page in both PDA themes. Archives and unpacked packages are supported.
- Conflict analysis: winning, replaced and unique files, the resulting build tree, and excluding one file without changing the source mod.
- Import ready-made game profiles from `Mod Organizer 2`.
- Automatically find the final EXE or choose it manually; engines from enabled mods are included.
- Two regular profile modes: Workspace with NTFS links and USVFS from Mod Organizer 2 for virtual file overlays.
- Profile readiness checks, Workspace/USVFS status, the latest game log and crash dump; cache cleanup and diagnostic report copying.
- Import, export, duplicate and rename profiles.
- Classic UI and two alternative S.T.A.L.K.E.R.-inspired PDA UI themes, with Russian and English languages.
- Built-in AP-PRO mod browser and screenshot viewer.
- Quick profile launch from the system tray, launch with Windows and minimize to the tray.
- Discord Rich Presence, a rotating launcher log, update checks with system notifications and update installation from settings.

### Launch modes

| Mode | Status | When to use |
| --- | --- | --- |
| **Workspace** | Stable | Builds an isolated profile with NTFS links without copying the entire game. Useful for builds incompatible with USVFS. |
| **USVFS** | Stable | The default for new regular profiles. Virtually combines files through Mod Organizer 2 components; supports x64 and x86. |
| **Standalone profile** | Stable | Starts an already assembled standalone build from its own folder. |

### Quick start

1. Download and extract the [latest release](https://github.com/ITzSYUK/CORDON/releases/latest).
2. Click **Create** and choose a regular or standalone profile.
3. Select the base game and mod folders, or the folder of a ready-made build.
4. Check the detected EXE and mod order.
5. Click **Launch**.

Detailed setup is described in the [English user guide](docs/USER_GUIDE_EN.md).

### Download

Downloads are available on the [latest release](https://github.com/ITzSYUK/CORDON/releases/latest) page.

| Package | For whom |
| --- | --- |
| `CORDON-...-win-x64-standalone.zip` | Stable USVFS, includes the .NET Runtime. `CORDON-Standalone.exe` stores launcher settings beside the EXE in `Data\StalkerModLauncher`. |
| `CORDON-...-win-x64.zip` | Stable USVFS, compact version for systems with .NET 8 Desktop Runtime x64 installed; launcher settings are stored in AppData. |
| `CORDON-...-win-x64-standalone-usvfs-experimental.zip` | Experimental optimized USVFS with .NET included; settings are stored beside the EXE. |
| `CORDON-...-win-x64-usvfs-experimental.zip` | Experimental optimized USVFS; requires .NET 8 Desktop Runtime x64, settings are stored in AppData. |

Experimental archives may accompany a release for compatibility testing with game builds; they are not included in every release. The stable package is recommended for regular use. The archive selects the optimized USVFS runtime; the profile mode remains **USVFS**. The built-in updater downloads the stable package and replaces the experimental runtime; update manually from the matching experimental archive to retain it.

Windows 10/11 x64 is required. USVFS may also require Microsoft Visual C++ 2015–2022 Redistributable for x64 and x86.

### Security and transparency

- The source code is open and distributed under GPLv3.
- Original game and mod files are read-only from the launcher's perspective.
- Regular profiles store game data separately in the Workspace by default. Explicitly selecting the shared data directory permits the game to write there; standalone builds use their own data location.

### Interface

The shared [interface gallery](#screenshots) above shows Classic UI, PDA UI and PDA UI 2.

### For developers

.NET 8 SDK and Windows 10/11 x64 are required.

```powershell
dotnet build .\StalkerModLauncher.sln
dotnet test .\StalkerModLauncher.sln -c Release
dotnet run --project .\src\StalkerModLauncher\StalkerModLauncher.csproj
```

- [Technical documentation in Russian](docs/TECHNICAL_RU.md)
- [Technical documentation in English](docs/TECHNICAL_EN.md)
- [User guide in English](docs/USER_GUIDE_EN.md)
- [Importing a build from MO2](docs/MO2_IMPORT_GUIDE_EN.md)
- [Third-party licenses](THIRD_PARTY_NOTICES.md)

---

## License

The launcher source code is licensed under the [GNU GPLv3](LICENSE.md). Third-party components and assets retain their original licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

This is an unofficial fan-made tool and is not affiliated with or endorsed by GSC Game World. S.T.A.L.K.E.R. and related trademarks belong to their respective owners.
