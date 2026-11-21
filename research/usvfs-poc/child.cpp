#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <windows.h>

namespace fs = std::filesystem;

#ifdef USVFS_SMOKE_DLLS
extern "C" __declspec(dllimport) int modValue();
#endif

static std::string readText(const fs::path& path)
{
  std::ifstream input(path, std::ios::binary);
  if (!input) {
    return "<missing>";
  }

  return {std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
}

int wmain(int argc, wchar_t* argv[])
{
  if (argc != 3 && argc != 4) {
    std::wcerr << L"Usage: usvfs_overlay_child <virtual-root> <result-file>\n";
    return 10;
  }

  const fs::path root = argv[1];
  const fs::path outputPath = argv[2];

  if (argc == 4) {
    std::wstring modulePath(32768, L'\0');
    if (GetModuleFileNameW(nullptr, modulePath.data(), static_cast<DWORD>(modulePath.size())) == 0 ||
        CompareStringOrdinal(modulePath.c_str(), -1, argv[3], -1, TRUE) != CSTR_EQUAL) {
      return 13;
    }
  }

  Sleep(500);

  std::ofstream output(outputPath, std::ios::binary);
  if (!output) {
    std::wcerr << L"Failed to open result file: " << outputPath.wstring() << L"\n";
    return 11;
  }

#ifdef USVFS_SMOKE_DLLS
  for (const auto* name : {L"usvfs_smoke_base.dll", L"usvfs_smoke_mod.dll"}) {
    const auto module = GetModuleHandleW(name);
    std::wstring modulePath(32768, L'\0');
    if (module == nullptr || GetModuleFileNameW(module, modulePath.data(), static_cast<DWORD>(modulePath.size())) == 0 ||
        CompareStringOrdinal(modulePath.c_str(), -1, (root / "tools" / name).c_str(), -1, TRUE) != CSTR_EQUAL) {
      output << "invalid virtual DLL path\n";
      return 12;
    }
  }
  output << "loader=" << modValue() << "\n";
#endif
  output << "shared=" << readText(root / "shared.txt") << "\n";
  output << "dynamic=" << readText(root / "dynamic.txt") << "\n";
  output << "base-only=" << readText(root / "base-only.txt") << "\n";
  output << "mod-only=" << readText(root / "mod-only.txt") << "\n";
  output << "nested=" << readText(root / "gamedata" / "config" / "system.ltx") << "\n";
  output << "bootstrap=" << readText(root / "physical-bootstrap.txt") << "\n";
  output << "profile-file=" << readText(root / "fsgame.ltx") << "\n";

  return 0;
}
