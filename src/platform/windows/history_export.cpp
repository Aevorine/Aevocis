#include "aevocis/platform/windows/history_export.hpp"

#include <sstream>

namespace aevocis::platform::windows {

namespace {

[[nodiscard]] std::string escape_json_text(std::string_view value) {
    std::string result;
    result.reserve(value.size() + 8);
    for (const unsigned char byte : value) {
        switch (byte) {
        case '"': result += "\\\""; break;
        case '\\': result += "\\\\"; break;
        case '\n': result += "\\n"; break;
        case '\r': result += "\\r"; break;
        default: result.push_back(static_cast<char>(byte)); break;
        }
    }
    return result;
}

[[nodiscard]] std::string escape_markdown(std::string_view value) {
    std::string result;
    result.reserve(value.size());
    for (const char byte : value) {
        if (byte == '\n') {
            result += "  \n";
        } else {
            result.push_back(byte);
        }
    }
    return result;
}

}  // namespace

std::optional<HistoryExportFormat> HistoryExporter::parse_format(std::string_view name) noexcept {
    if (name == "markdown" || name == "md") return HistoryExportFormat::Markdown;
    if (name == "txt" || name == "text") return HistoryExportFormat::PlainText;
    if (name == "json") return HistoryExportFormat::Json;
    return std::nullopt;
}

bool HistoryExporter::export_to(HistoryExportFormat format, const std::filesystem::path& path,
                                const std::vector<HistoryRecord>& records) noexcept {
    try {
        std::ostringstream out;
        switch (format) {
        case HistoryExportFormat::Markdown:
            out << "# Aevocis 历史记录\n\n";
            for (const auto& record : records) {
                out << "- " << escape_markdown(record.text) << "\n";
            }
            break;
        case HistoryExportFormat::PlainText:
            for (const auto& record : records) {
                out << record.text << "\n";
            }
            break;
        case HistoryExportFormat::Json:
            out << "{\n  \"items\": [\n";
            for (std::size_t index = 0; index < records.size(); ++index) {
                out << "    {\"text\": \"" << escape_json_text(records[index].text) << "\", \"epoch\": "
                    << records[index].epoch_seconds << "}" << (index + 1 < records.size() ? "," : "") << "\n";
            }
            out << "  ]\n}\n";
            break;
        }
        return Storage::atomic_write(path, out.str());
    } catch (...) {
        return false;
    }
}

}  // namespace aevocis::platform::windows
