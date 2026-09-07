#include "aevocis/platform/windows/app_log.hpp"

#include "aevocis/platform/windows/storage.hpp"

#include <windows.h>

#include <chrono>
#include <fstream>
#include <mutex>
#include <sstream>

namespace aevocis::platform::windows {

namespace {

std::mutex g_log_mutex;

void append_line(const std::string& line) noexcept {
    try {
        std::scoped_lock lock(g_log_mutex);
        const auto path = Storage::data_directory() / L"app.log";
        std::filesystem::create_directories(path.parent_path());
        std::ofstream stream(path, std::ios::app | std::ios::binary);
        if (!stream) return;
        stream << line << '\n';
        // E5 keeps the log small and useful for "what stage broke recently", not a full
        // audit trail -- once it crosses ~1 MB, restart from empty rather than growing forever.
        if (stream.tellp() > static_cast<std::streamoff>(1024 * 1024)) {
            stream.close();
            std::ofstream truncate(path, std::ios::trunc | std::ios::binary);
        }
    } catch (...) {
    }
}

[[nodiscard]] std::string timestamp() noexcept {
    const auto now = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    tm local{};
    localtime_s(&local, &now);
    std::ostringstream stream;
    stream << (1900 + local.tm_year) << '-' << (local.tm_mon + 1) << '-' << local.tm_mday << ' ' << local.tm_hour << ':'
           << local.tm_min << ':' << local.tm_sec;
    return stream.str();
}

}  // namespace

void AppLog::record_state(core::AppState state) noexcept {
    std::ostringstream line;
    line << timestamp() << " state=" << core::to_string(state);
    append_line(line.str());
}

void AppLog::record_error(core::AppState state, core::ErrorCode error) noexcept {
    std::ostringstream line;
    line << timestamp() << " state=" << core::to_string(state) << " error=" << core::to_string(error);
    append_line(line.str());
}

void AppLog::record_metric(std::string_view name, std::uint64_t value) noexcept {
    std::ostringstream line;
    line << timestamp() << " metric=" << name << " value=" << value;
    append_line(line.str());
}

}  // namespace aevocis::platform::windows
