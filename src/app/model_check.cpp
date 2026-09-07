#include "aevocis/platform/windows/sensevoice_recognizer.hpp"

#include <filesystem>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <optional>
#include <vector>

namespace {

struct Waveform {
    std::vector<float> samples;
    std::uint32_t sample_rate{};
};

[[nodiscard]] std::uint32_t u32(const std::vector<std::byte>& bytes, std::size_t offset) {
    return static_cast<std::uint32_t>(std::to_integer<unsigned char>(bytes[offset])) |
           (static_cast<std::uint32_t>(std::to_integer<unsigned char>(bytes[offset + 1])) << 8) |
           (static_cast<std::uint32_t>(std::to_integer<unsigned char>(bytes[offset + 2])) << 16) |
           (static_cast<std::uint32_t>(std::to_integer<unsigned char>(bytes[offset + 3])) << 24);
}

[[nodiscard]] std::optional<Waveform> read_wave(const std::filesystem::path& path) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) return std::nullopt;
    stream.seekg(0, std::ios::end);
    const auto file_size = stream.tellg();
    if (file_size <= 0) return std::nullopt;
    stream.seekg(0, std::ios::beg);
    std::vector<std::byte> bytes(static_cast<std::size_t>(file_size));
    stream.read(reinterpret_cast<char*>(bytes.data()), file_size);
    if (!stream) return std::nullopt;
    if (bytes.size() < 44 || std::memcmp(bytes.data(), "RIFF", 4) != 0 || std::memcmp(bytes.data() + 8, "WAVE", 4) != 0) return std::nullopt;
    std::uint16_t channels = 0;
    std::uint16_t bits = 0;
    std::uint32_t sample_rate = 0;
    std::size_t data_offset = 0;
    std::size_t data_size = 0;
    for (std::size_t offset = 12; offset + 8 <= bytes.size();) {
        const std::uint32_t size = u32(bytes, offset + 4);
        const std::size_t content = offset + 8;
        if (content + size > bytes.size()) break;
        if (std::memcmp(bytes.data() + offset, "fmt ", 4) == 0 && size >= 16) {
            channels = static_cast<std::uint16_t>(std::to_integer<unsigned char>(bytes[content + 2]) |
                                                  (std::to_integer<unsigned char>(bytes[content + 3]) << 8));
            sample_rate = u32(bytes, content + 4);
            bits = static_cast<std::uint16_t>(std::to_integer<unsigned char>(bytes[content + 14]) |
                                              (std::to_integer<unsigned char>(bytes[content + 15]) << 8));
        } else if (std::memcmp(bytes.data() + offset, "data", 4) == 0) {
            data_offset = content;
            data_size = size;
        }
        offset = content + size + (size & 1U);
    }
    if (channels == 0 || sample_rate == 0 || bits != 16 || data_offset == 0 || data_size == 0) return std::nullopt;
    const std::size_t frame_bytes = static_cast<std::size_t>(channels) * sizeof(std::int16_t);
    if (frame_bytes == 0) return std::nullopt;
    Waveform waveform;
    waveform.sample_rate = sample_rate;
    waveform.samples.reserve(data_size / frame_bytes);
    for (std::size_t offset = data_offset; offset + frame_bytes <= data_offset + data_size; offset += frame_bytes) {
        float mono = 0.0F;
        for (std::uint16_t channel = 0; channel < channels; ++channel) {
            const std::size_t sample_offset = offset + static_cast<std::size_t>(channel) * sizeof(std::int16_t);
            const std::int16_t sample = static_cast<std::int16_t>(std::to_integer<unsigned char>(bytes[sample_offset]) |
                                                                  (std::to_integer<unsigned char>(bytes[sample_offset + 1]) << 8));
            mono += static_cast<float>(sample) / 32768.0F;
        }
        waveform.samples.push_back(mono / static_cast<float>(channels));
    }
    return waveform;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc < 2 || argc > 3) {
        std::wcerr << L"usage: aevocis_model_check <model-directory> [wave-file]\n";
        return 2;
    }
    const auto directory = std::filesystem::path(argv[1]).string();
    aevocis::platform::windows::SenseVoiceRecognizer recognizer;
    if (!recognizer.load(directory) || !recognizer.ready()) {
        std::cerr << "sensevoice_load=failed\n";
        return 1;
    }
    std::cout << "sensevoice_load=ok\n";
    if (argc == 3) {
        const auto waveform = read_wave(argv[2]);
        if (!waveform.has_value()) {
            std::cerr << "wave_read=failed\n";
            return 1;
        }
        const auto result = recognizer.recognize(waveform->samples, waveform->sample_rate, {});
        if (!result.ok() || result.text.empty()) {
            std::cerr << "recognition=failed\n";
            return 1;
        }
        std::cout << "recognition=ok\ntext_length=" << result.text.size() << "\n";
    }
    return 0;
}
