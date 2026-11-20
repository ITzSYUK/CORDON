#include <windows.h>

#include <filesystem>
#include <iostream>
#include <string>
#include <string_view>
#include <vector>

namespace
{
std::wstring quote(const std::wstring& value)
{
  return L"\"" + value + L"\"";
}
}

int wmain(int argc, wchar_t* argv[])
{
  std::wstring executable;
  std::wstring root;
  const wchar_t* result;
  if (argc == 5 && std::wstring_view(argv[1]) == L"--relative") {
    std::wstring module(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
    module.resize(length);
    if (CompareStringOrdinal(module.c_str(), -1, argv[2], -1, TRUE) != CSTR_EQUAL) {
      std::wcerr << L"Expected virtual module path " << argv[2] << L", got " << module << L'\n';
      return 12;
    }
    const auto directory = std::filesystem::path(module).parent_path();
    executable = (directory / argv[3]).lexically_normal().wstring();
    root = directory.parent_path().wstring();
    result = argv[4];
  } else if (argc == 4) {
    executable = argv[1];
    root = argv[2];
    result = argv[3];
  } else {
    return 10;
  }

  std::wstring commandLine = quote(executable) + L" " + quote(root) + L" " + quote(result);
  std::vector<wchar_t> buffer(commandLine.begin(), commandLine.end());
  buffer.push_back(L'\0');

  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION process{};
  if (!CreateProcessW(
          executable.c_str(), buffer.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr,
          &startup, &process)) {
    return 11;
  }

  CloseHandle(process.hThread);
  CloseHandle(process.hProcess);
  return 0;
}
