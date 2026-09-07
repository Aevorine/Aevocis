#include "aevocis/core/voice.hpp"

#include <algorithm>
#include <cctype>
#include <initializer_list>
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

namespace {

[[nodiscard]] std::size_t find_any(std::string_view text, std::initializer_list<std::string_view> needles, std::size_t& matched_length) {
    for (const auto needle : needles) {
        const std::size_t position = text.find(needle);
        if (position != std::string_view::npos) {
            matched_length = needle.size();
            return position;
        }
    }
    matched_length = 0;
    return std::string_view::npos;
}

[[nodiscard]] std::string_view trim_view(std::string_view value) {
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front())) != 0) {
        value.remove_prefix(1);
    }
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back())) != 0) {
        value.remove_suffix(1);
    }
    return value;
}

}  // namespace

std::optional<TermLearnMatch> VoiceCommandMatcher::match_learn_term(std::string_view text) {
    std::string_view remainder = trim_view(text);
    bool prefixed = false;
    for (const std::string_view lead : {"记住", "remember"}) {
        if (remainder.size() > lead.size() && remainder.compare(0, lead.size(), lead) == 0) {
            remainder.remove_prefix(lead.size());
            prefixed = true;
            break;
        }
    }
    if (!prefixed) {
        return std::nullopt;
    }
    remainder = trim_view(remainder);
    std::size_t separator_length = 0;
    const std::size_t separator = find_any(remainder, {"读作", "念作", "写作", " as ", "是"}, separator_length);
    if (separator == std::string_view::npos || separator == 0) {
        return std::nullopt;
    }
    const std::string_view source = trim_view(remainder.substr(0, separator));
    const std::string_view replacement = trim_view(remainder.substr(separator + separator_length));
    if (source.empty() || replacement.empty()) {
        return std::nullopt;
    }
    return TermLearnMatch{std::string(source), std::string(replacement)};
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
