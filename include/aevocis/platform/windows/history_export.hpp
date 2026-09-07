#pragma once

#include "aevocis/platform/windows/storage.hpp"

#include <filesystem>
#include <optional>
#include <string_view>
#include <vector>

namespace aevocis::platform::windows {

enum class HistoryExportFormat { Markdown, PlainText, Json };

class HistoryExporter {
public:
    [[nodiscard]] static bool export_to(HistoryExportFormat format, const std::filesystem::path& path,
                                        const std::vector<HistoryRecord>& records) noexcept;
    // F3: parses the CLI/IPC "export:<format>:<path>" command payload; returns std::nullopt on
    // an unrecognized format rather than guessing, so a malformed request fails loudly instead
    // of silently writing the wrong format.
    [[nodiscard]] static std::optional<HistoryExportFormat> parse_format(std::string_view name) noexcept;
};

}  // namespace aevocis::platform::windows
