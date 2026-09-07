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
    // B3: releases the loaded model to reclaim its working set after an idle timeout; the next
    // recognize() attempt transparently reloads via the normal !ready() && load() path in
    // Application::run_session, at the cost of that one call paying the load latency again.
    void unload() noexcept;
    [[nodiscard]] bool ready() const noexcept;
    [[nodiscard]] core::RecognitionResult recognize(std::span<const float> samples, std::uint32_t sample_rate,
                                                     std::stop_token stop) const override;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

}  // namespace aevocis::platform::windows
