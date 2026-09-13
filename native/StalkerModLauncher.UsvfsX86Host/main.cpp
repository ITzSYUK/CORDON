#include <windows.h>
#include <shellapi.h>

#include <algorithm>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

#include <usvfs.h>

namespace fs = std::filesystem;

namespace
{
#pragma comment(lib, "shell32.lib")

constexpr std::uint32_t ConfigMagic = 0x32534656; // VFS2
constexpr std::uintmax_t MaxLogFileBytes = 2 * 1024 * 1024;

struct Mapping
{
  std::uint32_t kind{};
  std::wstring source;
  std::wstring destination;
  unsigned int flags{};
};

struct Config
{
  std::string instanceName;
  std::wstring executable;
  std::wstring arguments;
  std::wstring workingDirectory;
  std::wstring diagnosticLogPath;
  std::vector<Mapping> mappings;
};

struct DiagnosticLog
{
  fs::path path;
  std::ofstream output;
};

std::uint32_t readUInt32(std::ifstream& input)
{
  std::uint32_t value{};
  input.read(reinterpret_cast<char*>(&value), sizeof(value));
  if (!input) {
    throw std::runtime_error("Unexpected end of USVFS host configuration");
  }

  return value;
}

std::string readUtf8(std::ifstream& input)
{
  const auto size = readUInt32(input);
  if (size > 16 * 1024 * 1024) {
    throw std::runtime_error("Invalid string size in USVFS host configuration");
  }

  std::string value(size, '\0');
  input.read(value.data(), static_cast<std::streamsize>(size));
  if (!input) {
    throw std::runtime_error("Unexpected end of USVFS host configuration string");
  }

  return value;
}

std::wstring fromUtf8(const std::string& value)
{
  if (value.empty()) {
    return {};
  }

  const int size = MultiByteToWideChar(
      CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
  if (size <= 0) {
    throw std::runtime_error("Invalid UTF-8 in USVFS host configuration");
  }

  std::wstring result(size, L'\0');
  MultiByteToWideChar(
      CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), size);
  return result;
}

Config readConfig(const fs::path& path)
{
  std::ifstream input(path, std::ios::binary);
  if (!input) {
    throw std::runtime_error("Failed to open USVFS host configuration");
  }

  if (readUInt32(input) != ConfigMagic) {
    throw std::runtime_error("Unsupported USVFS host configuration format");
  }

  Config config;
  config.instanceName = readUtf8(input);
  config.executable = fromUtf8(readUtf8(input));
  config.arguments = fromUtf8(readUtf8(input));
  config.workingDirectory = fromUtf8(readUtf8(input));
  config.diagnosticLogPath = fromUtf8(readUtf8(input));

  const auto mappingCount = readUInt32(input);
  if (mappingCount > 100000) {
    throw std::runtime_error("Invalid mapping count in USVFS host configuration");
  }

  config.mappings.reserve(mappingCount);
  for (std::uint32_t index = 0; index < mappingCount; ++index) {
    Mapping mapping;
    mapping.kind = readUInt32(input);
    mapping.flags = readUInt32(input);
    mapping.source = fromUtf8(readUtf8(input));
    mapping.destination = fromUtf8(readUtf8(input));
    config.mappings.push_back(std::move(mapping));
  }

  return config;
}

std::wstring quote(const std::wstring& value)
{
  std::wstring result = L"\"";
  std::size_t backslashes = 0;
  for (const wchar_t character : value) {
    if (character == L'\\') {
      ++backslashes;
      continue;
    }

    if (character == L'\"') {
      result.append(backslashes * 2 + 1, L'\\');
      result.push_back(character);
      backslashes = 0;
      continue;
    }

    result.append(backslashes, L'\\');
    backslashes = 0;
    result.push_back(character);
  }

  result.append(backslashes * 2, L'\\');
  result.push_back(L'\"');
  return result;
}

void prepareLogFile(const fs::path& path)
{
  fs::create_directories(path.parent_path());
  std::error_code error;
  if (!fs::exists(path, error) || fs::file_size(path, error) < MaxLogFileBytes) {
    return;
  }

  const fs::path oldPath = path.parent_path() /
      (path.stem().wstring() + L".old" + path.extension().wstring());
  fs::remove(oldPath, error);
  error.clear();
  fs::rename(path, oldPath, error);
}

void openDiagnosticLog(DiagnosticLog& log, const fs::path& path)
{
  log.path = path;
  prepareLogFile(path);
  log.output.open(path, std::ios::binary | std::ios::app);
}

void rotateDiagnosticLog(DiagnosticLog& log)
{
  log.output.close();
  prepareLogFile(log.path);
  log.output.open(log.path, std::ios::binary | std::ios::app);
}

void drainLogs(DiagnosticLog* log)
{
  if (log == nullptr || !log->output) {
    return;
  }

  std::vector<char> buffer(8192, '\0');
  while (usvfsGetLogMessages(buffer.data(), buffer.size(), false)) {
    const auto position = log->output.tellp();
    if (position >= 0 && static_cast<std::uintmax_t>(position) >= MaxLogFileBytes) {
      rotateDiagnosticLog(*log);
    }
    log->output << buffer.data() << '\n';
  }
  log->output.flush();
}

fs::path processListPath(const fs::path& configurationPath)
{
  fs::path result = configurationPath;
  result += L".pids";
  return result;
}

void publishProcessIds(const fs::path& configurationPath)
{
  std::size_t count = 0;
  if (!usvfsGetVFSProcessList(&count, nullptr)) {
    return;
  }

  std::vector<DWORD> processIds(count);
  if (count > 0) {
    const auto capacity = count;
    if (!usvfsGetVFSProcessList(&count, processIds.data())) {
      return;
    }
    processIds.resize((std::min)(count, capacity));
  }

  std::ofstream output(processListPath(configurationPath), std::ios::binary | std::ios::trunc);
  for (const DWORD processId : processIds) {
    output << processId << '\n';
  }
}

void waitForProcess(
    HANDLE process,
    DiagnosticLog* diagnosticLog,
    const fs::path* configurationPath = nullptr)
{
  int processListPolls = 0;
  while (true) {
    const DWORD waitResult = WaitForSingleObject(process, 25);
    if (waitResult == WAIT_OBJECT_0) {
      break;
    }
    if (waitResult != WAIT_TIMEOUT) {
      throw std::runtime_error(
          "WaitForSingleObject failed: " + std::to_string(GetLastError()));
    }

    drainLogs(diagnosticLog);
    if (configurationPath != nullptr && ++processListPolls == 20) {
      publishProcessIds(*configurationPath);
      processListPolls = 0;
    }
  }
  drainLogs(diagnosticLog);
}

void waitForVfsProcessTree(DiagnosticLog* diagnosticLog, const fs::path& configurationPath)
{
  // Launchers such as Gunslinger Play.exe exit immediately after spawning the
  // actual game. Keep the controller alive until every hooked descendant exits.
  constexpr int requiredEmptyPolls = 10;
  int emptyPolls = 0;
  int processListPolls = 0;
  while (emptyPolls < requiredEmptyPolls) {
    std::size_t processCount = 0;
    if (!usvfsGetVFSProcessList(&processCount, nullptr)) {
      throw std::runtime_error(
          "usvfsGetVFSProcessList failed: " + std::to_string(GetLastError()));
    }

    emptyPolls = processCount == 0 ? emptyPolls + 1 : 0;
    if (++processListPolls == 5 || processCount == 0) {
      publishProcessIds(configurationPath);
      processListPolls = 0;
    }
    drainLogs(diagnosticLog);
    Sleep(100);
  }
}

int runElevated(const fs::path& executablePath, const fs::path& configurationPath)
{
  const auto executable = fs::absolute(executablePath).wstring();
  const auto parameters = quote(configurationPath.wstring());
  const auto workingDirectory = fs::path(executable).parent_path().wstring();
  SHELLEXECUTEINFOW startInfo{};
  startInfo.cbSize = sizeof(startInfo);
  startInfo.fMask = SEE_MASK_NOCLOSEPROCESS;
  startInfo.lpVerb = L"runas";
  startInfo.lpFile = executable.c_str();
  startInfo.lpParameters = parameters.c_str();
  startInfo.lpDirectory = workingDirectory.c_str();
  startInfo.nShow = SW_HIDE;
  if (!ShellExecuteExW(&startInfo) || startInfo.hProcess == nullptr) {
    throw std::runtime_error(
        "Failed to elevate USVFS x86 host: " + std::to_string(GetLastError()));
  }

  waitForProcess(startInfo.hProcess, nullptr);
  DWORD exitCode = 1;
  if (!GetExitCodeProcess(startInfo.hProcess, &exitCode)) {
    const DWORD error = GetLastError();
    CloseHandle(startInfo.hProcess);
    throw std::runtime_error(
        "Failed to read elevated USVFS x86 host exit code: " + std::to_string(error));
  }

  CloseHandle(startInfo.hProcess);
  return static_cast<int>(exitCode);
}
}

int wmain(int argc, wchar_t* argv[])
{
  if (argc != 2) {
    std::cerr << "Usage: StalkerModLauncher.UsvfsX86Host.exe <configuration>\n";
    return 10;
  }

  DiagnosticLog diagnosticLog;
  try {
    const auto config = readConfig(argv[1]);
    if (!config.diagnosticLogPath.empty()) {
      openDiagnosticLog(diagnosticLog, fs::path(config.diagnosticLogPath));
    }
    std::unique_ptr<usvfsParameters, decltype(&usvfsFreeParameters)> parameters(
        usvfsCreateParameters(), &usvfsFreeParameters);
    if (!parameters) {
      throw std::runtime_error("usvfsCreateParameters returned null");
    }

    usvfsSetInstanceName(parameters.get(), config.instanceName.c_str());
    usvfsSetDebugMode(parameters.get(), false);
    usvfsSetLogLevel(parameters.get(), LogLevel::Info);
    usvfsSetCrashDumpType(parameters.get(), CrashDumpsType::None);
    usvfsSetCrashDumpPath(parameters.get(), "");
    usvfsInitLogging(config.diagnosticLogPath.empty());

    if (!usvfsCreateVFS(parameters.get())) {
      throw std::runtime_error("usvfsCreateVFS failed");
    }

    usvfsClearVirtualMappings();
    for (const auto& mapping : config.mappings) {
      const BOOL mapped = mapping.kind == 0
          ? usvfsVirtualLinkDirectoryStatic(
                mapping.source.c_str(), mapping.destination.c_str(), mapping.flags)
          : usvfsVirtualLinkFile(
                mapping.source.c_str(), mapping.destination.c_str(), mapping.flags);
      if (!mapped) {
        throw std::runtime_error("USVFS x86 host mapping failed");
      }
    }

    std::wstring commandLine = quote(config.executable);
    if (!config.arguments.empty()) {
      commandLine += L" " + config.arguments;
    }

    std::vector<wchar_t> commandBuffer(commandLine.begin(), commandLine.end());
    commandBuffer.push_back(L'\0');
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!usvfsCreateProcessHooked(
            config.executable.c_str(), commandBuffer.data(), nullptr, nullptr, FALSE, 0,
            nullptr, config.workingDirectory.c_str(), &startup, &process)) {
      const DWORD error = GetLastError();
      if (error == ERROR_ELEVATION_REQUIRED) {
        usvfsDisconnectVFS();
        if (diagnosticLog.output.is_open()) {
          diagnosticLog.output << "Target requires elevation; restarting USVFS x86 host as administrator.\n";
          diagnosticLog.output.close();
        }
        return runElevated(argv[0], argv[1]);
      }

      throw std::runtime_error("usvfsCreateProcessHooked failed: " + std::to_string(error));
    }

    const fs::path configurationPath(argv[1]);
    publishProcessIds(configurationPath);
    waitForProcess(
        process.hProcess,
        diagnosticLog.output.is_open() ? &diagnosticLog : nullptr,
        &configurationPath);
    DWORD exitCode = 1;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    waitForVfsProcessTree(
        diagnosticLog.output.is_open() ? &diagnosticLog : nullptr,
        configurationPath);
    usvfsDisconnectVFS();
    drainLogs(diagnosticLog.output.is_open() ? &diagnosticLog : nullptr);
    return static_cast<int>(exitCode);
  } catch (const std::exception& exception) {
    std::cerr << "USVFS x86 host failed: " << exception.what() << "\n";
    usvfsDisconnectVFS();
    return 20;
  }
}
