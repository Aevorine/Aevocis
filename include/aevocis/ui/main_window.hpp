#pragma once

#include "aevocis/core/state.hpp"

#include <functional>
#include <cstdint>
#include <string>
#include <vector>

#include <windows.h>
#include <d2d1.h>
#include <wrl/client.h>

struct ID2D1Factory;
struct ID2D1HwndRenderTarget;
struct IDWriteFactory;
struct IDWriteTextFormat;

namespace aevocis::ui {

enum class ThemeMode : std::uint8_t { Paper, DarkGlass };

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
    void set_theme(ThemeMode theme) noexcept;
    void set_trigger_mode(bool toggle) noexcept;
    void show_or_hide() noexcept;
    void show() noexcept;
    void hide() noexcept;
    [[nodiscard]] bool visible() const noexcept;
    void toggle_theme() noexcept;
    void open_settings() noexcept;
    void set_state(core::AppState state) noexcept;
    void set_error(core::ErrorCode error) noexcept;
    void add_history(std::string text);
    void clear_history() noexcept;

private:
    static LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    LRESULT handle_window_message(UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    void render() noexcept;
    void create_resources() noexcept;
    void discard_resources() noexcept;
    void draw_text(const std::wstring& value, D2D1_RECT_F rect, float size, bool english = false) noexcept;
    void create_tooltips() noexcept;

    HINSTANCE instance_{};
    HWND hwnd_{};
    HWND tooltip_{};
    HICON icon_{};
    MessageHandler message_handler_;
    ThemeMode theme_{ThemeMode::Paper};
    core::AppState state_{core::AppState::Idle};
    core::ErrorCode error_{core::ErrorCode::None};
    bool settings_open_{false};
    bool toggle_mode_{false};
    Action trigger_mode_handler_;
    Action history_clear_handler_;
    Action theme_handler_;
    std::vector<std::wstring> history_;

    Microsoft::WRL::ComPtr<ID2D1Factory> d2d_factory_;
    Microsoft::WRL::ComPtr<ID2D1HwndRenderTarget> render_target_;
    Microsoft::WRL::ComPtr<IDWriteFactory> write_factory_;
};

}  // namespace aevocis::ui
