#pragma once

#include <windows.h>

namespace aevocis::platform::windows {

class TrayIcon {
public:
    TrayIcon() = default;
    TrayIcon(const TrayIcon&) = delete;
    TrayIcon& operator=(const TrayIcon&) = delete;
    ~TrayIcon();

    [[nodiscard]] bool install(HWND owner, UINT callback_message, HICON icon) noexcept;
    void remove() noexcept;
    void show_menu() const noexcept;

private:
    HWND owner_{};
    UINT callback_message_{};
    HICON icon_{};
    bool installed_{false};
};

}  // namespace aevocis::platform::windows
