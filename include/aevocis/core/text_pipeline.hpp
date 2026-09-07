#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace aevocis::core {

struct TermRule {
    std::string source;
    std::string replacement;
};

struct TextPipelineOptions {
    bool normalize_whitespace{true};
    bool append_sentence_punctuation{true};
    std::vector<std::string> noise_phrases;
};

struct TextPipelineResult {
    std::string text;
    bool voice_command_candidate{false};
};

class TextPipeline {
public:
    [[nodiscard]] static TextPipelineResult process(std::string_view input, const std::vector<TermRule>& terms,
                                                     const TextPipelineOptions& options = {});
    [[nodiscard]] static std::string normalize_trigger(std::string_view input);
    [[nodiscard]] static bool is_sentence_terminal(unsigned char last_byte) noexcept;
};

}  // namespace aevocis::core
