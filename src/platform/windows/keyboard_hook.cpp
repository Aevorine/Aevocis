#include "aevocis/platform/windows/keyboard_hook.hpp"

namespace aevocis::platform::windows {

KeyboardHook* KeyboardHook::active_ = nullptr;

KeyboardHook::~KeyboardHook() { uninstall(); }

bool KeyboardHook::install(HWND target, UINT message) noexcept {
    uninstall();
    if (target == nullptr || message == 0) {
        return false;
    }
    target_ = target;
    message_ = message;
    active_ = this;
    hook_ = SetWindowsHookExW(WH_KEYBOARD_LL, callback, GetModuleHandleW(nullptr), 0);
    if (hook_ == nullptr) {
        active_ = nullptr;
        target_ = nullptr;
        message_ = 0;
        return false;
    }
    return true;
}

void KeyboardHook::uninstall() noexcept {
    if (hook_ != nullptr) {
        (void)UnhookWindowsHookEx(hook_);
        hook_ = nullptr;
    }
    if (active_ == this) {
        active_ = nullptr;
    }
    target_ = nullptr;
    message_ = 0;
}

LRESULT CALLBACK KeyboardHook::callback(int code, WPARAM wparam, LPARAM lparam) noexcept {
    if (code == HC_ACTION && active_ != nullptr) {
        const auto* keyboard = reinterpret_cast<const KBDLLHOOKSTRUCT*>(lparam);
        if (keyboard != nullptr && (keyboard->flags & LLKHF_INJECTED) == 0) {
            const UINT message = static_cast<UINT>(wparam);
            const bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            const bool up = message == WM_KEYUP || message == WM_SYSKEYUP;
            if ((down || up) && active_->target_ != nullptr) {
                (void)PostMessageW(active_->target_, active_->message_, keyboard->vkCode, down ? 1 : 0);
            }
        }
    }
    return CallNextHookEx(nullptr, code, wparam, lparam);
}

}  // namespace aevocis::platform::windows
