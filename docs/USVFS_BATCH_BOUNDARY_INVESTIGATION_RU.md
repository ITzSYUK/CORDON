# Проверка границ порций NtQueryDirectoryFile

Дата: 27 октября 2026 года. Windows 11 Pro 10.0.26300, NTFS, MSVC 19.44,
USVFS PR #99: `93fcf7af42523d8da8d1436e88920f900e766596`.

## Что воспроизведено

Возвращена исходная строгая проверка в отдельной диагностической копии теста
`NtQueryDirectoryFileListsEntriesOfSeveralSourcesLikeNtdll`.
Для `FileDirectoryInformation`, буфера 312 байт и имён `usvfs_entry_XX.txt`
первый вызов Windows вернул 204 байта (две записи), USVFS — 308 (три записи).
Полные упорядоченные списки совпали. Большой буфер и single-entry совпадали
также по отдельным вызовам.

Контрольный тест ниже использует прямые `CreateFileW`, `NtQueryDirectoryFile`
и `CloseHandle`, без создания VFS-контекста и вызовов перехваченного перечисления.
Он воспроизвёл более консервативную границу нативного обхода:

| Класс | Конец третьей записи, байт | Минимальный native-буфер для трёх записей | Возвращено байт |
|---|---:|---:|---:|
| FileDirectoryInformation (1) | 308 | 340 | 308 |
| FileFullDirectoryInformation (2) | 312 | 344 | 312 |
| FileBothDirectoryInformation (3) | 402 | 418 | 402 |
| FileNamesInformation (12) | 144 | 144 | 144 |

Первая и вторая запись выравниваются до 8 байт; после последней записи
завершающее выравнивание не требуется. Отдельный контроль с удалением 8.3-имён
только у файлов фикстуры не изменил пороги. Для остальных восьми поддерживаемых
классов native-порог совпал с концом третьей записи.

Непосредственная причина расхождения: USVFS заполняет буфер по фактическим размерам
записей, а нативный путь на этой системе для трёх старых классов требует
дополнительный запас. Считать одинаковое разбиение на вызовы обязательным
для этой фикстуры нельзя без объяснения дополнительного native-запаса.
Не установлено, какой компонент создаёт запас: NTFS или фильтр файловой системы.
Это наблюдение на одном компьютере, а не доказательство общего правила Windows.

## Исправление проверки

В `scripts/patches/usvfs-pr99-tests.patch` сохранено сравнение полного списка,
но каждый виртуальный вызов теперь дополнительно проверяет:

- соответствие очередных имён эталонной последовательности;
- точные смещения соседних записей с выравниванием до 8 байт;
- невозможность поместить следующую ожидаемую запись в оставшееся место,
  если не запрошен single-entry и обход ещё не завершён.

Проверка действует и при смене класса записей, включая повтор с увеличенным
буфером, и в регрессии раннего завершения сканирования источника.
Существующие проверки статусов, границ буфера, canary, single-entry и полного
списка сохранены. Код runtime не изменён.

Проверена чувствительность: в диагностической копии только вызовы USVFS
принудительно получили `ReturnSingleEntry=true`, при сохранении исходных ожиданий
теста. Усиленная проверка отвергла это поведение с сообщением
`virtual query left room for the next matching entry`. Изменение удалено.

## Минимальное воспроизведение native-порога

Добавить тест ниже в конец `test/tvfs_test/directory_enumeration.cpp`
изолированной копии закреплённого USVFS с патчем тестов проекта. Используются
существующие помощники этой единицы трансляции; runtime менять не требуется.
Собрать `tvfs_test` в Release и запустить из ASCII-каталога:

```powershell
.\test\bin\tvfs_test_x64.exe --gtest_filter=NativeDirectoryBatch.ReportsThreeEntryBoundary
```

```cpp
TEST(NativeDirectoryBatch, ReportsThreeEntryBoundary)
{
  const auto root = test::path_of_test_temp(L"native_batch_boundary_direct");
  std::filesystem::create_directories(root);
  for (int index = 0; index < 14; ++index) {
    std::ofstream(root / std::format(L"usvfs_entry_{:02}.txt", index));
  }
  for (const auto informationClass : {FileDirectoryInformation, FileFullDirectoryInformation,
                                     FileBothDirectoryInformation, FileNamesInformation}) {
    const auto layout = findDirectoryRecordLayout(informationClass).value();
    const ULONG size = layout.fileNameOffset + 36;
    const ULONG end = (size + 7) / 8 * 8 * 2 + size;
    bool found = false;
    for (ULONG length = end; length <= 0x1000; ++length) {
      HANDLE directory = ::CreateFileW(root.c_str(), FILE_LIST_DIRECTORY,
          FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
          FILE_FLAG_BACKUP_SEMANTICS, nullptr);
      ASSERT_NE(INVALID_HANDLE_VALUE, directory);
      const auto result = queryOnce(nativeQueryDirectoryFile(), directory,
          {informationClass, length, false, L"usvfs_entry_*"});
      ::CloseHandle(directory);
      ASSERT_EQ(STATUS_SUCCESS, result.outcome.result);
      if (recordNames(layout, result.data).size() >= 3) {
        std::cout << "class=" << static_cast<int>(informationClass)
                  << " recordEnd=" << end << " nativeBuffer=" << length
                  << " returnedBytes=" << result.data.size() << '\n';
        found = true;
        break;
      }
    }
    ASSERT_TRUE(found);
  }
}
```

## Итоговая проверка

- Release-сборка USVFS x64/x86 штатным `Build-UsvfsDependencies.ps1`.
- Полные нативные наборы: 215/215 x64 и 215/215 x86, без пропусков.
- `Test-UsvfsRuntime.ps1`: пять итераций x64 и пять x86; прошли также проверки
  writable-directory, appdata и сохранения настроек AnomalyLauncher.
- Проверены воспроизводимость патчей, происхождение исходников и хеши runtime;
  штатный сборщик обновил локальный `runtime/build-info.json`.
- `git diff --check` для изменённого патча проходит. Общая проверка рабочего
  дерева на момент расследования отметила посторонние пустые строки/пробелы
  в `WorkspaceSourceScanner.cs` и `ProfileHealthViewModel.cs`; эти файлы
  в рамках расследования не изменялись.

Публикация, архивы, UI и игровые сборки не проверялись: изменение касается тестов.
