#pragma once

#include <string>

namespace aevocis::platform::windows {

class Autostart {
public:
    [[nodiscard]] static bool enabled() noexcept;
    [[nodiscard]] static bool set_enabled(bool enable, const std::wstring& executable) noexcept;
};

}  // namespace aevocis::platform::windows
