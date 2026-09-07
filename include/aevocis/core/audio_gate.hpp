#pragma once

#include <cstdint>
#include <span>

namespace aevocis::core {

struct AudioGateOptions {
    float rms_threshold{0.006F};
    std::uint32_t min_samples_at_16k{4800};
};

class AudioGate {
public:
    [[nodiscard]] static bool should_recognize(std::span<const float> samples, std::uint32_t sample_rate,
                                                const AudioGateOptions& options = {}) noexcept;
    [[nodiscard]] static float rms(std::span<const float> samples) noexcept;
};

}  // namespace aevocis::core
