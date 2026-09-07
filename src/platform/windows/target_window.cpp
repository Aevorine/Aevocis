#include "aevocis/platform/windows/target_window.hpp"

namespace aevocis::platform::windows {

TargetWindowToken TargetWindowToken::capture() noexcept {
    TargetWindowToken result{GetForegroundWindow(), 0};
    if (result.hwnd != nullptr) {
        (void)GetWindowThreadProcessId(result.hwnd, &result.process_id);
    }
    return result;
}

bool TargetWindowToken::valid() const noexcept {
    return hwnd != nullptr && process_id != 0 && IsWindow(hwnd) != FALSE;
}

bool TargetWindowToken::still_valid() const noexcept {
    if (!valid()) {
        return false;
    }
    DWORD current_process = 0;
    (void)GetWindowThreadProcessId(hwnd, &current_process);
    return current_process == process_id && GetForegroundWindow() == hwnd;
}

core::TargetToken TargetWindowToken::core_token() const noexcept {
    return {reinterpret_cast<std::uint64_t>(hwnd), static_cast<std::uint32_t>(process_id)};
}

}  // namespace aevocis::platform::windows
