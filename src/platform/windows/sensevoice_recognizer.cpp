#include "aevocis/platform/windows/sensevoice_recognizer.hpp"

#include <algorithm>
#include <filesystem>
#include <mutex>
#include <optional>
#include <thread>

#ifdef AEVOCIS_HAS_SHERPA
#include <sherpa-onnx/c-api/cxx-api.h>
#endif

namespace aevocis::platform::windows {

struct SenseVoiceRecognizer::Impl {
    mutable std::mutex mutex;
#ifdef AEVOCIS_HAS_SHERPA
    std::optional<sherpa_onnx::cxx::OfflineRecognizer> recognizer;
    std::optional<sherpa_onnx::cxx::OfflinePunctuation> punctuation;
#endif
};

SenseVoiceRecognizer::SenseVoiceRecognizer() : impl_(std::make_unique<Impl>()) {}
SenseVoiceRecognizer::~SenseVoiceRecognizer() = default;

bool SenseVoiceRecognizer::load(const std::string& model_directory) noexcept {
#ifndef AEVOCIS_HAS_SHERPA
    (void)model_directory;
    return false;
#else
    try {
        const std::filesystem::path directory(model_directory);
        const std::filesystem::path model_file = directory / "model.int8.onnx";
        const std::filesystem::path tokens_file = directory / "tokens.txt";
        if (!std::filesystem::is_regular_file(model_file) || !std::filesystem::is_regular_file(tokens_file)) {
            return false;
        }
        sherpa_onnx::cxx::OfflineRecognizerConfig config;
        config.model_config.sense_voice.model = model_file.string();
        config.model_config.sense_voice.language = "auto";
        config.model_config.sense_voice.use_itn = true;
        config.model_config.tokens = tokens_file.string();
        const auto available = std::thread::hardware_concurrency();
        config.model_config.num_threads = static_cast<int32_t>(std::min<unsigned int>(available == 0 ? 2U : available, 4U));
        config.model_config.provider = "cpu";
        auto recognizer = sherpa_onnx::cxx::OfflineRecognizer::Create(config);
        if (recognizer.Get() == nullptr) {
            return false;
        }
        std::optional<sherpa_onnx::cxx::OfflinePunctuation> punctuation;
        const auto punctuation_file = directory.parent_path() / "punct" / "model.int8.onnx";
        if (std::filesystem::is_regular_file(punctuation_file)) {
            sherpa_onnx::cxx::OfflinePunctuationConfig punctuation_config;
            punctuation_config.model.ct_transformer = punctuation_file.string();
            punctuation_config.model.num_threads = config.model_config.num_threads;
            punctuation_config.model.provider = "cpu";
            auto candidate = sherpa_onnx::cxx::OfflinePunctuation::Create(punctuation_config);
            if (candidate.Get() != nullptr) {
                punctuation = std::move(candidate);
            }
        }
        {
            std::scoped_lock lock(impl_->mutex);
            impl_->recognizer = std::move(recognizer);
            impl_->punctuation = std::move(punctuation);
        }
        std::vector<float> warmup(4800, 0.0F);
        (void)recognize(warmup, 16000, {});
        return true;
    } catch (...) {
        return false;
    }
#endif
}

bool SenseVoiceRecognizer::ready() const noexcept {
#ifdef AEVOCIS_HAS_SHERPA
    std::scoped_lock lock(impl_->mutex);
    return impl_->recognizer.has_value() && impl_->recognizer->Get() != nullptr;
#else
    return false;
#endif
}

core::RecognitionResult SenseVoiceRecognizer::recognize(std::span<const float> samples, std::uint32_t sample_rate,
                                                        std::stop_token stop) const {
    if (stop.stop_requested()) {
        return {"", core::ErrorCode::Cancelled, true};
    }
#ifndef AEVOCIS_HAS_SHERPA
    (void)samples;
    (void)sample_rate;
    return {"", core::ErrorCode::RecognitionUnavailable, true};
#else
    std::scoped_lock lock(impl_->mutex);
    if (!impl_->recognizer.has_value() || impl_->recognizer->Get() == nullptr || samples.empty() || sample_rate == 0) {
        return {"", core::ErrorCode::RecognitionUnavailable, true};
    }
    auto stream = impl_->recognizer->CreateStream();
    stream.AcceptWaveform(static_cast<int32_t>(sample_rate), samples.data(), static_cast<int32_t>(samples.size()));
    if (stop.stop_requested()) {
        return {"", core::ErrorCode::Cancelled, true};
    }
    impl_->recognizer->Decode(&stream);
    std::string text = impl_->recognizer->GetResult(&stream).text;
    if (impl_->punctuation.has_value() && !text.empty()) {
        text = impl_->punctuation->AddPunctuation(text);
    }
    return {std::move(text), core::ErrorCode::None, true};
#endif
}

}  // namespace aevocis::platform::windows
