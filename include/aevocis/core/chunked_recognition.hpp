#pragma once

#include "aevocis/core/recognizer.hpp"

#include <cstdint>
#include <span>
#include <stop_token>
#include <string>
#include <vector>

namespace aevocis::core {

struct ChunkedRecognitionOptions {
    std::uint32_t window_seconds{30};
    std::uint32_t overlap_seconds{2};
};

class ChunkedRecognizer {
public:
    [[nodiscard]] static RecognitionResult recognize(const IRecognizer& recognizer, std::span<const float> samples,
                                                      std::uint32_t sample_rate, std::stop_token stop,
                                                      const ChunkedRecognitionOptions& options = {});

private:
    [[nodiscard]] static std::vector<std::pair<std::size_t, std::size_t>> plan_windows(std::size_t sample_count,
                                                                                        std::uint32_t sample_rate,
                                                                                        const ChunkedRecognitionOptions& options);
};

}  // namespace aevocis::core
