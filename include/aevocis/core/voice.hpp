#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace aevocis::core {

enum class VoiceCommandAction : std::uint8_t { Cancel, SendEnter, UppercaseSuffix };

struct VoiceCommand {
    std::string phrase;
    VoiceCommandAction action{VoiceCommandAction::Cancel};
};

struct CommandMatch {
    VoiceCommandAction action{VoiceCommandAction::Cancel};
    std::string remaining_text;
};

struct TermLearnMatch {
    std::string source;
    std::string replacement;
};

class VoiceCommandMatcher {
public:
    [[nodiscard]] static std::vector<VoiceCommand> defaults();
    [[nodiscard]] static std::optional<CommandMatch> match(std::string_view text,
                                                            const std::vector<VoiceCommand>& commands);

    // A6: parses an utterance of the form "记住 <A> 读作 <B>" / "remember <A> as <B>" into a
    // term rule the caller can persist. Requires both sides non-empty so a partial or
    // malformed utterance never silently learns an empty replacement.
    [[nodiscard]] static std::optional<TermLearnMatch> match_learn_term(std::string_view text);
};

}  // namespace aevocis::core
