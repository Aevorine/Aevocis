#include "aevocis/core/audio_gate.hpp"

#include <cmath>

namespace aevocis::core {

float AudioGate::rms(std::span<const float> samples) noexcept {
    if (samples.empty()) {
        return 0.0F;
    }
    double sum_squares = 0.0;
    for (const float sample : samples) {
        sum_squares += static_cast<double>(sample) * static_cast<double>(sample);
    }
    return static_cast<float>(std::sqrt(sum_squares / static_cast<double>(samples.size())));
}

bool AudioGate::should_recognize(std::span<const float> samples, std::uint32_t sample_rate,
                                  const AudioGateOptions& options) noexcept {
    if (sample_rate == 0) {
        return false;
    }
    const double scaled_minimum = static_cast<double>(options.min_samples_at_16k) * (static_cast<double>(sample_rate) / 16000.0);
    if (static_cast<double>(samples.size()) < scaled_minimum) {
        return false;
    }
    return rms(samples) >= options.rms_threshold;
}

}  // namespace aevocis::core
