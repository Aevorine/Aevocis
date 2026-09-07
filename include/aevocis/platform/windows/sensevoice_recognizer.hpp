#pragma once

#include "aevocis/core/recognizer.hpp"

#include <memory>
#include <string>

namespace aevocis::platform::windows {

class SenseVoiceRecognizer final : public core::IRecognizer {
public:
    SenseVoiceRecognizer();
    ~SenseVoiceRecognizer() override;
    SenseVoiceRecognizer(const SenseVoiceRecognizer&) = delete;
    SenseVoiceRecognizer& operator=(const SenseVoiceRecognizer&) = delete;

    [[nodiscard]] bool load(const std::string& model_directory) noexcept;
    [[nodiscard]] bool ready() const noexcept;
    [[nodiscard]] core::RecognitionResult recognize(std::span<const float> samples, std::uint32_t sample_rate,
                                                     std::stop_token stop) const override;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

}  // namespace aevocis::platform::windows
