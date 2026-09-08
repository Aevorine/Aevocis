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
    // Live-preview support: lets a caller pull newly-captured audio while recording is still in
    // progress, without stopping capture. copy_since() only ever copies samples the caller has not
    // already consumed (cost proportional to new audio, not total elapsed recording), so polling it
    // periodically during a long dictation stays cheap.
    [[nodiscard]] std::size_t sample_count() const noexcept;
    [[nodiscard]] std::uint32_t current_sample_rate() const noexcept;
    [[nodiscard]] std::vector<float> copy_since(std::size_t offset) const noexcept;

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
