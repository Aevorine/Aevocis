#include "aevocis/platform/windows/global_hotkey.hpp"

namespace aevocis::platform::windows {

GlobalHotkey::~GlobalHotkey() { unregister(); }

bool GlobalHotkey::register_hotkey(HWND target, int id, UINT modifiers, UINT virtual_key) noexcept {
    unregister();
    if (target == nullptr || id <= 0) {
        return false;
    }
    registered_ = RegisterHotKey(target, id, modifiers, virtual_key) != FALSE;
    if (registered_) {
        target_ = target;
        id_ = id;
    }
    return registered_;
}

void GlobalHotkey::unregister() noexcept {
    if (registered_) {
        (void)UnregisterHotKey(target_, id_);
        registered_ = false;
        target_ = nullptr;
        id_ = 0;
    }
}

}  // namespace aevocis::platform::windows
