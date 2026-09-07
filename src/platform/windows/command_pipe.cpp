#include "aevocis/platform/windows/command_pipe.hpp"

#include <windows.h>
#include <sddl.h>

#include <string_view>

namespace aevocis::platform::windows {

namespace {

constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\AevocisNativeCpp";

[[nodiscard]] HANDLE create_pipe(PSECURITY_ATTRIBUTES security) noexcept {
    return CreateNamedPipeW(kPipeName, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 8192, 8192,
                            1000, security);
}

}  // namespace

CommandPipeServer::~CommandPipeServer() { stop(); }

bool CommandPipeServer::start(Handler handler) noexcept {
    if (!handler || worker_.joinable()) return false;
    handler_ = std::move(handler);
    stopping_.store(false);
    worker_ = std::jthread([this](std::stop_token) { loop(); });
    return true;
}

void CommandPipeServer::stop() noexcept {
    if (!worker_.joinable()) return;
    stopping_.store(true);
    HANDLE wake = CreateFileW(kPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (wake != INVALID_HANDLE_VALUE) CloseHandle(wake);
    worker_.request_stop();
    worker_.join();
    handler_ = {};
}

void CommandPipeServer::loop() noexcept {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, FALSE};
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;OW)", SDDL_REVISION_1, &descriptor, nullptr) != FALSE) {
        security.lpSecurityDescriptor = descriptor;
    }
    while (!stopping_.load()) {
        HANDLE pipe = create_pipe(security.lpSecurityDescriptor != nullptr ? &security : nullptr);
        if (pipe == INVALID_HANDLE_VALUE) break;
        const BOOL connected = ConnectNamedPipe(pipe, nullptr) != FALSE || GetLastError() == ERROR_PIPE_CONNECTED;
        if (!connected) {
            CloseHandle(pipe);
            continue;
        }
        char request[8192]{};
        DWORD read = 0;
        if (ReadFile(pipe, request, sizeof(request) - 1, &read, nullptr) != FALSE && read > 0 && handler_) {
            request[read] = '\0';
            std::string command(request, request + read);
            if (!command.empty() && command.back() == '\n') command.pop_back();
            const std::string response = handler_(std::move(command));
            DWORD written = 0;
            (void)WriteFile(pipe, response.data(), static_cast<DWORD>(response.size()), &written, nullptr);
        }
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
    if (descriptor != nullptr) LocalFree(descriptor);
}

std::string CommandPipeClient::request(const std::string& command) noexcept {
    HANDLE pipe = CreateFileW(kPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return {};
    const std::string message = command + "\n";
    DWORD written = 0;
    if (WriteFile(pipe, message.data(), static_cast<DWORD>(message.size()), &written, nullptr) == FALSE) {
        CloseHandle(pipe);
        return {};
    }
    char response[8192]{};
    DWORD read = 0;
    const BOOL ok = ReadFile(pipe, response, sizeof(response) - 1, &read, nullptr);
    CloseHandle(pipe);
    return ok != FALSE ? std::string(response, response + read) : std::string{};
}

}  // namespace aevocis::platform::windows
