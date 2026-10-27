# NtQueryDirectoryFile batch boundary investigation

Date: October 27, 2026. Windows 11 Pro 10.0.26300, NTFS, MSVC 19.44,
USVFS PR #99: `93fcf7af42523d8da8d1436e88920f900e766596`.

## Reproduced behavior

The original strict comparison was restored in an isolated diagnostic copy of
`NtQueryDirectoryFileListsEntriesOfSeveralSourcesLikeNtdll`.
For `FileDirectoryInformation`, a 312-byte buffer and `usvfs_entry_XX.txt` filenames,
the first Windows call returned 204 bytes (two records), while USVFS returned
308 bytes (three records). The complete ordered listings matched. With a large
buffer or single-entry queries, individual calls matched as well.

The control test below uses direct `CreateFileW`, `NtQueryDirectoryFile` and
`CloseHandle` calls, without creating a VFS context or calling hooked enumeration.
It reproduced the more conservative native batch boundary:

| Class | End of third record, bytes | Minimum native buffer for three records | Bytes returned |
|---|---:|---:|---:|
| FileDirectoryInformation (1) | 308 | 340 | 308 |
| FileFullDirectoryInformation (2) | 312 | 344 | 312 |
| FileBothDirectoryInformation (3) | 402 | 418 | 402 |
| FileNamesInformation (12) | 144 | 144 | 144 |

The first and second records are aligned to 8 bytes; no trailing alignment is
required after the last record. A separate control removed 8.3 aliases only from
fixture files and obtained the same thresholds. For the other eight supported
classes, the native threshold matched the end of the third record.

The immediate reason for the mismatch is that USVFS fills the buffer according to
the actual record sizes, whereas the native path on this system requires extra
buffer space for three older classes. Identical per-call batching cannot be
assumed for this fixture without explaining that additional native requirement.
It has not been established whether NTFS or a filesystem filter introduces it.
This is an observation on one computer, not proof of a general Windows rule.

## Test correction

`scripts/patches/usvfs-pr99-tests.patch` retains the complete-list comparison,
while each virtual call now additionally checks:

- that returned names match the next names in the reference sequence;
- exact offsets between adjacent records, aligned to 8 bytes;
- that the next expected record cannot fit in the remaining space, unless
  single-entry mode was requested or enumeration has finished.

These checks also apply when the information class changes, including retries
with a larger buffer, and to the early source-scan termination regression.
Existing status, buffer-boundary, canary, single-entry and complete-list checks
are retained. Runtime code was not changed.

Test sensitivity was verified by forcing `ReturnSingleEntry=true` only for USVFS
calls in the diagnostic copy while retaining the original test expectations.
The strengthened check rejected that behavior with
`virtual query left room for the next matching entry`. The mutation was removed.

## Minimal native-threshold reproducer

Append the test below to `test/tvfs_test/directory_enumeration.cpp` in an isolated
copy of the pinned USVFS revision with this project's test patch applied.
It uses existing helpers from that translation unit; no runtime changes are
required. Build `tvfs_test` in Release and run from an ASCII-only directory:

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

## Final validation

- Release builds of USVFS x64/x86 using `Build-UsvfsDependencies.ps1`.
- Complete native suites: 215/215 x64 and 215/215 x86, with no skipped tests.
- `Test-UsvfsRuntime.ps1`: five x64 and five x86 iterations; writable-directory,
  appdata and AnomalyLauncher settings persistence checks also passed.
- Patch reproducibility, source provenance and runtime hashes were verified;
  the standard build script updated the local `runtime/build-info.json`.
- `git diff --check` passes for the changed patch. At the time of investigation,
  the full working-tree check reported unrelated blank lines/whitespace in
  `WorkspaceSourceScanner.cs` and `ProfileHealthViewModel.cs`; those files were
  not changed as part of this investigation.

Publishing, archives, UI and game builds were not checked: this change concerns tests.
