#include "aevocis/core/voice.hpp"

#include <algorithm>
#include <cctype>
#include <string>

namespace aevocis::core {

namespace {

[[nodiscard]] bool trailing_noise(std::string_view value, std::size_t& end) {
    while (end > 0) {
        if (std::isspace(static_cast<unsigned char>(value[end - 1])) != 0) {
            --end;
            continue;
        }
        bool removed = false;
        for (const std::string_view suffix : {"。", "！", "？", "…", "、", "，", "；", "：", "\"", "'", "）", ")", "]", "】", "”", "’"}) {
            if (end >= suffix.size() && value.compare(end - suffix.size(), suffix.size(), suffix) == 0) {
                end -= suffix.size();
                removed = true;
                break;
            }
        }
        if (removed) {
            continue;
        }
        const unsigned char last = static_cast<unsigned char>(value[end - 1]);
        if (last == '.' || last == '!' || last == '?' || last == ',' || last == ';' || last == ':') {
            --end;
            continue;
        }
        break;
    }
    return end != 0;
}

[[nodiscard]] std::string normalize(std::string_view text) {
    std::size_t begin = 0;
    while (begin < text.size() && std::isspace(static_cast<unsigned char>(text[begin])) != 0) {
        ++begin;
    }
    std::size_t end = text.size();
    while (end > begin && std::isspace(static_cast<unsigned char>(text[end - 1])) != 0) {
        --end;
    }
    std::string result(text.substr(begin, end - begin));
    end = result.size();
    (void)trailing_noise(result, end);
    result.resize(end);
    std::transform(result.begin(), result.end(), result.begin(), [](unsigned char byte) {
        return static_cast<char>(std::tolower(byte));
    });
    return result;
}

[[nodiscard]] bool ends_with_case_insensitive(std::string_view text, std::string_view suffix) {
    if (suffix.size() > text.size()) {
        return false;
    }
    const std::size_t start = text.size() - suffix.size();
    for (std::size_t index = 0; index < suffix.size(); ++index) {
        const auto left = static_cast<unsigned char>(text[start + index]);
        const auto right = static_cast<unsigned char>(suffix[index]);
        if (std::tolower(left) != std::tolower(right)) {
            return false;
        }
    }
    return true;
}

[[nodiscard]] bool equal_case_insensitive(std::string_view left, std::string_view right) {
    return left.size() == right.size() && ends_with_case_insensitive(left, right);
}

}  // namespace

std::vector<VoiceCommand> VoiceCommandMatcher::defaults() {
    return {{"删除这段", VoiceCommandAction::Cancel},
            {"算了不要了", VoiceCommandAction::Cancel},
            {"取消", VoiceCommandAction::Cancel},
            {"换行", VoiceCommandAction::SendEnter},
            {"全部大写", VoiceCommandAction::UppercaseSuffix}};
}

std::optional<CommandMatch> VoiceCommandMatcher::match(std::string_view text,
                                                       const std::vector<VoiceCommand>& commands) {
    const std::string normalized = normalize(text);
    if (normalized.empty()) {
        return std::nullopt;
    }
    for (const auto& command : commands) {
        const std::string phrase = normalize(command.phrase);
        if (phrase.empty()) {
            continue;
        }
        if (command.action == VoiceCommandAction::UppercaseSuffix) {
            if (normalized.size() <= phrase.size() || !ends_with_case_insensitive(normalized, phrase)) {
                continue;
            }
            const std::string remaining = normalize(normalized.substr(0, normalized.size() - phrase.size()));
            if (!remaining.empty()) {
                return CommandMatch{command.action, remaining};
            }
        } else if (equal_case_insensitive(normalized, phrase)) {
            return CommandMatch{command.action, {}};
        }
    }
    return std::nullopt;
}

}  // namespace aevocis::core
