#pragma once

#include "aevocis/core/state.hpp"

#include <span>
#include <stop_token>
#include <string>

namespace aevocis::core {

class IRecognizer {
public:
    virtual ~IRecognizer() = default;
    [[nodiscard]] virtual RecognitionResult recognize(std::span<const float> samples, std::uint32_t sample_rate,
                                                      std::stop_token stop) const = 0;
};

class UnavailableRecognizer final : public IRecognizer {
public:
    [[nodiscard]] RecognitionResult recognize(std::span<const float>, std::uint32_t, std::stop_token stop) const override;
};

}  // namespace aevocis::core
