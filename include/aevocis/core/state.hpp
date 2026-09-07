#pragma once

#include <cstdint>
#include <string>

namespace aevocis::core {

enum class AppState : std::uint8_t {
    Idle,
    Starting,
    Capturing,
    Recognizing,
    PostProcessing,
    Confirming,
    Injecting,
    Cancelled,
    Failed
};

enum class TriggerMode : std::uint8_t { Hold, Toggle };

enum class ErrorCode : std::uint8_t {
    None,
    Busy,
    TargetChanged,
    AudioUnavailable,
    RecognitionUnavailable,
    RecognitionFailed,
    InjectionFailed,
    Cancelled,
    InvalidConfiguration
};

using SessionId = std::uint64_t;

struct TargetToken {
    std::uint64_t window_id{};
    std::uint32_t process_id{};

    [[nodiscard]] bool valid() const noexcept { return window_id != 0 && process_id != 0; }
};

struct RecognitionResult {
    std::string text;
    ErrorCode error{ErrorCode::None};
    bool final_result{true};

    [[nodiscard]] bool ok() const noexcept { return error == ErrorCode::None; }
};

struct SessionSnapshot {
    SessionId id{};
    AppState state{AppState::Idle};
    TargetToken target{};
    ErrorCode error{ErrorCode::None};
    bool cancel_requested{false};
};

[[nodiscard]] const char* to_string(AppState state) noexcept;
[[nodiscard]] const char* to_string(ErrorCode error) noexcept;

}  // namespace aevocis::core
