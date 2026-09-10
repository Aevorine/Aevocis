#include "aevocis/platform/windows/storage.hpp"

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <fstream>
#include <sstream>

namespace aevocis::platform::windows {

namespace {

[[nodiscard]] std::string escape_json(std::string_view value) {
    std::string result;
    result.reserve(value.size() + 8);
    for (const unsigned char byte : value) {
        switch (byte) {
        case '"': result += "\\\""; break;
        case '\\': result += "\\\\"; break;
        case '\n': result += "\\n"; break;
        case '\r': result += "\\r"; break;
        case '\t': result += "\\t"; break;
        default: result.push_back(static_cast<char>(byte)); break;
        }
    }
    return result;
}

[[nodiscard]] std::vector<std::string> json_strings(std::string_view json) {
    std::vector<std::string> values;
    std::string current;
    bool quoted = false;
    bool escaped = false;
    for (const char byte : json) {
        if (!quoted) {
            if (byte == '"') {
                quoted = true;
                current.clear();
            }
            continue;
        }
        if (escaped) {
            switch (byte) {
            case 'n': current.push_back('\n'); break;
            case 'r': current.push_back('\r'); break;
            case 't': current.push_back('\t'); break;
            default: current.push_back(byte); break;
            }
            escaped = false;
        } else if (byte == '\\') {
            escaped = true;
        } else if (byte == '"') {
            values.push_back(current);
            quoted = false;
        } else {
            current.push_back(byte);
        }
    }
    return values;
}

[[nodiscard]] std::uint32_t json_number(std::string_view json, std::string_view key, std::uint32_t fallback) {
    const std::string needle = "\"" + std::string(key) + "\"";
    const std::size_t key_position = json.find(needle);
    if (key_position == std::string_view::npos) {
        return fallback;
    }
    const std::size_t colon = json.find(':', key_position + needle.size());
    if (colon == std::string_view::npos) {
        return fallback;
    }
    std::size_t position = colon + 1;
    while (position < json.size() && (json[position] == ' ' || json[position] == '\t')) {
        ++position;
    }
    try {
        return static_cast<std::uint32_t>(std::stoul(std::string(json.substr(position))));
    } catch (...) {
        return fallback;
    }
}

[[nodiscard]] std::string json_string(std::string_view json, std::string_view key, std::string_view fallback) {
    const std::string needle = "\"" + std::string(key) + "\"";
    const std::size_t key_position = json.find(needle);
    if (key_position == std::string_view::npos) {
        return std::string(fallback);
    }
    const std::size_t colon = json.find(':', key_position + needle.size());
    if (colon == std::string_view::npos) {
        return std::string(fallback);
    }
    const std::size_t open_quote = json.find('"', colon);
    if (open_quote == std::string_view::npos) {
        return std::string(fallback);
    }
    std::size_t position = open_quote + 1;
    std::string value;
    while (position < json.size() && json[position] != '"') {
        if (json[position] == '\\' && position + 1 < json.size()) {
            ++position;
        }
        value.push_back(json[position]);
        ++position;
    }
    return value;
}

[[nodiscard]] std::wstring utf8_to_wide(std::string_view utf8) {
    if (utf8.empty()) return {};
    const int length = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (length <= 0) return {};
    std::wstring wide(static_cast<std::size_t>(length), L'\0');
    (void)MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), wide.data(), length);
    return wide;
}

[[nodiscard]] std::string wide_to_utf8(const std::wstring& wide) {
    if (wide.empty()) return {};
    const int length = WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) return {};
    std::string result(static_cast<std::size_t>(length), '\0');
    (void)WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), result.data(), length, nullptr, nullptr);
    return result;
}

[[nodiscard]] bool json_bool(std::string_view json, std::string_view key, bool fallback) {
    const std::string needle = "\"" + std::string(key) + "\"";
    const std::size_t key_position = json.find(needle);
    if (key_position == std::string_view::npos) {
        return fallback;
    }
    const std::size_t colon = json.find(':', key_position + needle.size());
    if (colon == std::string_view::npos) {
        return fallback;
    }
    const std::string value(json.substr(colon + 1, 8));
    if (value.find("true") != std::string::npos) return true;
    if (value.find("false") != std::string::npos) return false;
    return fallback;
}

[[nodiscard]] std::int64_t now_seconds() noexcept {
    return std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

}  // namespace

std::filesystem::path Storage::data_directory() {
    wchar_t value[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", value, ARRAYSIZE(value));
    if (length == 0 || length >= ARRAYSIZE(value)) {
        return std::filesystem::temp_directory_path() / L"Aevocis";
    }
    return std::filesystem::path(value) / L"Aevocis";
}

bool Storage::atomic_write(const std::filesystem::path& path, std::string_view content) noexcept {
    try {
        std::filesystem::create_directories(path.parent_path());
        const auto temporary = path.wstring() + L".tmp";
        {
            std::ofstream stream(std::filesystem::path(temporary), std::ios::binary | std::ios::trunc);
            if (!stream) return false;
            stream.write(content.data(), static_cast<std::streamsize>(content.size()));
            stream.flush();
            if (!stream) return false;
        }
        return MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE;
    } catch (...) {
        return false;
    }
}

std::string Storage::read_text(const std::filesystem::path& path) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) return {};
    std::ostringstream buffer;
    buffer << stream.rdbuf();
    return buffer.str();
}

namespace {
constexpr int kMaxThemeIndex = 3;
}  // namespace

AppSettings SettingsStore::load() const {
    AppSettings settings;
    std::string json = Storage::read_text(Storage::data_directory() / L"settings.json");
    // E3: a primary file that exists but carries none of the expected keys (truncated by a
    // crash mid-write before the atomic rename ever lands, or hand-edited into garbage) is
    // treated as corrupt and the last-known-good backup is tried instead, rather than
    // silently falling back straight to hard defaults and discarding the user's real settings.
    if (!json.empty() && json.find("push_to_talk_virtual_key") == std::string::npos) {
        const std::string backup = Storage::read_text(Storage::data_directory() / L"settings.json.bak");
        if (!backup.empty()) {
            json = backup;
        }
    }
    if (json.empty()) return settings;
    settings.push_to_talk_virtual_key = json_number(json, "push_to_talk_virtual_key", settings.push_to_talk_virtual_key);
    // Absent from settings.json files written by <= v0.4.7 -- defaults to 0, i.e. the previously
    // implicit "the push-to-talk key triggers on its own" behaviour, so an old file keeps working.
    settings.push_to_talk_modifiers = json_number(json, "push_to_talk_modifiers", settings.push_to_talk_modifiers) &
                                      (MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN);
    settings.show_hide_modifiers = json_number(json, "show_hide_modifiers", settings.show_hide_modifiers);
    settings.show_hide_virtual_key = json_number(json, "show_hide_virtual_key", settings.show_hide_virtual_key);
    settings.history_retention_days = std::clamp(json_number(json, "history_retention_days", settings.history_retention_days), 1U, 3650U);
    settings.theme = static_cast<int>(std::min(json_number(json, "theme", 0U), static_cast<std::uint32_t>(kMaxThemeIndex)));
    settings.toggle_mode = json_bool(json, "toggle_mode", settings.toggle_mode);
    settings.punctuation = json_bool(json, "punctuation", settings.punctuation);
    settings.draft_confirmation = json_bool(json, "draft_confirmation", settings.draft_confirmation);
    settings.autostart = json_bool(json, "autostart", settings.autostart);
    settings.external_pipeline_path = utf8_to_wide(json_string(json, "external_pipeline_path", ""));
    return settings;
}

bool SettingsStore::save(const AppSettings& settings) const noexcept {
    try {
        // E3: roll the previous good settings.json into settings.json.bak before overwriting,
        // so a future corrupt write still leaves one prior-good snapshot load() can recover from.
        const auto primary = Storage::data_directory() / L"settings.json";
        const std::string previous = Storage::read_text(primary);
        if (!previous.empty()) {
            (void)Storage::atomic_write(Storage::data_directory() / L"settings.json.bak", previous);
        }
    } catch (...) {
    }
    std::ostringstream json;
    json << "{\n"
         << "  \"push_to_talk_virtual_key\": " << settings.push_to_talk_virtual_key << ",\n"
         << "  \"push_to_talk_modifiers\": " << settings.push_to_talk_modifiers << ",\n"
         << "  \"show_hide_modifiers\": " << settings.show_hide_modifiers << ",\n"
         << "  \"show_hide_virtual_key\": " << settings.show_hide_virtual_key << ",\n"
         << "  \"toggle_mode\": " << (settings.toggle_mode ? "true" : "false") << ",\n"
         << "  \"punctuation\": " << (settings.punctuation ? "true" : "false") << ",\n"
         << "  \"draft_confirmation\": " << (settings.draft_confirmation ? "true" : "false") << ",\n"
         << "  \"autostart\": " << (settings.autostart ? "true" : "false") << ",\n"
         << "  \"history_retention_days\": " << settings.history_retention_days << ",\n"
         << "  \"theme\": " << settings.theme << ",\n"
         << "  \"external_pipeline_path\": \"" << escape_json(wide_to_utf8(settings.external_pipeline_path)) << "\"\n}\n";
    return Storage::atomic_write(Storage::data_directory() / L"settings.json", json.str());
}

void HistoryStore::load() {
    records_.clear();
    const auto values = json_strings(Storage::read_text(Storage::data_directory() / L"history.json"));
    bool has_text_field = false;
    for (const auto& value : values) {
        if (value == "text" || value == "Text") {
            has_text_field = true;
            break;
        }
    }
    if (has_text_field) {
        for (std::size_t index = 0; index + 1 < values.size(); ++index) {
            if (values[index] == "text" || values[index] == "Text") {
                records_.push_back({values[index + 1], 0});
                ++index;
            }
        }
    } else {
        for (std::size_t index = 1; index < values.size(); ++index) {
            records_.push_back({values[index], 0});
        }
    }
    if (records_.size() > 200) records_.resize(200);
}

bool HistoryStore::add(std::string text, std::uint32_t retention_days) noexcept {
    if (text.empty()) return true;
    records_.insert(records_.begin(), {std::move(text), now_seconds()});
    if (records_.size() > 200) records_.resize(200);
    const std::int64_t cutoff = now_seconds() - static_cast<std::int64_t>(retention_days) * 86400;
    records_.erase(std::remove_if(records_.begin(), records_.end(), [cutoff](const HistoryRecord& record) {
                      return record.epoch_seconds != 0 && record.epoch_seconds < cutoff;
                  }),
                  records_.end());
    std::ostringstream json;
    json << "{\n  \"items\": [\n";
    for (std::size_t index = 0; index < records_.size(); ++index) {
        json << "    \"" << escape_json(records_[index].text) << "\"" << (index + 1 < records_.size() ? "," : "") << "\n";
    }
    json << "  ]\n}\n";
    return Storage::atomic_write(Storage::data_directory() / L"history.json", json.str());
}

bool HistoryStore::clear() noexcept {
    records_.clear();
    return Storage::atomic_write(Storage::data_directory() / L"history.json", "{\"items\": []}\n");
}

void TermDictionaryStore::load() {
    terms_.clear();
    const auto values = json_strings(Storage::read_text(Storage::data_directory() / L"terms.json"));
    for (std::size_t index = 0; index + 3 < values.size(); ++index) {
        if (values[index] == "source" && values[index + 2] == "replacement") {
            terms_.push_back({values[index + 1], values[index + 3]});
            index += 3;
        }
    }
}

bool TermDictionaryStore::learn(core::TermRule rule) noexcept {
    if (rule.source.empty() || rule.replacement.empty()) {
        return false;
    }
    const auto existing = std::find_if(terms_.begin(), terms_.end(),
                                       [&rule](const core::TermRule& term) { return term.source == rule.source; });
    if (existing != terms_.end()) {
        existing->replacement = std::move(rule.replacement);
    } else {
        terms_.push_back(std::move(rule));
    }
    return save(terms_);
}

bool TermDictionaryStore::save(const std::vector<core::TermRule>& terms) const noexcept {
    std::ostringstream json;
    json << "{\n  \"items\": [\n";
    for (std::size_t index = 0; index < terms.size(); ++index) {
        json << "    {\"source\": \"" << escape_json(terms[index].source) << "\", \"replacement\": \""
             << escape_json(terms[index].replacement) << "\"}" << (index + 1 < terms.size() ? "," : "") << "\n";
    }
    json << "  ]\n}\n";
    return Storage::atomic_write(Storage::data_directory() / L"terms.json", json.str());
}

}  // namespace aevocis::platform::windows
