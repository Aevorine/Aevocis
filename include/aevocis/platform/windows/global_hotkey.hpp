#pragma once

#include <windows.h>

namespace aevocis::platform::windows {

class GlobalHotkey {
public:
    GlobalHotkey() = default;
    GlobalHotkey(const GlobalHotkey&) = delete;
    GlobalHotkey& operator=(const GlobalHotkey&) = delete;
    ~GlobalHotkey();

    [[nodiscard]] bool register_hotkey(HWND target, int id, UINT modifiers, UINT virtual_key) noexcept;
    void unregister() noexcept;

private:
    HWND target_{};
    int id_{};
    bool registered_{false};
};

}  // namespace aevocis::platform::windows
