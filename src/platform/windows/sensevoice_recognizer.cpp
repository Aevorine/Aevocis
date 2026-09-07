#include "aevocis/platform/windows/sensevoice_recognizer.hpp"

#include <algorithm>
#include <filesystem>
#include <mutex>
#include <optional>
#include <thread>

#ifdef AEVOCIS_HAS_SHERPA
#include <sherpa-onnx/c-api/c-api.h>
#endif

namespace aevocis::platform::windows {

struct SenseVoiceRecognizer::Impl {
    mutable std::mutex mutex;
#ifdef AEVOCIS_HAS_SHERPA
    const SherpaOnnxOfflineRecognizer* recognizer{};
    const SherpaOnnxOfflinePunctuation* punctuation{};
#endif
};

SenseVoiceRecognizer::SenseVoiceRecognizer() : impl_(std::make_unique<Impl>()) {}
SenseVoiceRecognizer::~SenseVoiceRecognizer() {
#ifdef AEVOCIS_HAS_SHERPA
    std::scoped_lock lock(impl_->mutex);
    if (impl_->punctuation != nullptr) {
        SherpaOnnxDestroyOfflinePunctuation(impl_->punctuation);
    }
    if (impl_->recognizer != nullptr) {
        SherpaOnnxDestroyOfflineRecognizer(impl_->recognizer);
    }
#endif
}

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
        const std::string model_path = model_file.string();
        const std::string tokens_path = tokens_file.string();
        SherpaOnnxOfflineRecognizerConfig config{};
        config.model_config.sense_voice.model = model_path.c_str();
        config.model_config.sense_voice.language = "auto";
        config.model_config.sense_voice.use_itn = 1;
        config.model_config.tokens = tokens_path.c_str();
        const auto available = std::thread::hardware_concurrency();
        config.model_config.num_threads = static_cast<int32_t>(std::clamp(available == 0 ? 2U : available / 2U, 1U, 4U));
        config.model_config.provider = "cpu";
        config.decoding_method = "greedy_search";
        const SherpaOnnxOfflineRecognizer* recognizer = SherpaOnnxCreateOfflineRecognizer(&config);
        if (recognizer == nullptr) {
            return false;
        }
        const SherpaOnnxOfflinePunctuation* punctuation = nullptr;
        const auto punctuation_file = directory.parent_path() / "punct" / "model.int8.onnx";
        if (std::filesystem::is_regular_file(punctuation_file)) {
            const std::string punctuation_path = punctuation_file.string();
            SherpaOnnxOfflinePunctuationConfig punctuation_config{};
            punctuation_config.model.ct_transformer = punctuation_path.c_str();
            punctuation_config.model.num_threads = config.model_config.num_threads;
            punctuation_config.model.provider = "cpu";
            punctuation = SherpaOnnxCreateOfflinePunctuation(&punctuation_config);
            if (punctuation == nullptr) {
                punctuation = nullptr;
            }
        }
        {
            std::scoped_lock lock(impl_->mutex);
            if (impl_->punctuation != nullptr) {
                SherpaOnnxDestroyOfflinePunctuation(impl_->punctuation);
            }
            if (impl_->recognizer != nullptr) {
                SherpaOnnxDestroyOfflineRecognizer(impl_->recognizer);
            }
            impl_->recognizer = recognizer;
            impl_->punctuation = punctuation;
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
    return impl_->recognizer != nullptr;
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
    if (impl_->recognizer == nullptr || samples.empty() || sample_rate == 0) {
        return {"", core::ErrorCode::RecognitionUnavailable, true};
    }
    const SherpaOnnxOfflineStream* stream = SherpaOnnxCreateOfflineStream(impl_->recognizer);
    if (stream == nullptr) {
        return {"", core::ErrorCode::RecognitionFailed, true};
    }
    SherpaOnnxAcceptWaveformOffline(stream, static_cast<int32_t>(sample_rate), samples.data(), static_cast<int32_t>(samples.size()));
    if (stop.stop_requested()) {
        SherpaOnnxDestroyOfflineStream(stream);
        return {"", core::ErrorCode::Cancelled, true};
    }
    SherpaOnnxDecodeOfflineStream(impl_->recognizer, stream);
    const SherpaOnnxOfflineRecognizerResult* result = SherpaOnnxGetOfflineStreamResult(stream);
    if (result == nullptr || result->text == nullptr) {
        if (result != nullptr) SherpaOnnxDestroyOfflineRecognizerResult(result);
        SherpaOnnxDestroyOfflineStream(stream);
        return {"", core::ErrorCode::RecognitionFailed, true};
    }
    std::string text(result->text);
    SherpaOnnxDestroyOfflineRecognizerResult(result);
    SherpaOnnxDestroyOfflineStream(stream);
    if (impl_->punctuation != nullptr && !text.empty()) {
        const char* punctuated = SherpaOfflinePunctuationAddPunct(impl_->punctuation, text.c_str());
        if (punctuated != nullptr) {
            text = punctuated;
            SherpaOfflinePunctuationFreeText(punctuated);
        }
    }
    return {std::move(text), core::ErrorCode::None, true};
#endif
}

}  // namespace aevocis::platform::windows
