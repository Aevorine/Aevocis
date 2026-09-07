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

class VoiceCommandMatcher {
public:
    [[nodiscard]] static std::vector<VoiceCommand> defaults();
    [[nodiscard]] static std::optional<CommandMatch> match(std::string_view text,
                                                            const std::vector<VoiceCommand>& commands);
};

}  // namespace aevocis::core
