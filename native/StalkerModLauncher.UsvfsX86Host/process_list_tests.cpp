#include <windows.h>
#include <shellapi.h>
#include <chrono>
#include <cstdint>
#include <iterator>
#include <usvfs.h>

BOOL WINAPI testProcessList(size_t* count, LPDWORD ids);
BOOL WINAPI testCreateProcessW(LPCWSTR application, LPWSTR commandLine, LPSECURITY_ATTRIBUTES processAttributes,
                              LPSECURITY_ATTRIBUTES threadAttributes, BOOL inheritHandles, DWORD flags,
                              LPVOID environment, LPCWSTR directory, LPSTARTUPINFOW startup, LPPROCESS_INFORMATION process);
BOOL WINAPI testShellExecuteExW(SHELLEXECUTEINFOW* info);
#define usvfsGetVFSProcessList testProcessList
#define CreateProcessW testCreateProcessW
#define ShellExecuteExW testShellExecuteExW
#define wmain unusedHostMain
#include "main.cpp"
#undef wmain
#undef ShellExecuteExW
#undef CreateProcessW

namespace
{
std::vector<DWORD> testIds;
bool failQuery = false;
bool failRead = false;
int elevationRequests = 0;
constexpr wchar_t TestElevationVariable[] = L"CORDON_TEST_ELEVATED_HOST";

void require(bool condition, const char* message)
{
  if (!condition) {
    throw std::runtime_error(message);
  }
}

void verifyVirtualElevation(const fs::path& root, const fs::path& testHost)
{
  const auto configPath = root / L"elevation.bin";
  std::ofstream config(configPath, std::ios::binary);
  const auto writeUInt32 = [&](std::uint32_t value) {
    config.write(reinterpret_cast<const char*>(&value), sizeof(value));
  };
  const auto writeString = [&](const std::u8string& value) {
    writeUInt32(static_cast<std::uint32_t>(value.size()));
    config.write(reinterpret_cast<const char*>(value.data()), static_cast<std::streamsize>(value.size()));
  };
  writeUInt32(VirtualExecutableConfigMagic);
  writeString(root.filename().u8string());
  writeString((root / L"requires-elevation.exe").u8string());
  writeString(u8"--return-73");
  writeString(root.u8string());
  writeString((root / L"usvfs.log").u8string());
  writeUInt32(0);
  config.close();
  require(static_cast<bool>(config), "Failed to write the elevation fixture");

  auto host = testHost.wstring();
  auto path = configPath.wstring();
  wchar_t elevatedArgument[] = L"--elevated";
  wchar_t* retryArgs[] = {host.data(), elevatedArgument, path.data()};
  require(unusedHostMain(3, retryArgs) == 20 && elevationRequests == 0,
          "An already retried start must not request elevation again");
  wchar_t* args[] = {host.data(), path.data()};
  require(unusedHostMain(2, args) == 73 && elevationRequests == 1,
          "A virtual CreateProcess error 740 must retry once and return the target's exit code");
}
}

BOOL WINAPI testProcessList(size_t* count, LPDWORD ids)
{
  if (failQuery || (ids != nullptr && failRead)) {
    return FALSE;
  }
  if (ids != nullptr) {
    std::copy_n(testIds.begin(), (std::min)(*count, testIds.size()), ids);
  }
  *count = testIds.size();
  return TRUE;
}

BOOL WINAPI testCreateProcessW(LPCWSTR application, LPWSTR commandLine, LPSECURITY_ATTRIBUTES processAttributes,
                              LPSECURITY_ATTRIBUTES threadAttributes, BOOL inheritHandles, DWORD flags,
                              LPVOID environment, LPCWSTR directory, LPSTARTUPINFOW startup, LPPROCESS_INFORMATION process)
{
  if (fs::path(application).filename() == L"requires-elevation.exe") {
    if (!GetEnvironmentVariableW(TestElevationVariable, nullptr, 0)) {
      SetLastError(ERROR_ELEVATION_REQUIRED);
      return FALSE;
    }

    wchar_t host[MAX_PATH]{};
    GetModuleFileNameW(nullptr, host, MAX_PATH);
    return CreateProcessW(host, commandLine, processAttributes, threadAttributes, inheritHandles, flags,
                          environment, directory, startup, process);
  }
  return CreateProcessW(application, commandLine, processAttributes, threadAttributes, inheritHandles, flags,
                        environment, directory, startup, process);
}

BOOL WINAPI testShellExecuteExW(SHELLEXECUTEINFOW* info)
{
  ++elevationRequests;
  if (std::wstring_view(info->lpVerb) != L"runas" ||
      !std::wstring_view(info->lpParameters).starts_with(L"--elevated ")) {
    SetLastError(ERROR_INVALID_PARAMETER);
    return FALSE;
  }

  auto commandLine = quote(info->lpFile) + L" " + info->lpParameters;
  SetEnvironmentVariableW(TestElevationVariable, L"1");
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION process{};
  const auto started = CreateProcessW(info->lpFile, commandLine.data(), nullptr, nullptr, FALSE, 0,
                                     nullptr, info->lpDirectory, &startup, &process);
  const auto error = GetLastError();
  SetEnvironmentVariableW(TestElevationVariable, nullptr);
  if (!started) {
    SetLastError(error);
    return FALSE;
  }
  CloseHandle(process.hThread);
  info->hProcess = process.hProcess;
  return TRUE;
}

int wmain(int argc, wchar_t* argv[])
{
  if (argc == 2 && std::wstring_view(argv[1]) == L"--return-73") {
    return 73;
  }
  if (argc != 1) {
    return unusedHostMain(argc, argv);
  }
  const auto root = fs::temp_directory_path() /
      (L"stalker-process-list-tests-" + std::to_wstring(GetCurrentProcessId()));
  fs::create_directories(root);
  try {
    ProcessList list{root / L"session.bin"};
    const auto path = processListPath(list.configurationPath);
    publishProcessIds(list);
    require(fs::exists(path) && fs::file_size(path) == 0, "Initial empty list must be published");
    testIds = {456, 123, 123};
    publishProcessIds(list);
    require(list.published == std::vector<DWORD>({123, 456}), "PID set must be normalized");

    const auto unchangedTime = fs::file_time_type::clock::now() - std::chrono::hours(1);
    fs::last_write_time(path, unchangedTime);
    testIds = {123, 456};
    publishProcessIds(list);
    require(fs::last_write_time(path) == unchangedTime, "Same PID set must not rewrite the file");

    testIds = {123, 789};
    failQuery = true;
    publishProcessIds(list);
    failQuery = false;
    failRead = true;
    publishProcessIds(list);
    failRead = false;
    require(fs::last_write_time(path) == unchangedTime, "Failed query must preserve the published file");

    const auto lock = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    require(lock != INVALID_HANDLE_VALUE, "Failed to lock the fixture");
    publishProcessIds(list);
    CloseHandle(lock);
    require(!list.published, "Failed write must invalidate the cached set");
    testIds = {123, 456};
    publishProcessIds(list);
    require(fs::last_write_time(path) != unchangedTime, "Failed write must be retried even if the PID set reverts");
    testIds = {123, 789};
    publishProcessIds(list);
    std::ifstream input(path);
    const std::string contents((std::istreambuf_iterator<char>(input)), {});
    input.close();
    require(contents == "123\n789\n", "Changed PID set must be retried and written");

    testIds.clear();
    publishProcessIds(list);
    require(fs::file_size(path) == 0, "Exited processes must be removed");
    fs::last_write_time(path, unchangedTime);
    publishProcessIds(list);
    require(fs::last_write_time(path) == unchangedTime, "Repeated empty list must not be rewritten");
    verifyVirtualElevation(root, fs::absolute(argv[0]));
    fs::remove_all(root);
    return 0;
  } catch (const std::exception& exception) {
    std::cerr << exception.what() << '\n';
    fs::remove_all(root);
    return 1;
  }
}
