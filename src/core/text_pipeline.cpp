#include "aevocis/core/text_pipeline.hpp"

#include <algorithm>
#include <cctype>

namespace aevocis::core {

namespace {

void replace_all(std::string& value, std::string_view from, std::string_view to) {
    if (from.empty()) {
        return;
    }
    std::size_t position = 0;
    while ((position = value.find(from, position)) != std::string::npos) {
        value.replace(position, from.size(), to);
        position += to.size();
    }
}

void trim_ascii(std::string& value) {
    const auto not_space = [](unsigned char ch) { return !std::isspace(ch); };
    value.erase(value.begin(), std::find_if(value.begin(), value.end(), not_space));
    value.erase(std::find_if(value.rbegin(), value.rend(), not_space).base(), value.end());
}

[[nodiscard]] bool ends_with(std::string_view value, std::string_view suffix) noexcept {
    return value.size() >= suffix.size() && value.compare(value.size() - suffix.size(), suffix.size(), suffix) == 0;
}

[[nodiscard]] bool ends_with_terminal(std::string_view value) noexcept {
    if (value.empty()) return false;
    const unsigned char last = static_cast<unsigned char>(value.back());
    return last == '.' || last == '!' || last == '?' || last == ';' || last == ':' || last == ',' ||
           ends_with(value, "。") || ends_with(value, "！") || ends_with(value, "？") || ends_with(value, "；") ||
           ends_with(value, "：") || ends_with(value, "，") || ends_with(value, "、") || ends_with(value, "…");
}

}  // namespace

TextPipelineResult TextPipeline::process(std::string_view input, const std::vector<TermRule>& terms,
                                         const TextPipelineOptions& options) {
    std::string value(input);
    for (const auto& phrase : options.noise_phrases) {
        replace_all(value, phrase, "");
    }
    for (const auto& term : terms) {
        replace_all(value, term.source, term.replacement);
    }
    if (options.normalize_whitespace) {
        std::string compact;
        compact.reserve(value.size());
        bool previous_space = false;
        for (const unsigned char ch : value) {
            if (std::isspace(ch) != 0) {
                if (!previous_space) {
                    compact.push_back(' ');
                }
                previous_space = true;
            } else {
                compact.push_back(static_cast<char>(ch));
                previous_space = false;
            }
        }
        value = std::move(compact);
    }
    trim_ascii(value);
    const auto trigger = normalize_trigger(value);
    const bool command = trigger == "撤销" || trigger == "取消" || trigger == "清空历史" || trigger == "打开设置";
    if (options.append_sentence_punctuation && !value.empty() && !ends_with_terminal(value)) {
        value += "。";
    }
    return {std::move(value), command};
}

std::string TextPipeline::normalize_trigger(std::string_view input) {
    std::string value(input);
    trim_ascii(value);
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

bool TextPipeline::is_sentence_terminal(unsigned char last_byte) noexcept {
    return last_byte == '.' || last_byte == '!' || last_byte == '?' || last_byte == ';' || last_byte == ':' ||
           last_byte == ',' || last_byte >= 0x80;
}

}  // namespace aevocis::core
