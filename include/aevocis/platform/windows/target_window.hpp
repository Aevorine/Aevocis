#pragma once

#include "aevocis/core/state.hpp"

#include <windows.h>

namespace aevocis::platform::windows {

struct TargetWindowToken {
    HWND hwnd{};
    DWORD process_id{};

    [[nodiscard]] static TargetWindowToken capture() noexcept;
    [[nodiscard]] bool valid() const noexcept;
    [[nodiscard]] bool still_valid() const noexcept;
    [[nodiscard]] core::TargetToken core_token() const noexcept;
};

}  // namespace aevocis::platform::windows
