#pragma once

#include "aevocis/core/state.hpp"
#include "aevocis/platform/windows/composition_host.hpp"
#include "aevocis/ui/style.hpp"

#include <string>

#include <windows.h>
#include <wrl/client.h>

struct ID2D1Factory;
struct ID2D1HwndRenderTarget;
struct ID2D1RenderTarget;
struct IDWriteFactory;

namespace aevocis::ui {

class RecordingOverlay {
public:
    explicit RecordingOverlay(HINSTANCE instance) noexcept;
    RecordingOverlay(const RecordingOverlay&) = delete;
    RecordingOverlay& operator=(const RecordingOverlay&) = delete;
    ~RecordingOverlay();

    [[nodiscard]] bool create() noexcept;
    void set_theme(ThemeMode theme) noexcept;
    void set_state(core::AppState state) noexcept;
    void set_partial_text(std::wstring text) noexcept;
    void hide() noexcept;

private:
    static LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    LRESULT handle_message(UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    void render() noexcept;
    void render_content(ID2D1RenderTarget* target) noexcept;
    void create_resources() noexcept;
    void discard_resources() noexcept;
    void reposition() noexcept;
    void tick_animation() noexcept;

    HINSTANCE instance_{};
    HWND hwnd_{};
    ThemeMode theme_{ThemeMode::DarkGlass};
    core::AppState state_{core::AppState::Idle};
    ULONGLONG capture_start_tick_{0};
    std::wstring partial_text_;
    float opacity_{0.0F};
    float target_opacity_{0.0F};
    bool composition_ready_{false};

    platform::windows::CompositionSurface surface_;
    Microsoft::WRL::ComPtr<ID2D1Factory> fallback_factory_;
    Microsoft::WRL::ComPtr<ID2D1HwndRenderTarget> fallback_target_;
    Microsoft::WRL::ComPtr<IDWriteFactory> write_factory_;
};

}  // namespace aevocis::ui
