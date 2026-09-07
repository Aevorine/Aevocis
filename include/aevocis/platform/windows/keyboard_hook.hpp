#pragma once

#include <windows.h>

namespace aevocis::platform::windows {

class KeyboardHook {
public:
    KeyboardHook() = default;
    KeyboardHook(const KeyboardHook&) = delete;
    KeyboardHook& operator=(const KeyboardHook&) = delete;
    ~KeyboardHook();

    [[nodiscard]] bool install(HWND target, UINT message) noexcept;
    void uninstall() noexcept;

private:
    static LRESULT CALLBACK callback(int code, WPARAM wparam, LPARAM lparam) noexcept;
    static KeyboardHook* active_;

    HHOOK hook_{};
    HWND target_{};
    UINT message_{};
};

}  // namespace aevocis::platform::windows
