#include "aevocis/platform/windows/external_pipeline.hpp"

#include <windows.h>

#include <filesystem>

namespace aevocis::platform::windows {

namespace {

struct PipeHandles {
    HANDLE read{INVALID_HANDLE_VALUE};
    HANDLE write{INVALID_HANDLE_VALUE};

    ~PipeHandles() {
        if (read != INVALID_HANDLE_VALUE) CloseHandle(read);
        if (write != INVALID_HANDLE_VALUE) CloseHandle(write);
    }
};

}  // namespace

std::optional<std::string> ExternalPipeline::run(const std::wstring& executable_path, const std::string& input_utf8,
                                                  std::uint32_t timeout_ms) noexcept {
    try {
        if (executable_path.empty() || !std::filesystem::is_regular_file(executable_path)) {
            return std::nullopt;
        }
        SECURITY_ATTRIBUTES security{};
        security.nLength = sizeof(security);
        security.bInheritHandle = TRUE;

        PipeHandles stdin_pipe;
        PipeHandles stdout_pipe;
        if (CreatePipe(&stdin_pipe.read, &stdin_pipe.write, &security, 0) == FALSE) return std::nullopt;
        if (CreatePipe(&stdout_pipe.read, &stdout_pipe.write, &security, 0) == FALSE) return std::nullopt;
        // The write end of stdin and the read end of stdout must not be inherited by the child,
        // or the pipes never see EOF/close correctly once this process is also holding them.
        if (SetHandleInformation(stdin_pipe.write, HANDLE_FLAG_INHERIT, 0) == FALSE) return std::nullopt;
        if (SetHandleInformation(stdout_pipe.read, HANDLE_FLAG_INHERIT, 0) == FALSE) return std::nullopt;

        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
        startup.wShowWindow = SW_HIDE;
        startup.hStdInput = stdin_pipe.read;
        startup.hStdOutput = stdout_pipe.write;
        startup.hStdError = stdout_pipe.write;

        PROCESS_INFORMATION process{};
        std::wstring command_line = L"\"" + executable_path + L"\"";
        const BOOL created = CreateProcessW(executable_path.c_str(), command_line.data(), nullptr, nullptr, TRUE,
                                            CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process);
        if (created == FALSE) {
            return std::nullopt;
        }
        // These two ends belong to the child now; keeping them open here would deadlock the
        // read loop below (it would never see EOF because this process still holds a writable
        // handle to the same pipe).
        CloseHandle(stdin_pipe.read);
        stdin_pipe.read = INVALID_HANDLE_VALUE;
        CloseHandle(stdout_pipe.write);
        stdout_pipe.write = INVALID_HANDLE_VALUE;

        DWORD written = 0;
        (void)WriteFile(stdin_pipe.write, input_utf8.data(), static_cast<DWORD>(input_utf8.size()), &written, nullptr);
        CloseHandle(stdin_pipe.write);
        stdin_pipe.write = INVALID_HANDLE_VALUE;

        const DWORD wait_result = WaitForSingleObject(process.hProcess, timeout_ms);
        std::string output;
        if (wait_result == WAIT_OBJECT_0) {
            char buffer[4096];
            DWORD available = 0;
            while (PeekNamedPipe(stdout_pipe.read, nullptr, 0, nullptr, &available, nullptr) != FALSE && available > 0) {
                DWORD read_bytes = 0;
                if (ReadFile(stdout_pipe.read, buffer, sizeof(buffer), &read_bytes, nullptr) == FALSE || read_bytes == 0) {
                    break;
                }
                output.append(buffer, read_bytes);
            }
        } else {
            // Timed out or wait failed -- the child is untrusted user configuration, not part of
            // this app, so it gets terminated rather than left to linger.
            TerminateProcess(process.hProcess, 1);
        }
        DWORD exit_code = 1;
        GetExitCodeProcess(process.hProcess, &exit_code);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        if (wait_result != WAIT_OBJECT_0 || exit_code != 0 || output.empty()) {
            return std::nullopt;
        }
        return output;
    } catch (...) {
        return std::nullopt;
    }
}

}  // namespace aevocis::platform::windows
