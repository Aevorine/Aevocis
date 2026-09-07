#include "aevocis/platform/windows/wasapi_recorder.hpp"

#include <audioclient.h>
#include <mmdeviceapi.h>
#include <synchapi.h>
#include <windows.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <cstring>

namespace aevocis::platform::windows {

using Microsoft::WRL::ComPtr;

namespace {

[[nodiscard]] bool is_float_format(const WAVEFORMATEX* format) noexcept {
    if (format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) {
        return true;
    }
    if (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE && format->cbSize >= 22) {
        const auto* extensible = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format);
        return IsEqualGUID(extensible->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) != FALSE;
    }
    return false;
}

[[nodiscard]] float pcm_sample(const std::byte* source, WORD bits) noexcept {
    if (bits == 16) {
        std::int16_t value{};
        std::memcpy(&value, source, sizeof(value));
        return static_cast<float>(value) / 32768.0F;
    }
    if (bits == 24) {
        std::int32_t value = (static_cast<std::int32_t>(source[0]) & 0xFF) |
                             ((static_cast<std::int32_t>(source[1]) & 0xFF) << 8) |
                             ((static_cast<std::int32_t>(source[2]) & 0xFF) << 16);
        if ((value & 0x00800000) != 0) {
            value |= static_cast<std::int32_t>(0xFF000000);
        }
        return static_cast<float>(value) / 8388608.0F;
    }
    if (bits == 32) {
        std::int32_t value{};
        std::memcpy(&value, source, sizeof(value));
        return static_cast<float>(value) / 2147483648.0F;
    }
    return 0.0F;
}

}  // namespace

WasapiRecorder::~WasapiRecorder() {
    if (running()) {
        (void)stop();
    }
}

bool WasapiRecorder::start() noexcept {
    std::scoped_lock lock(mutex_);
    if (running_) {
        return false;
    }
    samples_.clear();
    samples_.reserve(16000 * 120);
    sample_rate_ = 0;
    stop_event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (stop_event_ == nullptr) {
        return false;
    }
    running_ = true;
    worker_ = std::jthread([this](std::stop_token stop) { capture_loop(stop); });
    return true;
}

RecordedAudio WasapiRecorder::stop() noexcept {
    std::jthread worker;
    HANDLE event = nullptr;
    {
        std::scoped_lock lock(mutex_);
        if (!running_) {
            return {};
        }
        running_ = false;
        event = static_cast<HANDLE>(stop_event_);
        stop_event_ = nullptr;
        worker = std::move(worker_);
    }
    if (event != nullptr) {
        (void)SetEvent(event);
    }
    if (worker.joinable()) {
        worker.request_stop();
        worker.join();
    }
    if (event != nullptr) {
        (void)CloseHandle(event);
    }
    std::scoped_lock lock(mutex_);
    return {std::move(samples_), sample_rate_};
}

bool WasapiRecorder::running() const noexcept {
    std::scoped_lock lock(mutex_);
    return running_;
}

void WasapiRecorder::capture_loop(std::stop_token stop) noexcept {
    if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) {
        return;
    }
    ComPtr<IMMDeviceEnumerator> enumerator;
    ComPtr<IMMDevice> device;
    ComPtr<IAudioClient> client;
    ComPtr<IAudioCaptureClient> capture;
    WAVEFORMATEX* format = nullptr;
    HANDLE event = nullptr;
    HRESULT result = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
    if (SUCCEEDED(result)) {
        result = enumerator->GetDefaultAudioEndpoint(eCapture, eConsole, &device);
    }
    if (SUCCEEDED(result)) {
        result = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client);
    }
    if (SUCCEEDED(result)) {
        result = client->GetMixFormat(&format);
    }
    if (SUCCEEDED(result)) {
        {
            std::scoped_lock lock(mutex_);
            event = static_cast<HANDLE>(stop_event_);
        }
        result = client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 10000000, 0, format, nullptr);
    }
    HANDLE capture_event = nullptr;
    if (SUCCEEDED(result)) {
        capture_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        result = capture_event == nullptr ? E_FAIL : client->SetEventHandle(capture_event);
    }
    if (SUCCEEDED(result)) {
        result = client->GetService(IID_PPV_ARGS(&capture));
    }
    if (SUCCEEDED(result)) {
        std::scoped_lock lock(mutex_);
        sample_rate_ = format->nSamplesPerSec;
        result = client->Start();
    }

    if (SUCCEEDED(result)) {
        const WORD channels = std::max<WORD>(1, format->nChannels);
        const WORD bits = format->wBitsPerSample;
        const bool float_format = is_float_format(format);
        while (!stop.stop_requested()) {
            const HANDLE handles[] = {capture_event, event};
            const DWORD wait = WaitForMultipleObjects(2, handles, FALSE, 100);
            if (wait == WAIT_OBJECT_0 + 1) {
                break;
            }
            UINT32 packet_frames = 0;
            while (SUCCEEDED(capture->GetNextPacketSize(&packet_frames)) && packet_frames > 0) {
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                if (FAILED(capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr))) {
                    break;
                }
                std::scoped_lock lock(mutex_);
                const std::size_t max_samples = 16000U * 120U;
                const std::size_t available = max_samples > samples_.size() ? max_samples - samples_.size() : 0;
                const std::size_t frame_count = std::min<std::size_t>(frames, available);
                const std::size_t bytes_per_sample = format->wBitsPerSample / 8;
                const std::size_t block_bytes = format->nBlockAlign;
                for (std::size_t frame = 0; frame < frame_count; ++frame) {
                    float mono = 0.0F;
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && bytes_per_sample != 0) {
                        const auto* frame_data = reinterpret_cast<const std::byte*>(data) + frame * block_bytes;
                        for (WORD channel = 0; channel < channels; ++channel) {
                            const auto* channel_data = frame_data + channel * bytes_per_sample;
                            float sample = 0.0F;
                            if (float_format && bits == 32) {
                                std::memcpy(&sample, channel_data, sizeof(sample));
                            } else {
                                sample = pcm_sample(channel_data, bits);
                            }
                            mono += sample;
                        }
                        mono /= static_cast<float>(channels);
                    }
                    samples_.push_back(std::clamp(mono, -1.0F, 1.0F));
                }
                (void)capture->ReleaseBuffer(frames);
                packet_frames = 0;
            }
        }
        (void)client->Stop();
    }
    if (capture_event != nullptr) {
        (void)CloseHandle(capture_event);
    }
    if (format != nullptr) {
        CoTaskMemFree(format);
    }
    CoUninitialize();
}

}  // namespace aevocis::platform::windows
