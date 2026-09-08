#include "aevocis/ui/recording_overlay.hpp"

#include <d2d1.h>
#include <dwrite.h>

#include <algorithm>
#include <cmath>
#include <cwchar>

namespace aevocis::ui {

namespace {

constexpr wchar_t kClassName[] = L"AevocisNativeCppRecordingOverlay";
constexpr int kWidth = 460;
constexpr int kPillHeight = 72;
constexpr int kCaptionHeight = 84;
constexpr int kCaptionGap = 8;
constexpr int kHeight = kCaptionHeight + kCaptionGap + kPillHeight;
constexpr UINT_PTR kAnimTimerId = 1;
constexpr UINT kAnimIntervalMs = 33;
constexpr std::size_t kCaptionMaxChars = 180;

[[nodiscard]] const wchar_t* state_label(core::AppState state) noexcept {
    switch (state) {
    case core::AppState::Starting: return L"准备录音";
    case core::AppState::Capturing: return L"正在听";
    case core::AppState::Recognizing: return L"正在识别";
    case core::AppState::PostProcessing: return L"正在整理";
    case core::AppState::Confirming: return L"等待确认";
    case core::AppState::Injecting: return L"正在输入";
    case core::AppState::Cancelled: return L"已取消";
    case core::AppState::Failed: return L"暂时不可用";
    case core::AppState::Idle: return L"";
    }
    return L"";
}

void draw_text(ID2D1RenderTarget* target, IDWriteFactory* write_factory, const std::wstring& text, D2D1_RECT_F rect,
               float size, D2D1_COLOR_F color, DWRITE_FONT_WEIGHT weight, bool wrap = false) noexcept {
    if (target == nullptr || write_factory == nullptr || text.empty()) {
        return;
    }
    Microsoft::WRL::ComPtr<IDWriteTextFormat> format;
    (void)write_factory->CreateTextFormat(L"Segoe UI", nullptr, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                          size, L"zh-CN", &format);
    if (format == nullptr) {
        return;
    }
    (void)format->SetWordWrapping(wrap ? DWRITE_WORD_WRAPPING_WRAP : DWRITE_WORD_WRAPPING_NO_WRAP);
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> brush;
    (void)target->CreateSolidColorBrush(color, &brush);
    if (brush != nullptr) {
        target->DrawTextW(text.data(), static_cast<UINT32>(text.size()), format.Get(), rect, brush.Get(),
                          D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
    }
}

}  // namespace

RecordingOverlay::RecordingOverlay(HINSTANCE instance) noexcept : instance_(instance) {}

RecordingOverlay::~RecordingOverlay() {
    hide();
    discard_resources();
    if (hwnd_ != nullptr) {
        DestroyWindow(hwnd_);
    }
}

bool RecordingOverlay::create() noexcept {
    WNDCLASSEXW window_class{};
    window_class.cbSize = sizeof(window_class);
    window_class.hInstance = instance_;
    window_class.lpfnWndProc = window_proc;
    window_class.lpszClassName = kClassName;
    window_class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    if (RegisterClassExW(&window_class) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        return false;
    }
    hwnd_ = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, kClassName, L"Aevocis Recording", WS_POPUP,
                            0, 0, kWidth, kHeight, nullptr, nullptr, instance_, this);
    if (hwnd_ == nullptr) {
        return false;
    }
    create_resources();
    return composition_ready_ || fallback_target_ != nullptr;
}

void RecordingOverlay::set_theme(ThemeMode theme) noexcept {
    theme_ = theme;
    if (hwnd_ != nullptr) {
        InvalidateRect(hwnd_, nullptr, FALSE);
    }
}

void RecordingOverlay::set_partial_text(std::wstring text) noexcept {
    if (text.size() > kCaptionMaxChars) {
        text.erase(0, text.size() - kCaptionMaxChars);
    }
    partial_text_ = std::move(text);
    if (hwnd_ != nullptr) {
        InvalidateRect(hwnd_, nullptr, FALSE);
    }
}

void RecordingOverlay::set_state(core::AppState state) noexcept {
    const core::AppState previous = state_;
    state_ = state;
    if (state == core::AppState::Capturing && previous != core::AppState::Capturing) {
        capture_start_tick_ = GetTickCount64();
        partial_text_.clear();
    }
    if (state == core::AppState::Idle) {
        target_opacity_ = 0.0F;
    } else {
        target_opacity_ = 1.0F;
        reposition();
        ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
    }
    SetTimer(hwnd_, kAnimTimerId, kAnimIntervalMs, nullptr);
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void RecordingOverlay::hide() noexcept {
    opacity_ = 0.0F;
    target_opacity_ = 0.0F;
    if (composition_ready_) {
        surface_.set_opacity(0.0F);
    }
    if (hwnd_ != nullptr) {
        KillTimer(hwnd_, kAnimTimerId);
        ShowWindow(hwnd_, SW_HIDE);
    }
}

void RecordingOverlay::reposition() noexcept {
    POINT cursor{};
    HMONITOR monitor = GetCursorPos(&cursor) != FALSE ? MonitorFromPoint(cursor, MONITOR_DEFAULTTOPRIMARY)
                                                       : MonitorFromWindow(hwnd_, MONITOR_DEFAULTTOPRIMARY);
    MONITORINFO info{};
    info.cbSize = sizeof(info);
    RECT work{0, 0, 0, 0};
    if (monitor != nullptr && GetMonitorInfoW(monitor, &info) != FALSE) {
        work = info.rcWork;
    } else {
        SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    }
    const int x = (work.left + work.right - kWidth) / 2;
    const int y = work.bottom - kHeight - 34;
    SetWindowPos(hwnd_, HWND_TOPMOST, x, y, kWidth, kHeight, SWP_NOACTIVATE | SWP_SHOWWINDOW);
}

void RecordingOverlay::tick_animation() noexcept {
    constexpr float kStep = 0.22F;
    if (opacity_ < target_opacity_) {
        opacity_ = std::min(target_opacity_, opacity_ + kStep);
    } else if (opacity_ > target_opacity_) {
        opacity_ = std::max(target_opacity_, opacity_ - kStep);
    }
    if (composition_ready_) {
        surface_.set_opacity(opacity_);
    }
    if (opacity_ == 0.0F && target_opacity_ == 0.0F) {
        KillTimer(hwnd_, kAnimTimerId);
        ShowWindow(hwnd_, SW_HIDE);
        return;
    }
    InvalidateRect(hwnd_, nullptr, FALSE);
}

LRESULT CALLBACK RecordingOverlay::window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept {
    auto* self = reinterpret_cast<RecordingOverlay*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* create = reinterpret_cast<const CREATESTRUCTW*>(lparam);
        self = static_cast<RecordingOverlay*>(create->lpCreateParams);
        self->hwnd_ = hwnd;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    return self != nullptr ? self->handle_message(message, wparam, lparam) : DefWindowProcW(hwnd, message, wparam, lparam);
}

LRESULT RecordingOverlay::handle_message(UINT message, WPARAM wparam, LPARAM lparam) noexcept {
    switch (message) {
    case WM_PAINT: {
        PAINTSTRUCT paint{};
        BeginPaint(hwnd_, &paint);
        render();
        EndPaint(hwnd_, &paint);
        return 0;
    }
    case WM_TIMER:
        if (wparam == kAnimTimerId) {
            tick_animation();
        }
        return 0;
    case WM_SIZE:
        if (wparam != SIZE_MINIMIZED) {
            if (composition_ready_) {
                surface_.resize(LOWORD(lparam), HIWORD(lparam));
            } else {
                discard_resources();
                create_resources();
            }
        }
        return 0;
    case WM_ERASEBKGND:
        return 1;
    default:
        return DefWindowProcW(hwnd_, message, wparam, lparam);
    }
}

void RecordingOverlay::create_resources() noexcept {
    if (write_factory_ == nullptr) {
        (void)DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                  reinterpret_cast<IUnknown**>(write_factory_.GetAddressOf()));
    }
    if (!composition_ready_ && fallback_target_ == nullptr) {
        composition_ready_ = surface_.attach(hwnd_, kWidth, kHeight);
    }
    if (!composition_ready_ && fallback_target_ == nullptr) {
        if (fallback_factory_ == nullptr) {
            (void)D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, fallback_factory_.GetAddressOf());
        }
        if (fallback_factory_ != nullptr) {
            RECT rect{};
            GetClientRect(hwnd_, &rect);
            const auto size = D2D1::SizeU(static_cast<UINT32>(rect.right), static_cast<UINT32>(rect.bottom));
            (void)fallback_factory_->CreateHwndRenderTarget(D2D1::RenderTargetProperties(),
                                                            D2D1::HwndRenderTargetProperties(hwnd_, size), &fallback_target_);
        }
    }
}

void RecordingOverlay::discard_resources() noexcept { fallback_target_.Reset(); }

void RecordingOverlay::render() noexcept {
    if (composition_ready_) {
        if (ID2D1DeviceContext* dc = surface_.begin_draw(); dc != nullptr) {
            render_content(dc);
            surface_.end_draw();
        }
        return;
    }
    if (fallback_target_ == nullptr) {
        create_resources();
    }
    if (fallback_target_ == nullptr) {
        return;
    }
    fallback_target_->BeginDraw();
    render_content(fallback_target_.Get());
    const HRESULT result = fallback_target_->EndDraw();
    if (result == D2DERR_RECREATE_TARGET) {
        discard_resources();
    }
}

void RecordingOverlay::render_content(ID2D1RenderTarget* target) noexcept {
    if (target == nullptr) {
        return;
    }
    const Palette palette = palette_for(theme_);
    target->Clear(D2D1::ColorF(0, 0.0F));

    const float pill_top = static_cast<float>(kCaptionHeight + kCaptionGap);
    const D2D1_RECT_F pill = D2D1::RectF(6, pill_top + 6.0F, static_cast<float>(kWidth) - 6, static_cast<float>(kHeight) - 6);
    const float radius = (pill.bottom - pill.top) / 2.0F;

    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> brush;

    if (!partial_text_.empty()) {
        const D2D1_RECT_F caption = D2D1::RectF(6, 4, static_cast<float>(kWidth) - 6, pill_top - 4.0F);
        brush.Reset();
        (void)target->CreateSolidColorBrush(D2D1::ColorF(0x000000, 0.22F * opacity_), &brush);
        if (brush != nullptr) {
            target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(caption.left, caption.top + 3.0F, caption.right, caption.bottom + 3.0F), 18.0F, 18.0F), brush.Get());
        }
        brush.Reset();
        (void)target->CreateSolidColorBrush(D2D1::ColorF(palette.panel.r, palette.panel.g, palette.panel.b, 0.94F * opacity_), &brush);
        if (brush != nullptr) {
            target->FillRoundedRectangle(D2D1::RoundedRect(caption, 18.0F, 18.0F), brush.Get());
        }
        draw_text(target, write_factory_.Get(), partial_text_,
                 D2D1::RectF(caption.left + 18.0F, caption.top + 12.0F, caption.right - 18.0F, caption.bottom - 10.0F), 15.0F,
                 D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, opacity_), DWRITE_FONT_WEIGHT_MEDIUM, true);
    }
    for (int layer = 4; layer >= 1; --layer) {
        const float grow = static_cast<float>(layer) * 2.0F;
        const float alpha = 0.045F * static_cast<float>(5 - layer) * opacity_;
        brush.Reset();
        (void)target->CreateSolidColorBrush(D2D1::ColorF(0x000000, alpha), &brush);
        if (brush != nullptr) {
            target->FillRoundedRectangle(
                D2D1::RoundedRect(D2D1::RectF(pill.left - grow, pill.top - grow + 3.0F, pill.right + grow, pill.bottom + grow + 3.0F),
                                  radius + grow, radius + grow),
                brush.Get());
        }
    }

    brush.Reset();
    (void)target->CreateSolidColorBrush(D2D1::ColorF(palette.panel.r, palette.panel.g, palette.panel.b, 0.94F * opacity_), &brush);
    if (brush != nullptr) {
        target->FillRoundedRectangle(D2D1::RoundedRect(pill, radius, radius), brush.Get());
    }

    const double phase = static_cast<double>(GetTickCount64() % 1400) / 1400.0;
    const float pulse = static_cast<float>(0.55 + 0.45 * std::sin(phase * 6.2831853));
    const float dot_cy = (pill.top + pill.bottom) / 2.0F;
    brush.Reset();
    (void)target->CreateSolidColorBrush(D2D1::ColorF(palette.record.r, palette.record.g, palette.record.b, pulse * opacity_), &brush);
    if (brush != nullptr) {
        target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(pill.left + 30.0F, dot_cy), 6.0F, 6.0F), brush.Get());
    }

    draw_text(target, write_factory_.Get(), state_label(state_), D2D1::RectF(58, 20, 280, 52), 16.0F,
             D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, opacity_), DWRITE_FONT_WEIGHT_SEMI_BOLD);

    if (state_ == core::AppState::Capturing) {
        brush.Reset();
        (void)target->CreateSolidColorBrush(D2D1::ColorF(palette.accent.r, palette.accent.g, palette.accent.b, opacity_), &brush);
        if (brush != nullptr) {
            const float base_x = pill.left + 290.0F;
            for (int bar = 0; bar < 5; ++bar) {
                const double bar_phase = phase * 6.2831853 + static_cast<double>(bar) * 0.9;
                const float bar_height = 4.0F + static_cast<float>(std::fabs(std::sin(bar_phase))) * 16.0F;
                const float x = base_x + static_cast<float>(bar) * 7.0F;
                target->FillRoundedRectangle(
                    D2D1::RoundedRect(D2D1::RectF(x, dot_cy - bar_height / 2.0F, x + 3.0F, dot_cy + bar_height / 2.0F), 1.5F, 1.5F),
                    brush.Get());
            }
        }
        const ULONGLONG elapsed_ms = capture_start_tick_ == 0 ? 0 : GetTickCount64() - capture_start_tick_;
        const unsigned int total_seconds = static_cast<unsigned int>(elapsed_ms / 1000);
        wchar_t timer_text[16]{};
        (void)swprintf(timer_text, ARRAYSIZE(timer_text), L"%u:%02u", total_seconds / 60, total_seconds % 60);
        draw_text(target, write_factory_.Get(), timer_text, D2D1::RectF(pill.right - 60.0F, 20, pill.right - 12.0F, 52), 15.0F,
                 D2D1::ColorF(palette.muted.r, palette.muted.g, palette.muted.b, opacity_), DWRITE_FONT_WEIGHT_NORMAL);
    }
}

}  // namespace aevocis::ui
