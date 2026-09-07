#include "aevocis/core/noise_gate.hpp"

#include <algorithm>
#include <cmath>
#include <numbers>

namespace aevocis::core {

NoiseGate::NoiseGate(std::uint32_t sample_rate, const NoiseGateOptions& options) noexcept
    : sample_rate_(sample_rate), options_(options) {
    // Standard one-pole high-pass coefficient derivation (RC = 1 / (2*pi*fc)).
    const float rc = 1.0F / (2.0F * std::numbers::pi_v<float> * options_.high_pass_cutoff_hz);
    const float dt = sample_rate_ > 0 ? 1.0F / static_cast<float>(sample_rate_) : 0.0F;
    high_pass_alpha_ = rc / (rc + dt);
}

void NoiseGate::process(std::span<float> samples) noexcept {
    if (sample_rate_ == 0) {
        return;
    }
    for (float& sample : samples) {
        // One-pole high-pass: y[n] = alpha * (y[n-1] + x[n] - x[n-1]).
        const float filtered = high_pass_alpha_ * (high_pass_previous_output_ + sample - high_pass_previous_input_);
        high_pass_previous_input_ = sample;
        high_pass_previous_output_ = filtered;

        const float magnitude = std::fabs(filtered);
        if (samples_seen_ < options_.calibration_samples) {
            // Calibration window: let the floor estimate settle against real ambient noise
            // before the gate starts attenuating anything, so a loud opening word isn't clipped
            // by a floor estimate of zero.
            noise_floor_ = noise_floor_ + options_.floor_smoothing * (magnitude - noise_floor_);
            ++samples_seen_;
            sample = filtered;
            continue;
        }
        // Track the floor only from segments that still look like background noise (quieter
        // than the current gate threshold) so real speech doesn't drag the floor upward.
        const float threshold = noise_floor_ * options_.attenuation_margin;
        if (magnitude < threshold) {
            noise_floor_ = noise_floor_ + options_.floor_smoothing * (magnitude - noise_floor_);
        }
        if (magnitude <= noise_floor_) {
            sample = 0.0F;
        } else if (magnitude < threshold && threshold > noise_floor_) {
            // Soft ramp between the floor and the gate threshold instead of a hard cutoff, so
            // quiet trailing consonants fade rather than clicking off.
            const float ratio = (magnitude - noise_floor_) / (threshold - noise_floor_);
            sample = filtered * ratio;
        } else {
            sample = filtered;
        }
    }
}

}  // namespace aevocis::core
