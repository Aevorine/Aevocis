#pragma once

#include "aevocis/core/state.hpp"
#include "aevocis/platform/windows/composition_host.hpp"
#include "aevocis/ui/style.hpp"

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include <windows.h>
#include <wrl/client.h>

struct ID2D1Factory;
struct ID2D1HwndRenderTarget;
struct ID2D1RenderTarget;
struct IDWriteFactory;
struct IDWriteTextFormat;

namespace aevocis::ui {

struct SessionStats {
    std::uint32_t dictations_today{0};
    std::uint32_t dictations_total{0};
    std::uint64_t characters_total{0};
};

struct HistoryEntry {
    std::wstring text;
    std::int64_t epoch_seconds{0};
};

class MainWindow {
public:
    using MessageHandler = std::function<bool(UINT, WPARAM, LPARAM)>;
    using Action = std::function<void()>;

    explicit MainWindow(HINSTANCE instance) noexcept;
    MainWindow(const MainWindow&) = delete;
    MainWindow& operator=(const MainWindow&) = delete;
    ~MainWindow();

    [[nodiscard]] bool create() noexcept;
    void set_icon(HICON icon) noexcept;
    [[nodiscard]] HWND handle() const noexcept { return hwnd_; }
    void set_message_handler(MessageHandler handler);
    void set_trigger_mode_handler(Action handler);
    void set_history_clear_handler(Action handler);
    void set_theme_handler(Action handler);
    void set_first_paint_handler(Action handler);
    void set_theme(ThemeMode theme) noexcept;
    [[nodiscard]] ThemeMode theme() const noexcept { return theme_; }
    void set_trigger_mode(bool toggle) noexcept;
    void show_or_hide() noexcept;
    void show() noexcept;
    void hide() noexcept;
    [[nodiscard]] bool visible() const noexcept;
    void toggle_theme() noexcept;
    void open_settings() noexcept;
    void set_state(core::AppState state) noexcept;
    void set_error(core::ErrorCode error) noexcept;
    void add_history(std::string text, std::int64_t epoch_seconds = 0);
    void clear_history() noexcept;
    void set_stats(SessionStats stats) noexcept;

private:
    struct FocusRegion {
        RECT rect;
        Action activate;
    };

    static LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    LRESULT handle_window_message(UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    void render() noexcept;
    void render_content(ID2D1RenderTarget* target) noexcept;
    void create_resources() noexcept;
    void discard_resources() noexcept;
    void create_search_edit() noexcept;
    void refresh_search() noexcept;
    void create_tooltips() noexcept;
    void apply_dark_titlebar() noexcept;
    void update_search_brush() noexcept;
    void tick_animation() noexcept;
    [[nodiscard]] std::vector<FocusRegion> build_focus_regions();
    void handle_key_down(WPARAM virtual_key) noexcept;

    HINSTANCE instance_{};
    HWND hwnd_{};
    HWND tooltip_{};
    HWND search_edit_{};
    HICON icon_{};
    MessageHandler message_handler_;
    ThemeMode theme_{ThemeMode::DarkGlass};
    core::AppState state_{core::AppState::Idle};
    core::ErrorCode error_{core::ErrorCode::None};
    bool settings_open_{false};
    bool toggle_mode_{false};
    Action trigger_mode_handler_;
    Action history_clear_handler_;
    Action theme_handler_;
    Action first_paint_handler_;
    bool first_paint_fired_{false};
    std::vector<HistoryEntry> history_;
    std::vector<std::size_t> visible_history_;
    std::wstring search_query_;
    SessionStats stats_{};
    int focus_index_{-1};
    float opacity_{1.0F};
    float target_opacity_{1.0F};
    HBRUSH search_bk_brush_{};
    HFONT search_font_{};

    platform::windows::CompositionSurface surface_;
    bool composition_ready_{false};
    Microsoft::WRL::ComPtr<ID2D1Factory> fallback_factory_;
    Microsoft::WRL::ComPtr<ID2D1HwndRenderTarget> fallback_target_;
    Microsoft::WRL::ComPtr<IDWriteFactory> write_factory_;
};

}  // namespace aevocis::ui
