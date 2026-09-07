#pragma once

#include <string>
#include <string_view>

#include <windows.h>

namespace aevocis::platform::windows {

class UpdateManager {
public:
    static void check_and_install_async(HWND owner, std::wstring current_version, std::wstring executable_path) noexcept;

private:
    static void check_and_install(HWND owner, const std::wstring& current_version, const std::wstring& executable_path) noexcept;
};

}  // namespace aevocis::platform::windows
