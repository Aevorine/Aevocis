#include "aevocis/platform/windows/autostart.hpp"

#include <windows.h>

#include <string>

namespace aevocis::platform::windows {

namespace {

constexpr wchar_t kRunKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr wchar_t kValueName[] = L"Aevocis";

}  // namespace

bool Autostart::enabled() noexcept {
    HKEY key{};
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKey, 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS) {
        return false;
    }
    wchar_t value[2048]{};
    DWORD bytes = sizeof(value);
    const LSTATUS result = RegGetValueW(key, nullptr, kValueName, RRF_RT_REG_SZ, nullptr, value, &bytes);
    (void)RegCloseKey(key);
    return result == ERROR_SUCCESS && value[0] != L'\0';
}

bool Autostart::set_enabled(bool enable, const std::wstring& executable) noexcept {
    HKEY key{};
    try {
        if (RegCreateKeyExW(HKEY_CURRENT_USER, kRunKey, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS) {
            return false;
        }
        LSTATUS result = ERROR_SUCCESS;
        if (enable) {
            const std::wstring command = L"\"" + executable + L"\"";
            result = RegSetValueExW(key, kValueName, 0, REG_SZ, reinterpret_cast<const BYTE*>(command.c_str()),
                                    static_cast<DWORD>((command.size() + 1) * sizeof(wchar_t)));
        } else {
            result = RegDeleteValueW(key, kValueName);
            if (result == ERROR_FILE_NOT_FOUND) result = ERROR_SUCCESS;
        }
        (void)RegCloseKey(key);
        return result == ERROR_SUCCESS;
    } catch (...) {
        if (key != nullptr) {
            (void)RegCloseKey(key);
        }
        return false;
    }
}

}  // namespace aevocis::platform::windows
