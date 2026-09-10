#pragma once

#include "aevocis/core/text_pipeline.hpp"

#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

#include <windows.h>

namespace aevocis::platform::windows {

struct AppSettings {
    std::uint32_t push_to_talk_virtual_key{VK_RCONTROL};
    // Side-agnostic modifier flags (MOD_CONTROL/MOD_ALT/MOD_SHIFT/MOD_WIN) that must be held
    // together with push_to_talk_virtual_key for it to start a recording. 0 -- the shipped
    // default, paired with VK_RCONTROL -- means the key triggers on its own. Deliberately the
    // same bit values RegisterHotKey uses so a push-to-talk binding can be compared directly
    // against the app's own RegisterHotKey shortcuts when checking for collisions.
    std::uint32_t push_to_talk_modifiers{0};
    std::uint32_t show_hide_modifiers{MOD_CONTROL | MOD_ALT};
    std::uint32_t show_hide_virtual_key{'H'};
    bool toggle_mode{false};
    bool punctuation{true};
    bool draft_confirmation{false};
    bool autostart{false};
    std::uint32_t history_retention_days{30};
    int theme{1};
    // F1: empty (the default) means the hook is off -- text passes through unchanged. Only
    // takes effect once the user explicitly types a path in here.
    std::wstring external_pipeline_path;
};

struct HistoryRecord {
    std::string text;
    std::int64_t epoch_seconds{};
};

class Storage {
public:
    [[nodiscard]] static std::filesystem::path data_directory();
    [[nodiscard]] static bool atomic_write(const std::filesystem::path& path, std::string_view content) noexcept;
    [[nodiscard]] static std::string read_text(const std::filesystem::path& path);
};

class SettingsStore {
public:
    [[nodiscard]] AppSettings load() const;
    [[nodiscard]] bool save(const AppSettings& settings) const noexcept;
};

class HistoryStore {
public:
    void load();
    [[nodiscard]] bool add(std::string text, std::uint32_t retention_days) noexcept;
    [[nodiscard]] bool clear() noexcept;
    [[nodiscard]] const std::vector<HistoryRecord>& records() const noexcept { return records_; }

private:
    std::vector<HistoryRecord> records_;
};

class TermDictionaryStore {
public:
    void load();
    [[nodiscard]] bool save(const std::vector<core::TermRule>& terms) const noexcept;
    [[nodiscard]] const std::vector<core::TermRule>& terms() const noexcept { return terms_; }
    // A6: appends a rule learned from a "记住 A 读作 B" utterance, replacing any existing
    // rule with the same source so re-teaching a term overwrites rather than duplicates.
    [[nodiscard]] bool learn(core::TermRule rule) noexcept;

private:
    std::vector<core::TermRule> terms_;
};

}  // namespace aevocis::platform::windows
