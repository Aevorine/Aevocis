#pragma once

#include "aevocis/core/state.hpp"

#include <windows.h>
#include <wrl/client.h>

struct ID2D1Factory;
struct ID2D1HwndRenderTarget;
struct IDWriteFactory;

namespace aevocis::ui {

class RecordingOverlay {
public:
    explicit RecordingOverlay(HINSTANCE instance) noexcept;
    RecordingOverlay(const RecordingOverlay&) = delete;
    RecordingOverlay& operator=(const RecordingOverlay&) = delete;
    ~RecordingOverlay();

    [[nodiscard]] bool create() noexcept;
    void set_state(core::AppState state) noexcept;
    void hide() noexcept;

private:
    static LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    LRESULT handle_message(UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    void render() noexcept;
    void create_resources() noexcept;
    void discard_resources() noexcept;

    HINSTANCE instance_{};
    HWND hwnd_{};
    core::AppState state_{core::AppState::Idle};
    Microsoft::WRL::ComPtr<ID2D1Factory> d2d_factory_;
    Microsoft::WRL::ComPtr<ID2D1HwndRenderTarget> render_target_;
    Microsoft::WRL::ComPtr<IDWriteFactory> write_factory_;
};

}  // namespace aevocis::ui
