#pragma once

#include "aevocis/core/state.hpp"

#include <cstdint>
#include <string_view>

namespace aevocis::platform::windows {

// E5: writes one line per state/error transition -- stage name and error code only, never the
// recognized text or any other user content -- so diagnosing "which stage broke" never requires
// storing what the user said.
class AppLog {
public:
    static void record_state(core::AppState state) noexcept;
    static void record_error(core::AppState state, core::ErrorCode error) noexcept;
    // B5 and future timing/counter instrumentation: name + number only, same redaction rule as
    // the state/error lines above.
    static void record_metric(std::string_view name, std::uint64_t value) noexcept;
};

}  // namespace aevocis::platform::windows
