#pragma once

#include <cstdint>
#include <mutex>
#include <stop_token>
#include <thread>
#include <vector>

namespace aevocis::platform::windows {

struct RecordedAudio {
    std::vector<float> samples;
    std::uint32_t sample_rate{};
};

class WasapiRecorder {
public:
    WasapiRecorder() = default;
    WasapiRecorder(const WasapiRecorder&) = delete;
    WasapiRecorder& operator=(const WasapiRecorder&) = delete;
    ~WasapiRecorder();

    [[nodiscard]] bool start() noexcept;
    [[nodiscard]] RecordedAudio stop() noexcept;
    [[nodiscard]] bool running() const noexcept;

private:
    void capture_loop(std::stop_token stop) noexcept;

    mutable std::mutex mutex_;
    std::vector<float> samples_;
    std::uint32_t sample_rate_{};
    void* stop_event_{};
    std::jthread worker_;
    bool running_{false};
};

}  // namespace aevocis::platform::windows
