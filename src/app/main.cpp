#include "aevocis/core/audio_gate.hpp"
#include "aevocis/core/chunked_recognition.hpp"
#include "aevocis/core/noise_gate.hpp"
#include "aevocis/core/recognizer.hpp"
#include "aevocis/core/task_scheduler.hpp"
#include "aevocis/core/text_pipeline.hpp"
#include "aevocis/core/voice.hpp"
#include "aevocis/platform/windows/app_log.hpp"
#include "aevocis/platform/windows/global_hotkey.hpp"
#include "aevocis/platform/windows/crash_reporter.hpp"
#include "aevocis/platform/windows/external_pipeline.hpp"
#include "aevocis/platform/windows/history_export.hpp"
#include "aevocis/platform/windows/command_pipe.hpp"
#include "aevocis/platform/windows/autostart.hpp"
#include "aevocis/platform/windows/keyboard_hook.hpp"
#include "aevocis/platform/windows/messages.hpp"
#include "aevocis/platform/windows/single_instance.hpp"
#include "aevocis/platform/windows/sensevoice_recognizer.hpp"
#include "aevocis/platform/windows/storage.hpp"
#include "aevocis/platform/windows/target_window.hpp"
#include "aevocis/platform/windows/text_injector.hpp"
#include "aevocis/platform/windows/tray_icon.hpp"
#include "aevocis/platform/windows/update_manager.hpp"
#include "aevocis/platform/windows/wasapi_recorder.hpp"
#include "aevocis/version.hpp"
#include "aevocis/ui/command_palette.hpp"
#include "aevocis/ui/main_window.hpp"
#include "aevocis/ui/recording_overlay.hpp"

#include <windows.h>

#include <condition_variable>
#include <chrono>
#include <ctime>
#include <deque>
#include <filesystem>
#include <atomic>
#include <cwchar>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>
#include <shellapi.h>

namespace {

using namespace aevocis;
using platform::windows::GlobalHotkey;
using platform::windows::KeyboardHook;
using platform::windows::SingleInstance;
using platform::windows::TargetWindowToken;
using platform::windows::TextInjector;
using platform::windows::TrayIcon;
using platform::windows::WasapiRecorder;

constexpr int kShowHideHotkeyId = 1;
constexpr int kUndoHotkeyId = 2;
constexpr int kCommandPaletteHotkeyId = 3;
// B3: idle-unload watchdog. Checked every minute; the model is released after 10 consecutive
// idle minutes, trading a one-time reload latency on the next dictation for not holding the
// model's working set in memory during long idle stretches -- the counterpart the app's
// existing "load lazily, never at startup" choice was missing.
constexpr UINT_PTR kIdleTimerId = 1;
constexpr UINT kIdleTimerIntervalMs = 60'000;
constexpr ULONGLONG kIdleUnloadThresholdMs = 10ULL * 60ULL * 1000ULL;

[[nodiscard]] std::string uppercase_utf8(std::string_view value) {
    if (value.empty()) {
        return {};
    }
    const std::string input(value);
    const int source_length = static_cast<int>(input.size());
    const int wide_length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), source_length, nullptr, 0);
    if (wide_length <= 0) {
        return {};
    }
    std::wstring wide(static_cast<std::size_t>(wide_length), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), source_length, wide.data(), wide_length) != wide_length) {
        return {};
    }
    (void)CharUpperBuffW(wide.data(), static_cast<DWORD>(wide.size()));
    const int utf8_length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(), static_cast<int>(wide.size()), nullptr, 0,
                                                nullptr, nullptr);
    if (utf8_length <= 0) {
        return {};
    }
    std::string result(static_cast<std::size_t>(utf8_length), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(), static_cast<int>(wide.size()), result.data(), utf8_length,
                            nullptr, nullptr) != utf8_length) {
        return {};
    }
    return result;
}

class Application {
public:
    explicit Application(HINSTANCE instance, ULONGLONG process_start_tick)
        : instance_(instance), instance_guard_(L"Local\\AevocisNativeCppSingleton"), settings_(), window_(instance), overlay_(instance),
          command_palette_(instance), process_start_tick_(process_start_tick) {
        settings_ = settings_store_.load();
        toggle_mode_.store(settings_.toggle_mode);
        history_store_.load();
        terms_store_.load();
    }

    ~Application() {
        if (icon_owned_ && icon_ != nullptr) {
            (void)DestroyIcon(icon_);
        }
    }

    int run() {
        icon_ = load_icon();
        window_.set_icon(icon_);
        if (!instance_guard_.primary() || !window_.create()) {
            return 0;
        }
        window_.set_message_handler([this](UINT message, WPARAM wparam, LPARAM lparam) {
            return handle_message(message, wparam, lparam);
        });
        // B5: M01's real, not-fabricated, first-frame measurement -- logged once via AppLog
        // (stage/number only, no user content, consistent with E5's redaction rule) rather than
        // just asserted against APP_METRICS.md's "<300ms" target with no evidence behind it.
        window_.set_first_paint_handler([this] {
            const ULONGLONG elapsed_ms = GetTickCount64() - process_start_tick_;
            platform::windows::AppLog::record_metric("first_paint_ms", elapsed_ms);
        });
        window_.set_theme_handler([this] {
            settings_.theme = settings_.theme == 0 ? 1 : 0;
            (void)settings_store_.save(settings_);
            overlay_.set_theme(window_.theme());
        });
        window_.set_trigger_mode_handler([this] {
            const bool toggle = !toggle_mode_.load();
            toggle_mode_.store(toggle);
            settings_.toggle_mode = toggle;
            (void)settings_store_.save(settings_);
        });
        window_.set_history_clear_handler([this] {
            std::scoped_lock lock(history_mutex_);
            if (history_store_.clear()) {
                window_.clear_history();
            }
        });
        window_.set_theme(settings_.theme == 1 ? ui::ThemeMode::DarkGlass : ui::ThemeMode::Paper);
        window_.set_trigger_mode(settings_.toggle_mode);
        for (const auto& record : history_store_.records()) {
            window_.add_history(record.text, record.epoch_seconds);
        }
        window_.set_stats(compute_stats());
        (void)tray_.install(window_.handle(), platform::windows::kTrayMessage, icon_);
        (void)overlay_.create();
        overlay_.set_theme(window_.theme());
        (void)command_server_.start([this](std::string command) { return handle_command_request(std::move(command)); });
        (void)show_hide_hotkey_.register_hotkey(window_.handle(), kShowHideHotkeyId, settings_.show_hide_modifiers,
                                                 settings_.show_hide_virtual_key);
        // C4: dedicated undo-last-injection hotkey, separate from the voice "撤销" command so
        // it works instantly without speaking. Fixed combo for now (not yet in the settings
        // rebind UI); registration failure (e.g. another app already owns it) degrades to
        // "voice undo still works", never a crash.
        (void)undo_hotkey_.register_hotkey(window_.handle(), kUndoHotkeyId, MOD_CONTROL | MOD_ALT, 'Z');
        // C3: Ctrl+Shift+P by the now-common editor convention for "command palette".
        (void)command_palette_hotkey_.register_hotkey(window_.handle(), kCommandPaletteHotkeyId, MOD_CONTROL | MOD_SHIFT, 'P');
        command_palette_.set_commands(build_palette_commands());
        last_activity_tick_ = GetTickCount64();
        (void)SetTimer(window_.handle(), kIdleTimerId, kIdleTimerIntervalMs, nullptr);
        (void)keyboard_hook_.install(window_.handle(), platform::windows::kKeyboardMessage);
        window_.show();
        MSG message{};
        while (GetMessageW(&message, nullptr, 0, 0) > 0) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        scheduler_.wait();
        return static_cast<int>(message.wParam);
    }

private:
    bool handle_message(UINT message, WPARAM wparam, LPARAM lparam) {
        if (message == WM_HOTKEY && static_cast<int>(wparam) == kShowHideHotkeyId) {
            window_.show_or_hide();
            return true;
        }
        if (message == WM_HOTKEY && static_cast<int>(wparam) == kUndoHotkeyId) {
            request_undo();
            return true;
        }
        if (message == WM_HOTKEY && static_cast<int>(wparam) == kCommandPaletteHotkeyId) {
            command_palette_.toggle(window_.handle());
            return true;
        }
        if (message == WM_TIMER && wparam == kIdleTimerId) {
            if (!scheduler_.active() && recognizer_.ready() &&
                GetTickCount64() - last_activity_tick_ >= kIdleUnloadThresholdMs) {
                recognizer_.unload();
            }
            return true;
        }
        if (message == platform::windows::kKeyboardMessage) {
            handle_keyboard(static_cast<UINT>(wparam), lparam != 0);
            return true;
        }
        if (message == platform::windows::kStatusMessage) {
            const auto state = static_cast<core::AppState>(wparam);
            const auto error = static_cast<core::ErrorCode>(lparam);
            if (error == core::ErrorCode::None) {
                window_.set_state(state);
            } else {
                window_.set_error(error);
            }
            overlay_.set_state(state);
            return true;
        }
        if (message == platform::windows::kUpdateQuitMessage) {
            PostQuitMessage(0);
            return true;
        }
        if (message == platform::windows::kPartialTextMessage) {
            std::wstring text;
            {
                std::scoped_lock lock(partial_mutex_);
                text = pending_partial_text_;
            }
            overlay_.set_partial_text(std::move(text));
            return true;
        }
        if (message == platform::windows::kHistoryMessage) {
            std::vector<platform::windows::HistoryRecord> fresh;
            {
                std::scoped_lock lock(history_mutex_);
                const auto& records = history_store_.records();
                const std::size_t count = pending_history_count_ > records.size() ? records.size() : pending_history_count_;
                fresh.assign(records.begin(), records.begin() + static_cast<std::ptrdiff_t>(count));
                pending_history_count_ = 0;
            }
            // fresh is newest-first; walk it oldest-of-the-batch-to-newest so MainWindow's own
            // front-insert ends up in the same newest-first order.
            for (auto it = fresh.rbegin(); it != fresh.rend(); ++it) {
                window_.add_history(it->text, it->epoch_seconds);
            }
            window_.set_stats(compute_stats());
            return true;
        }
        if (message == platform::windows::kCommandMessage) {
            std::deque<std::string> commands;
            {
                std::scoped_lock lock(command_mutex_);
                commands.swap(pending_commands_);
            }
            for (auto& command : commands) {
                if (command == "show") {
                    window_.show();
                } else if (command == "theme") {
                    window_.toggle_theme();
                } else if (command.rfind("inject:", 0) == 0) {
                    schedule_injection(command.substr(7));
                }
            }
            return true;
        }
        if (message == platform::windows::kTrayMessage) {
            const auto event = static_cast<UINT>(lparam);
            if (event == WM_LBUTTONUP) {
                window_.show_or_hide();
            } else if (event == WM_RBUTTONUP) {
                tray_.show_menu();
            }
            return true;
        }
        if (message == WM_COMMAND) {
            switch (static_cast<UINT>(wparam)) {
            case platform::windows::kCommandToggle:
                window_.show_or_hide();
                return true;
            case platform::windows::kCommandTheme:
                window_.toggle_theme();
                return true;
            case platform::windows::kCommandSettings:
                window_.open_settings();
                window_.show();
                return true;
            case platform::windows::kCommandAutostart: {
                const bool enabled = platform::windows::Autostart::enabled();
                const bool updated = platform::windows::Autostart::set_enabled(!enabled, executable_path());
                if (updated) {
                    settings_.autostart = !enabled;
                    (void)settings_store_.save(settings_);
                }
                return true;
            }
            case platform::windows::kCommandUpdate:
                platform::windows::UpdateManager::check_and_install_async(window_.handle(), std::wstring(kVersion), executable_path());
                return true;
            case platform::windows::kCommandQuit:
                PostQuitMessage(0);
                return true;
            default:
                break;
            }
        }
        return false;
    }

    // True if any modifier other than the push-to-talk key itself is currently down.
    // Used to reject a push-to-talk trigger that arrives as part of a chord (Ctrl+Alt,
    // Ctrl+Shift, AltGr's synthetic Ctrl+Alt, etc.) so only a standalone right-Ctrl press
    // ever starts recording.
    [[nodiscard]] static bool is_other_modifier_held() noexcept {
        return (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0 ||
               (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0 || (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ||
               (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0 || (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0 ||
               (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0 ||
               (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;
    }

    void handle_keyboard(UINT virtual_key, bool down) {
        if (virtual_key != settings_.push_to_talk_virtual_key) {
            return;
        }
        {
            std::scoped_lock lock(input_mutex_);
            key_down_ = down;
            if (!down) {
                input_cv_.notify_all();
            }
        }
        if (down) {
            if (toggle_mode_.load() && scheduler_.active()) {
                std::scoped_lock lock(input_mutex_);
                toggle_stop_ = true;
                input_cv_.notify_all();
                return;
            }
            if (!toggle_mode_.load() && scheduler_.active()) {
                return;
            }
            // Only a bare push-to-talk key press (right Ctrl alone) may start a new
            // recording. Windows synthesizes a Ctrl-down event as part of AltGr (and any
            // other Ctrl+<modifier> chord also reports this same vkCode on down), so without
            // this guard holding Alt/Shift/Win/left-Ctrl together with right Ctrl -- or an
            // AltGr press on non-US keyboard layouts -- would incorrectly start recording.
            if (is_other_modifier_held()) {
                return;
            }
            const TargetWindowToken target = TargetWindowToken::capture();
            if (!target.valid()) {
                window_.set_error(core::ErrorCode::TargetChanged);
                return;
            }
            {
                std::scoped_lock lock(input_mutex_);
                toggle_stop_ = false;
                target_ = target;
            }
            if (!scheduler_.submit(target.core_token(), [this, target](std::stop_token stop, core::SessionId id) {
                    run_session(stop, id, target);
                })) {
                window_.set_error(core::ErrorCode::Busy);
            }
        }
    }

    void run_session(std::stop_token stop, core::SessionId id, TargetWindowToken target) {
        last_activity_tick_ = GetTickCount64();
        post_state(core::AppState::Starting);
        if (!recorder_.start()) {
            (void)scheduler_.transition(id, core::AppState::Failed, core::ErrorCode::AudioUnavailable);
            post_error(core::ErrorCode::AudioUnavailable);
            return;
        }
        (void)scheduler_.transition(id, core::AppState::Capturing);
        post_state(core::AppState::Capturing);
        {
            partial_offset_ = 0;
            partial_transcript_.clear();
            const auto stop_requested = [this, &stop] {
                return stop.stop_requested() || (toggle_mode_.load() ? toggle_stop_ : !key_down_);
            };
            for (;;) {
                std::unique_lock lock(input_mutex_);
                const bool done = input_cv_.wait_for(lock, std::chrono::milliseconds(1200), stop_requested);
                lock.unlock();
                if (done) {
                    break;
                }
                emit_partial_tick(stop);
            }
        }
        platform::windows::RecordedAudio audio = recorder_.stop();
        if (stop.stop_requested()) {
            (void)scheduler_.transition(id, core::AppState::Cancelled, core::ErrorCode::Cancelled);
            post_state(core::AppState::Cancelled);
            return;
        }
        // A4: high-pass + adaptive noise-floor gate runs before anything else sees the buffer,
        // so both the A2 silence check and the recognizer itself get the cleaned signal.
        if (audio.sample_rate != 0) {
            core::NoiseGate(audio.sample_rate).process(audio.samples);
        }
        // A2: silence/too-short gate -- runs before the recognizer ever sees the buffer, so a
        // mistrigger or near-silent capture can never produce hallucinated model output. This is
        // a quiet no-op (Idle), not a Failed transition, since nothing actually went wrong.
        if (!core::AudioGate::should_recognize(audio.samples, audio.sample_rate)) {
            (void)scheduler_.transition(id, core::AppState::Idle);
            post_state(core::AppState::Idle);
            return;
        }
        (void)scheduler_.transition(id, core::AppState::Recognizing);
        post_state(core::AppState::Recognizing);
        if (!recognizer_.ready() && !recognizer_.load(model_directory())) {
            (void)scheduler_.transition(id, core::AppState::Failed, core::ErrorCode::RecognitionUnavailable);
            post_error(core::ErrorCode::RecognitionUnavailable);
            return;
        }
        // A7: transparently windows+stitches long recordings; behaves exactly like a direct
        // recognizer_.recognize() call for anything under one window, so short dictations are
        // unaffected.
        const core::RecognitionResult result = core::ChunkedRecognizer::recognize(recognizer_, audio.samples, audio.sample_rate, stop);
        if (!result.ok()) {
            (void)scheduler_.transition(id, core::AppState::Failed, result.error);
            post_error(result.error);
            return;
        }
        (void)scheduler_.transition(id, core::AppState::PostProcessing);
        post_state(core::AppState::PostProcessing);
        core::TextPipelineOptions options;
        options.append_sentence_punctuation = settings_.punctuation;
        auto processed = core::TextPipeline::process(result.text, terms_store_.terms(), options);
        // F1: opt-in external post-processing hook. Runs after the built-in pipeline (term
        // rules, punctuation, casing) so the external tool sees the already-cleaned text, not
        // the raw recognizer output. A broken/slow/missing tool degrades to "text unchanged" --
        // never blocks or corrupts the dictation.
        if (!settings_.external_pipeline_path.empty()) {
            if (const auto piped = platform::windows::ExternalPipeline::run(settings_.external_pipeline_path, processed.text);
                piped.has_value() && !piped->empty()) {
                processed.text = *piped;
            }
        }
        if (processed.text.empty() || !target.still_valid()) {
            (void)scheduler_.transition(id, core::AppState::Failed, core::ErrorCode::TargetChanged);
            post_error(core::ErrorCode::TargetChanged);
            return;
        }
        // A6: "记住 A 读作 B" is handled before the fixed voice-command list -- it teaches a
        // term rule instead of injecting text, and never touches history (nothing was dictated).
        if (const auto learned = core::VoiceCommandMatcher::match_learn_term(processed.text); learned.has_value()) {
            (void)terms_store_.learn({learned->source, learned->replacement});
            (void)scheduler_.transition(id, core::AppState::Idle);
            post_state(core::AppState::Idle);
            return;
        }
        if (const auto command = core::VoiceCommandMatcher::match(processed.text, voice_commands_); command.has_value()) {
            run_voice_command(stop, id, target, *command, options);
            return;
        }
        (void)scheduler_.transition(id, core::AppState::Injecting);
        post_state(core::AppState::Injecting);
        if (!injector_.inject(target, processed.text)) {
            (void)scheduler_.transition(id, core::AppState::Failed, core::ErrorCode::InjectionFailed);
            post_error(core::ErrorCode::InjectionFailed);
            return;
        }
        remember_injection(target, processed.text);
        queue_history(processed.text);
        (void)scheduler_.transition(id, core::AppState::Idle);
        post_state(core::AppState::Idle);
    }

    void run_voice_command(std::stop_token stop, core::SessionId id, const TargetWindowToken& target,
                           const core::CommandMatch& command, const core::TextPipelineOptions& options) {
        if (stop.stop_requested()) {
            (void)scheduler_.transition(id, core::AppState::Cancelled, core::ErrorCode::Cancelled);
            post_state(core::AppState::Cancelled);
            return;
        }
        if (command.action == core::VoiceCommandAction::Cancel) {
            TargetWindowToken previous_target;
            std::size_t previous_length = 0;
            {
                std::scoped_lock lock(last_injection_mutex_);
                previous_target = last_injection_target_;
                previous_length = last_injected_length_;
                last_injected_length_ = 0;
            }
            const bool undone = previous_length == 0 ||
                                (previous_target.still_valid() && injector_.send_virtual_key(previous_target, VK_BACK, previous_length));
            (void)scheduler_.transition(id, undone ? core::AppState::Cancelled : core::AppState::Failed,
                                        undone ? core::ErrorCode::Cancelled : core::ErrorCode::InjectionFailed);
            if (undone) {
                post_state(core::AppState::Cancelled);
                post_state(core::AppState::Idle);
            } else {
                post_error(core::ErrorCode::InjectionFailed);
            }
            return;
        }
        (void)scheduler_.transition(id, core::AppState::Injecting);
        post_state(core::AppState::Injecting);
        if (command.action == core::VoiceCommandAction::SendEnter) {
            if (injector_.send_virtual_key(target, VK_RETURN)) {
                remember_injection(target, "\n");
                (void)scheduler_.transition(id, core::AppState::Idle);
                post_state(core::AppState::Idle);
            } else {
                (void)scheduler_.transition(id, core::AppState::Failed, core::ErrorCode::InjectionFailed);
                post_error(core::ErrorCode::InjectionFailed);
            }
            return;
        }
        const auto remainder = core::TextPipeline::process(command.remaining_text, terms_store_.terms(), options);
        const std::string upper = uppercase_utf8(remainder.text);
        if (upper.empty() || !injector_.inject(target, upper)) {
            (void)scheduler_.transition(id, core::AppState::Failed, core::ErrorCode::InjectionFailed);
            post_error(core::ErrorCode::InjectionFailed);
            return;
        }
        remember_injection(target, upper);
        queue_history(upper);
        (void)scheduler_.transition(id, core::AppState::Idle);
        post_state(core::AppState::Idle);
    }

    void remember_injection(const TargetWindowToken& target, std::string_view text) {
        std::scoped_lock lock(last_injection_mutex_);
        last_injection_target_ = target;
        last_injected_length_ = TextInjector::utf16_length(text);
    }

    // Live-preview pass: runs on the same worker thread as run_session(), between wait_for()
    // wake-ups, so it never races the final full-buffer recognition that happens after the loop
    // exits. Only previews once the model is already warm (recognizer_.ready()) -- forcing a
    // multi-second cold load just to preview would stall the very recording it's trying to show.
    // Each tick decodes only the audio captured since the last tick (bounded, constant-ish cost
    // regardless of how long the dictation has run so far) and appends it to a running caption;
    // this text is advisory only -- it never feeds the authoritative recognition/injection path.
    void emit_partial_tick(std::stop_token stop) {
        if (stop.stop_requested() || !recognizer_.ready()) {
            return;
        }
        const std::uint32_t rate = recorder_.current_sample_rate();
        if (rate == 0) {
            return;
        }
        const std::size_t total = recorder_.sample_count();
        if (total <= partial_offset_) {
            return;
        }
        const double new_seconds = static_cast<double>(total - partial_offset_) / static_cast<double>(rate);
        if (new_seconds < 0.9) {
            return;
        }
        std::vector<float> segment = recorder_.copy_since(partial_offset_);
        partial_offset_ = total;
        if (segment.empty() || !core::AudioGate::should_recognize(segment, rate)) {
            return;
        }
        const core::RecognitionResult result = recognizer_.recognize(segment, rate, stop);
        if (!result.ok() || result.text.empty()) {
            return;
        }
        const int length = MultiByteToWideChar(CP_UTF8, 0, result.text.data(), static_cast<int>(result.text.size()), nullptr, 0);
        if (length <= 0) {
            return;
        }
        std::wstring wide(static_cast<std::size_t>(length), L'\0');
        (void)MultiByteToWideChar(CP_UTF8, 0, result.text.data(), static_cast<int>(result.text.size()), wide.data(), length);
        if (!partial_transcript_.empty()) {
            partial_transcript_ += L' ';
        }
        partial_transcript_ += wide;
        {
            std::scoped_lock lock(partial_mutex_);
            pending_partial_text_ = partial_transcript_;
        }
        (void)PostMessageW(window_.handle(), platform::windows::kPartialTextMessage, 0, 0);
    }

    void post_state(core::AppState state) const noexcept {
        platform::windows::AppLog::record_state(state);
        (void)PostMessageW(window_.handle(), platform::windows::kStatusMessage, static_cast<WPARAM>(state),
                            static_cast<LPARAM>(core::ErrorCode::None));
    }

    void post_error(core::ErrorCode error) const noexcept {
        platform::windows::AppLog::record_error(core::AppState::Failed, error);
        (void)PostMessageW(window_.handle(), platform::windows::kStatusMessage, static_cast<WPARAM>(core::AppState::Failed),
                            static_cast<LPARAM>(error));
    }

    void queue_history(std::string text) {
        std::scoped_lock lock(history_mutex_);
        ++pending_history_count_;
        (void)history_store_.add(std::move(text), settings_.history_retention_days);
        (void)PostMessageW(window_.handle(), platform::windows::kHistoryMessage, 0, 0);
    }

    // C4: same VK_BACK-loop undo the voice "撤销" command already used, reachable directly by
    // hotkey. Declines (silently, matching schedule_injection's existing busy behavior) while a
    // session is active, since undoing mid-recognition would race the in-flight injection.
    void request_undo() {
        if (scheduler_.active()) {
            return;
        }
        TargetWindowToken previous_target;
        std::size_t previous_length = 0;
        {
            std::scoped_lock lock(last_injection_mutex_);
            previous_target = last_injection_target_;
            previous_length = last_injected_length_;
            last_injected_length_ = 0;
        }
        if (previous_length == 0 || !previous_target.valid()) {
            return;
        }
        if (!scheduler_.submit(previous_target.core_token(), [this, previous_target, previous_length](std::stop_token, core::SessionId id) {
                const bool undone = previous_target.still_valid() && injector_.send_virtual_key(previous_target, VK_BACK, previous_length);
                (void)scheduler_.transition(id, undone ? core::AppState::Idle : core::AppState::Failed,
                                            undone ? core::ErrorCode::None : core::ErrorCode::InjectionFailed);
                if (undone) {
                    post_state(core::AppState::Idle);
                } else {
                    post_error(core::ErrorCode::InjectionFailed);
                }
            })) {
            std::scoped_lock lock(last_injection_mutex_);
            last_injected_length_ = previous_length;
        }
    }

    void schedule_injection(std::string text) {
        if (text.empty() || scheduler_.active()) {
            return;
        }
        const TargetWindowToken target = TargetWindowToken::capture();
        if (!target.valid()) {
            post_error(core::ErrorCode::TargetChanged);
            return;
        }
        if (!scheduler_.submit(target.core_token(), [this, target, text = std::move(text)](std::stop_token stop, core::SessionId id) {
                post_state(core::AppState::Injecting);
                if (stop.stop_requested() || !injector_.inject(target, text)) {
                    (void)scheduler_.transition(id, core::AppState::Failed, core::ErrorCode::InjectionFailed);
                    post_error(core::ErrorCode::InjectionFailed);
                    return;
                }
                queue_history(text);
                (void)scheduler_.transition(id, core::AppState::Idle);
                post_state(core::AppState::Idle);
            })) {
            post_error(core::ErrorCode::Busy);
        }
    }

    [[nodiscard]] std::string handle_command_request(std::string command) {
        if (command == "status") {
            return "{\"running\":true}\n";
        }
        // F3: "export:<format>:<absolute-path>" -- handled synchronously here (a pure read of
        // history plus a file write, no UI/injection involved) rather than routed through the
        // UI-thread command queue like inject/show/theme are.
        if (command.rfind("export:", 0) == 0) {
            const std::string_view payload(command);
            const auto first_colon = payload.find(':', 7);
            if (first_colon == std::string_view::npos) {
                return "{\"accepted\":false,\"error\":\"malformed_export\"}\n";
            }
            const auto format = platform::windows::HistoryExporter::parse_format(payload.substr(7, first_colon - 7));
            if (!format.has_value()) {
                return "{\"accepted\":false,\"error\":\"unknown_format\"}\n";
            }
            const std::filesystem::path target(command.substr(first_colon + 1));
            std::vector<platform::windows::HistoryRecord> snapshot;
            {
                std::scoped_lock lock(history_mutex_);
                snapshot = history_store_.records();
            }
            const bool wrote = platform::windows::HistoryExporter::export_to(*format, target, snapshot);
            return wrote ? "{\"accepted\":true}\n" : "{\"accepted\":false,\"error\":\"write_failed\"}\n";
        }
        if (command != "show" && command != "theme" && command.rfind("inject:", 0) != 0) {
            return "{\"accepted\":false,\"error\":\"unknown_command\"}\n";
        }
        if (command.rfind("inject:", 0) == 0 && scheduler_.active()) {
            return "{\"accepted\":false,\"error\":\"busy\"}\n";
        }
        {
            std::scoped_lock lock(command_mutex_);
            pending_commands_.push_back(std::move(command));
        }
        (void)PostMessageW(window_.handle(), platform::windows::kCommandMessage, 0, 0);
        return "{\"accepted\":true}\n";
    }

    // C5: dictations_today only counts records carrying a real epoch_seconds (added during a
    // process run since history persistence, by design, has never stored timestamps to disk --
    // see storage.cpp) -- an honest undercount across a restart on the same calendar day rather
    // than a fabricated number. Totals and character counts are exact regardless.
    [[nodiscard]] ui::SessionStats compute_stats() const {
        ui::SessionStats stats;
        const auto midnight = [] {
            const auto now = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
            tm local{};
            localtime_s(&local, &now);
            local.tm_hour = 0;
            local.tm_min = 0;
            local.tm_sec = 0;
            return static_cast<std::int64_t>(_mktime64(&local));
        }();
        for (const auto& record : history_store_.records()) {
            stats.dictations_total += 1;
            stats.characters_total += record.text.size();
            if (record.epoch_seconds != 0 && record.epoch_seconds >= midnight) {
                stats.dictations_today += 1;
            }
        }
        return stats;
    }

    // C3: every entry here just calls something already reachable via tray menu / hotkey /
    // WM_COMMAND -- the palette is a faster way to reach existing actions, not a new surface of
    // its own logic.
    [[nodiscard]] std::vector<ui::PaletteCommand> build_palette_commands() {
        std::vector<ui::PaletteCommand> commands;
        commands.push_back({L"显示 / 隐藏窗口", [this] { window_.show_or_hide(); }});
        commands.push_back({L"切换主题", [this] { window_.toggle_theme(); }});
        commands.push_back({L"打开设置", [this] {
                                window_.open_settings();
                                window_.show();
                            }});
        commands.push_back({L"撤销上次输入", [this] { request_undo(); }});
        commands.push_back({L"清空历史记录", [this] {
                                std::scoped_lock lock(history_mutex_);
                                if (history_store_.clear()) {
                                    window_.clear_history();
                                }
                            }});
        commands.push_back({L"检查更新", [this] {
                                platform::windows::UpdateManager::check_and_install_async(window_.handle(), std::wstring(kVersion),
                                                                                          executable_path());
                            }});
        commands.push_back({L"切换开机自启", [this] {
                                const bool enabled = platform::windows::Autostart::enabled();
                                const bool updated = platform::windows::Autostart::set_enabled(!enabled, executable_path());
                                if (updated) {
                                    settings_.autostart = !enabled;
                                    (void)settings_store_.save(settings_);
                                }
                            }});
        commands.push_back({L"退出 Aevocis", [] { PostQuitMessage(0); }});
        return commands;
    }

    [[nodiscard]] std::string model_directory() const {
        const auto executable = executable_path();
        if (executable.empty()) {
            return {};
        }
        return (std::filesystem::path(executable).parent_path() / L"Models" / L"sensevoice").string();
    }

    [[nodiscard]] std::wstring executable_path() const {
        wchar_t executable[MAX_PATH]{};
        const DWORD length = GetModuleFileNameW(instance_, executable, ARRAYSIZE(executable));
        if (length == 0 || length >= ARRAYSIZE(executable)) {
            return {};
        }
        return std::filesystem::path(executable).wstring();
    }

    [[nodiscard]] HICON load_icon() noexcept {
        const std::wstring executable = executable_path();
        if (!executable.empty()) {
            const auto path = std::filesystem::path(executable).parent_path() / L"Aevocis.ico";
            HICON icon = static_cast<HICON>(LoadImageW(nullptr, path.c_str(), IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE));
            if (icon != nullptr) {
                icon_owned_ = true;
                return icon;
            }
        }
        return LoadIconW(nullptr, IDI_APPLICATION);
    }

    HINSTANCE instance_{};
    HICON icon_{};
    bool icon_owned_{false};
    SingleInstance instance_guard_;
    platform::windows::SettingsStore settings_store_;
    platform::windows::AppSettings settings_;
    platform::windows::HistoryStore history_store_;
    platform::windows::TermDictionaryStore terms_store_;
    ui::MainWindow window_;
    ui::RecordingOverlay overlay_;
    TrayIcon tray_;
    GlobalHotkey show_hide_hotkey_;
    GlobalHotkey undo_hotkey_;
    GlobalHotkey command_palette_hotkey_;
    ui::CommandPalette command_palette_;
    // Written from both the UI thread (run()/handle_message) and scheduler worker threads
    // (run_session) -- atomic rather than a plain ULONGLONG to avoid a real data race, not just
    // a theoretical one, since x64's natural word-tearing-free store isn't a language guarantee.
    std::atomic<ULONGLONG> last_activity_tick_{0};
    ULONGLONG process_start_tick_{0};
    KeyboardHook keyboard_hook_;
    core::SingleTaskScheduler scheduler_;
    WasapiRecorder recorder_;
    platform::windows::SenseVoiceRecognizer recognizer_;
    std::vector<core::VoiceCommand> voice_commands_ = core::VoiceCommandMatcher::defaults();
    platform::windows::CommandPipeServer command_server_;
    TextInjector injector_;
    std::mutex input_mutex_;
    std::condition_variable input_cv_;
    mutable std::mutex history_mutex_;
    std::size_t pending_history_count_{0};
    std::size_t partial_offset_{0};
    std::wstring partial_transcript_;
    std::mutex partial_mutex_;
    std::wstring pending_partial_text_;
    std::mutex command_mutex_;
    std::deque<std::string> pending_commands_;
    TargetWindowToken target_{};
    bool key_down_{false};
    bool toggle_stop_{false};
    std::atomic_bool toggle_mode_{false};
    std::mutex last_injection_mutex_;
    TargetWindowToken last_injection_target_{};
    std::size_t last_injected_length_{0};
};

}  // namespace

namespace {

[[nodiscard]] std::string utf8_from_wide(const wchar_t* value) {
    if (value == nullptr || *value == L'\0') return {};
    const int source_length = static_cast<int>(wcslen(value));
    const int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, source_length, nullptr, 0, nullptr, nullptr);
    if (length <= 0) return {};
    std::string result(static_cast<std::size_t>(length), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, source_length, result.data(), length, nullptr, nullptr) != length) return {};
    return result;
}

int run_command_line(PWSTR command_line) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(command_line, &argc);
    if (argv == nullptr || argc < 2) {
        if (argv != nullptr) LocalFree(static_cast<HLOCAL>(argv));
        return -1;
    }
    std::string command;
    if (std::wstring_view(argv[1]) == L"--status") {
        command = "status";
    } else if (std::wstring_view(argv[1]) == L"--show") {
        command = "show";
    } else if (std::wstring_view(argv[1]) == L"--theme") {
        command = "theme";
    } else if (std::wstring_view(argv[1]) == L"--inject" && argc >= 3) {
        command = "inject:" + utf8_from_wide(argv[2]);
    } else if (std::wstring_view(argv[1]) == L"--export" && argc >= 4) {
        command = "export:" + utf8_from_wide(argv[2]) + ":" + utf8_from_wide(argv[3]);
    } else {
        LocalFree(static_cast<HLOCAL>(argv));
        return 2;
    }
    const std::string response = aevocis::platform::windows::CommandPipeClient::request(command);
    LocalFree(static_cast<HLOCAL>(argv));
    const auto write_output = [](std::string_view value) {
        HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
        if (output == nullptr || output == INVALID_HANDLE_VALUE) {
            (void)AttachConsole(ATTACH_PARENT_PROCESS);
            output = GetStdHandle(STD_OUTPUT_HANDLE);
        }
        if (output != nullptr && output != INVALID_HANDLE_VALUE) {
            DWORD written = 0;
            (void)WriteFile(output, value.data(), static_cast<DWORD>(value.size()), &written, nullptr);
        }
    };
    if (response.empty()) {
        const char unavailable[] = "{\"running\":false}\n";
        write_output(unavailable);
        return command == "status" ? 0 : 1;
    }
    write_output(response);
    return response.find("\"accepted\":true") != std::string::npos || response.find("\"running\":true") != std::string::npos ? 0 : 1;
}

}  // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR command_line, int) {
    const ULONGLONG process_start_tick = GetTickCount64();
    const int command_result = run_command_line(command_line);
    if (command_result >= 0) return command_result;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    aevocis::platform::windows::CrashReporter::install();
    aevocis::platform::windows::CrashReporter::register_auto_restart();
    Application application(instance, process_start_tick);
    return application.run();
}
