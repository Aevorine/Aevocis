#pragma once

#include <cstdint>
#include <span>
#include <vector>

namespace aevocis::core {

struct NoiseGateOptions {
    // First-order high-pass cutoff -- removes DC offset and low-frequency rumble (fans, AC,
    // desk vibration) well below human voice fundamentals without touching speech content.
    float high_pass_cutoff_hz{80.0F};
    // Samples during the attack window feed the running noise-floor estimate instead of being
    // gated, so the gate has real data to calibrate against before it starts attenuating.
    std::uint32_t calibration_samples{1600};
    float attenuation_margin{1.6F};
    float floor_smoothing{0.05F};
};

// A4: real-time noise reduction via a high-pass filter (fixed cutoff) plus an adaptive
// noise-floor gate (tracks ambient level during quiet stretches, attenuates anything close to
// it) -- a lighter-weight, dependency-free technique than a learned model like RNNoise, chosen
// deliberately over vendoring a new third-party binary blind under this session's context
// pressure. Processes in place so callers don't need a second buffer.
class NoiseGate {
public:
    explicit NoiseGate(std::uint32_t sample_rate, const NoiseGateOptions& options = {}) noexcept;
    void process(std::span<float> samples) noexcept;

private:
    std::uint32_t sample_rate_;
    NoiseGateOptions options_;
    float high_pass_alpha_;
    float high_pass_previous_input_{0.0F};
    float high_pass_previous_output_{0.0F};
    float noise_floor_{0.0F};
    std::uint32_t samples_seen_{0};
};

}  // namespace aevocis::core
