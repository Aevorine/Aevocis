#pragma once

#include "aevocis/platform/windows/target_window.hpp"

#include <string_view>
#include <cstddef>
#include <cstdint>

namespace aevocis::platform::windows {

class TextInjector {
public:
    [[nodiscard]] bool inject(const TargetWindowToken& target, std::string_view utf8) const noexcept;
    [[nodiscard]] bool send_virtual_key(const TargetWindowToken& target, std::uint16_t virtual_key,
                                        std::size_t repeat = 1) const noexcept;
    [[nodiscard]] static std::size_t utf16_length(std::string_view utf8) noexcept;
};

}  // namespace aevocis::platform::windows
