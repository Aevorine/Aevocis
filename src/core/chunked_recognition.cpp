#include "aevocis/core/chunked_recognition.hpp"

#include <algorithm>

namespace aevocis::core {

std::vector<std::pair<std::size_t, std::size_t>> ChunkedRecognizer::plan_windows(std::size_t sample_count, std::uint32_t sample_rate,
                                                                                  const ChunkedRecognitionOptions& options) {
    std::vector<std::pair<std::size_t, std::size_t>> windows;
    const std::size_t window_samples = static_cast<std::size_t>(options.window_seconds) * sample_rate;
    const std::size_t overlap_samples = static_cast<std::size_t>(options.overlap_seconds) * sample_rate;
    if (window_samples == 0 || sample_count <= window_samples) {
        windows.emplace_back(0, sample_count);
        return windows;
    }
    const std::size_t step = window_samples > overlap_samples ? window_samples - overlap_samples : window_samples;
    std::size_t start = 0;
    while (start < sample_count) {
        const std::size_t end = std::min(start + window_samples, sample_count);
        windows.emplace_back(start, end);
        if (end == sample_count) {
            break;
        }
        start += step;
    }
    return windows;
}

RecognitionResult ChunkedRecognizer::recognize(const IRecognizer& recognizer, std::span<const float> samples,
                                               std::uint32_t sample_rate, std::stop_token stop,
                                               const ChunkedRecognitionOptions& options) {
    const auto windows = plan_windows(samples.size(), sample_rate, options);
    if (windows.size() <= 1) {
        return recognizer.recognize(samples, sample_rate, stop);
    }
    std::string combined;
    for (const auto& [start, end] : windows) {
        if (stop.stop_requested()) {
            return {std::move(combined), ErrorCode::Cancelled};
        }
        const auto window = samples.subspan(start, end - start);
        const RecognitionResult piece = recognizer.recognize(window, sample_rate, stop);
        if (!piece.ok()) {
            if (combined.empty()) {
                return piece;
            }
            break;
        }
        if (!combined.empty() && !piece.text.empty()) {
            combined += ' ';
        }
        combined += piece.text;
    }
    return {std::move(combined), ErrorCode::None};
}

}  // namespace aevocis::core
