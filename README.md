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
  <a href="#screenshots">Скриншоты</a>
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
- Отдельные сохранения, настройки, логи и скриншоты для каждого обычного профиля.
- Включение и отключение модов, изменение приоритета одиночным и групповым перетаскиванием; моды ниже в списке имеют больший приоритет.
- Сканирование папки с модами и установка распакованных модов из ZIP, 7Z и RAR.
- Анализ конфликтов: победившие, заменённые и уникальные файлы, итоговое дерево сборки и исключение отдельного файла без изменения исходного мода.
- Импорт готовых игровых профилей из `Mod Organizer 2`.
- Автоматический поиск итогового EXE с возможностью выбрать файл вручную; учитываются движки из включённых модов.
- Два режима обычного профиля: Workspace с NTFS-ссылками и USVFS от Mod Organizer 2 для виртуального наложения файлов.
- Проверка готовности профиля, состояния Workspace/USVFS, последнего игрового лога и crash dump; очистка кэша и копирование диагностического отчёта.
- Импорт, экспорт, копирование и переименование профилей.
- Классический интерфейс и альтернативный интерфейс в стиле КПК S.T.A.L.K.E.R.
- Встроенный браузер модификаций из каталога AP-PRO и просмотрщик скриншотов.
- Быстрый запуск профилей из системного трея, запуск лаунчера вместе с Windows и сворачивание в трей.
- Discord Rich Presence, журнал лаунчера с ротацией и автоматическая проверка обновлений с системными уведомлениями.

## Режимы запуска

| Режим | Статус | Когда использовать |
| --- | --- | --- |
| **Workspace** | Стабильный | Рекомендуемый режим. Собирает изолированный профиль с помощью NTFS-ссылок, не копируя игру целиком. |
| **USVFS** | Стабильный | Виртуально объединяет файлы через компоненты Mod Organizer 2. Поддерживает x64 и x86; совместимость зависит от движка и способа запуска. |
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
| `CORDON-...-win-x64-standalone.zip` | Рекомендуется большинству пользователей. Уже содержит .NET Runtime. |
| `CORDON-...-win-x64.zip` | Компактная версия для системы с установленным .NET 8 Desktop Runtime x64. |

Требуется Windows 10/11 x64. Для USVFS может потребоваться Microsoft Visual C++ 2015–2022 Redistributable x64 и x86.

## Безопасность и прозрачность

- Исходный код открыт и распространяется по лицензии GPLv3.
- Исходные папки игры и модов используются только для чтения.
- Записываемые данные профилей хранятся отдельно в Workspace.

<a id="screenshots"></a>

## Интерфейс

| Classic UI | PDA UI | PDA UI 2 |
| --- | --- | --- |
| [![Главное окно](docs/assets/screenshots/ClassicUI_main_window.png)](docs/assets/screenshots/ClassicUI_main_window.png) | [![Главное окно](docs/assets/screenshots/PDAUI_main_window.png)](docs/assets/screenshots/PDAUI_main_window.png) | [![Главное окно](docs/assets/screenshots/PDAUI2_main_window.png)](docs/assets/screenshots/PDAUI2_main_window.png) |
| [![Браузер модификаций](docs/assets/screenshots/ClassicUI_APPRO_browser.png)](docs/assets/screenshots/ClassicUI_APPRO_browser.png) | [![Браузер модификаций](docs/assets/screenshots/PDAUI_APPRO_browser.png)](docs/assets/screenshots/PDAUI_APPRO_browser.png) | [![Браузер модификаций](docs/assets/screenshots/PDAUI2_APPRO_browser.png)](docs/assets/screenshots/PDAUI2_APPRO_browser.png) |
| [![Скриншоты](docs/assets/screenshots/ClassicUI_screens_window.png)](docs/assets/screenshots/ClassicUI_screens_window.png) | [![Скриншоты](docs/assets/screenshots/PDAUI_screens_window.png)](docs/assets/screenshots/PDAUI_screens_window.png) | [![Скриншоты](docs/assets/screenshots/PDAUI2_screens_window.png)](docs/assets/screenshots/PDAUI2_screens_window.png) |
| [![Состояние](docs/assets/screenshots/ClassicUI_profile_status.png)](docs/assets/screenshots/ClassicUI_profile_status.png) | [![Состояние](docs/assets/screenshots/PDAUI_profile_status.png)](docs/assets/screenshots/PDAUI_profile_status.png) | [![Состояние](docs/assets/screenshots/PDAUI2_profile_status.png)](docs/assets/screenshots/PDAUI2_profile_status.png) |
| [![Полный экран](docs/assets/screenshots/ClassicUI_full_screen_window.png)](docs/assets/screenshots/ClassicUI_full_screen_window.png) | [![Профиль](docs/assets/screenshots/PDAUI_profile_window.png)](docs/assets/screenshots/PDAUI_profile_window.png) | [![Профиль](docs/assets/screenshots/PDAUI2_profile_window.png)](docs/assets/screenshots/PDAUI2_profile_window.png) |
| [![Сканирование модов](docs/assets/screenshots/ClassicUI_mods_scan.png)](docs/assets/screenshots/ClassicUI_mods_scan.png) | [![Настройки](docs/assets/screenshots/PDAUI_settings_window.png)](docs/assets/screenshots/PDAUI_settings_window.png) | [![Настройки](docs/assets/screenshots/PDAUI2_settings_window.png)](docs/assets/screenshots/PDAUI2_settings_window.png) |

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
- [Лицензии сторонних компонентов](THIRD_PARTY_NOTICES.md)

---

<a id="english"></a>

## English

**Run multiple S.T.A.L.K.E.R. setups from one game installation — with isolated mods, saves and settings.**

CORDON is an open-source Windows profile launcher for the original trilogy, Anomaly, OGSR, iX-Ray, other X-Ray-based projects and standalone mod builds. It keeps profiles separate without modifying the original game or mod directories.

### Why use it

- Create profiles with different mod lists, patches and engine builds.
- Keep saves, settings, logs and screenshots isolated per profile.
- Reorder mods and inspect file conflicts.
- Import Mod Organizer 2 `modlist.txt` state.
- Detect launch executables automatically or select one manually.
- Use the stable linked Workspace backend without copying the entire game.
- Use the stable USVFS backend for supported x64 and x86 games.
- Switch between Classic UI and a S.T.A.L.K.E.R.-inspired PDA interface.

### Quick start

1. Download and extract the [latest release](https://github.com/ITzSYUK/CORDON/releases/latest).
2. Click **Create** and choose a regular or standalone profile.
3. Select the base game and mod folders, or one ready-to-play standalone folder.
4. Check the detected executable and mod order.
5. Choose either stable backend—Workspace or USVFS—then click **Launch**.

### Release packages

| Package | Description |
| --- | --- |
| `CORDON-...-win-x64-standalone.zip` | Recommended for most users. Includes the .NET Runtime. |
| `CORDON-...-win-x64.zip` | Smaller package. Requires .NET 8 Desktop Runtime x64. |

Windows 10/11 x64 is required. USVFS may also require the Microsoft Visual C++ 2015–2022 Redistributable for both x64 and x86.

See the [English user guide](docs/USER_GUIDE_EN.md) for detailed usage and the [English technical documentation](docs/TECHNICAL_EN.md) for architecture, Workspace safety, USVFS limitations and release packaging.

---

## License

The launcher source code is licensed under the [GNU GPLv3](LICENSE.md). Third-party components and assets retain their original licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Это неофициальный фанатский инструмент, не связанный с GSC Game World и не одобренный компанией. S.T.A.L.K.E.R. и связанные товарные знаки принадлежат их правообладателям.

This is an unofficial fan-made tool and is not affiliated with or endorsed by GSC Game World. S.T.A.L.K.E.R. and related trademarks belong to their respective owners.
