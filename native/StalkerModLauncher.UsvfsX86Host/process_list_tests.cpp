#include <windows.h>
#include <chrono>
#include <cstdint>
#include <iterator>
#include <usvfs.h>

BOOL WINAPI testProcessList(size_t* count, LPDWORD ids);
#define usvfsGetVFSProcessList testProcessList
#define wmain unusedHostMain
#include "main.cpp"
#undef wmain

namespace
{
std::vector<DWORD> testIds;
bool failQuery = false;
bool failRead = false;

void require(bool condition, const char* message)
{
  if (!condition) {
    throw std::runtime_error(message);
  }
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

int main()
{
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
    fs::remove_all(root);
    return 0;
  } catch (const std::exception& exception) {
    std::cerr << exception.what() << '\n';
    fs::remove_all(root);
    return 1;
  }
}
