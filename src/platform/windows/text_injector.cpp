#include "aevocis/platform/windows/text_injector.hpp"

#include <windows.h>

#include <algorithm>
#include <string>
#include <vector>

namespace aevocis::platform::windows {

namespace {

[[nodiscard]] std::wstring utf8_to_wide(std::string_view value) {
    if (value.empty()) {
        return {};
    }
    const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (length <= 0) {
        return {};
    }
    std::wstring result(static_cast<std::size_t>(length), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), length) != length) {
        return {};
    }
    return result;
}

constexpr UINT kModifierKeys[] = {VK_MENU,  VK_LMENU,  VK_RMENU,  VK_CONTROL, VK_LCONTROL,
                                  VK_RCONTROL, VK_SHIFT, VK_LSHIFT, VK_RSHIFT, VK_LWIN, VK_RWIN};

// SendInput never resets modifier state before delivering the keystrokes it's given -- if a
// modifier neither inject() nor send_virtual_key() asked for happens to already be physically
// held (e.g. Alt from an in-progress Alt-Tab, or the user just resting a finger on Ctrl),
// Windows can interpret the injected keystrokes as an unintended chord: KEYEVENTF_UNICODE text
// delivered while Alt is held can pop the target window's menu bar into focus (a real, documented
// SendInput+KEYEVENTF_UNICODE side effect, not something either call configures on purpose), and
// an injected VK_RETURN under a held Alt reads as Alt+Enter -- fullscreen toggle in many apps.
// Releasing any modifier that's actually down right before injecting avoids both. Nothing needs
// restoring afterward: the injected content (recognized speech, or an undo backspace) has no
// relationship to whatever chord the user's physical fingers happened to be mid-pressing.
void release_held_modifiers() noexcept {
    std::vector<INPUT> ups;
    ups.reserve(std::size(kModifierKeys));
    for (const UINT vk : kModifierKeys) {
        if ((GetAsyncKeyState(static_cast<int>(vk)) & 0x8000) != 0) {
            INPUT up{};
            up.type = INPUT_KEYBOARD;
            up.ki.wVk = static_cast<WORD>(vk);
            up.ki.dwFlags = KEYEVENTF_KEYUP;
            ups.push_back(up);
        }
    }
    if (!ups.empty()) {
        (void)SendInput(static_cast<UINT>(ups.size()), ups.data(), sizeof(INPUT));
    }
}

}  // namespace

bool TextInjector::inject(const TargetWindowToken& target, std::string_view utf8) const noexcept {
    if (!target.still_valid()) {
        return false;
    }
    const std::wstring wide = utf8_to_wide(utf8);
    if (wide.empty() && !utf8.empty()) {
        return false;
    }
    release_held_modifiers();

    constexpr std::size_t chunk_size = 128;
    for (std::size_t offset = 0; offset < wide.size(); offset += chunk_size) {
        if (!target.still_valid()) {
            return false;
        }
        const std::size_t end = std::min(offset + chunk_size, wide.size());
        std::vector<INPUT> inputs;
        inputs.reserve((end - offset) * 2);
        for (std::size_t index = offset; index < end; ++index) {
            INPUT down{};
            down.type = INPUT_KEYBOARD;
            down.ki.wScan = wide[index];
            down.ki.dwFlags = KEYEVENTF_UNICODE;
            inputs.push_back(down);
            INPUT up = down;
            up.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            inputs.push_back(up);
        }
        if (SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT)) != inputs.size()) {
            return false;
        }
    }
    return true;
}

bool TextInjector::send_virtual_key(const TargetWindowToken& target, std::uint16_t virtual_key,
                                    std::size_t repeat) const noexcept {
    if (repeat == 0 || repeat > 2000 || !target.still_valid()) {
        return false;
    }
    release_held_modifiers();
    std::vector<INPUT> inputs;
    inputs.reserve(repeat * 2);
    for (std::size_t index = 0; index < repeat; ++index) {
        INPUT down{};
        down.type = INPUT_KEYBOARD;
        down.ki.wVk = virtual_key;
        inputs.push_back(down);
        INPUT up = down;
        up.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs.push_back(up);
    }
    return target.still_valid() && SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT)) == inputs.size();
}

std::size_t TextInjector::utf16_length(std::string_view utf8) noexcept {
    return utf8_to_wide(utf8).size();
}

}  // namespace aevocis::platform::windows
