#include <windows.h>

#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <string_view>
#include <vector>

#ifdef USVFS_SMOKE_DLLS
extern "C" __declspec(dllimport) int modValue();
#endif

namespace
{
std::wstring quote(const std::wstring& value)
{
  return L"\"" + value + L"\"";
}
}

int wmain(int argc, wchar_t* argv[])
{
#ifdef USVFS_SMOKE_DLLS
  if (modValue() != 97) {
    return 13;
  }
#endif
  std::wstring executable;
  std::wstring root;
  const wchar_t* result;
  if ((argc == 5 || argc == 6) && std::wstring_view(argv[1]) == L"--relative") {
    if (argc == 6) {
      std::ifstream config(std::filesystem::path(argv[5]), std::ios::binary);
      std::ifstream parentFile(std::filesystem::path(L"..") / L"base-only.txt", std::ios::binary);
      std::string value;
      if (!config || !std::getline(parentFile, value) || value != "base-only") {
        return 14;
      }
      std::ofstream output("launch-test-output.txt", std::ios::binary);
      if (!(output << "profile-only")) {
        return 15;
      }
    }
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
  if (argc == 6) {
    commandLine += L" " + quote(executable);
  }
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
