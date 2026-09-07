#pragma once

#include "aevocis/core/state.hpp"

namespace aevocis::platform::windows {

// E5: writes one line per state/error transition -- stage name and error code only, never the
// recognized text or any other user content -- so diagnosing "which stage broke" never requires
// storing what the user said.
class AppLog {
public:
    static void record_state(core::AppState state) noexcept;
    static void record_error(core::AppState state, core::ErrorCode error) noexcept;
};

}  // namespace aevocis::platform::windows
