#include "aevocis/ui/recording_overlay.hpp"

#include <d2d1.h>
#include <dwrite.h>

#include <cwchar>

namespace aevocis::ui {

namespace {

constexpr wchar_t kClassName[] = L"AevocisNativeCppRecordingOverlay";
constexpr int kWidth = 420;
constexpr int kHeight = 66;

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

}  // namespace

RecordingOverlay::RecordingOverlay(HINSTANCE instance) noexcept : instance_(instance) {}

RecordingOverlay::~RecordingOverlay() {
    hide();
    discard_resources();
    if (hwnd_ != nullptr) DestroyWindow(hwnd_);
}

bool RecordingOverlay::create() noexcept {
    WNDCLASSEXW window_class{};
    window_class.cbSize = sizeof(window_class);
    window_class.hInstance = instance_;
    window_class.lpfnWndProc = window_proc;
    window_class.lpszClassName = kClassName;
    window_class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    if (RegisterClassExW(&window_class) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;
    hwnd_ = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, kClassName, L"Aevocis Recording",
                            WS_POPUP, 0, 0, kWidth, kHeight, nullptr, nullptr, instance_, this);
    if (hwnd_ == nullptr) return false;
    create_resources();
    return render_target_ != nullptr;
}

void RecordingOverlay::set_state(core::AppState state) noexcept {
    state_ = state;
    if (state == core::AppState::Idle) {
        hide();
        return;
    }
    RECT work{};
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    const int x = (work.left + work.right - kWidth) / 2;
    const int y = work.bottom - kHeight - 34;
    SetWindowPos(hwnd_, HWND_TOPMOST, x, y, kWidth, kHeight, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void RecordingOverlay::hide() noexcept {
    if (hwnd_ != nullptr) ShowWindow(hwnd_, SW_HIDE);
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
    case WM_SIZE:
        if (wparam != SIZE_MINIMIZED) {
            discard_resources();
            create_resources();
        }
        return 0;
    case WM_ERASEBKGND:
        return 1;
    default:
        return DefWindowProcW(hwnd_, message, wparam, lparam);
    }
}

void RecordingOverlay::create_resources() noexcept {
    RECT rect{};
    GetClientRect(hwnd_, &rect);
    if (d2d_factory_ == nullptr) (void)D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2d_factory_.GetAddressOf());
    if (write_factory_ == nullptr) {
        (void)DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                  reinterpret_cast<IUnknown**>(write_factory_.GetAddressOf()));
    }
    if (d2d_factory_ != nullptr && render_target_ == nullptr) {
        const auto size = D2D1::SizeU(static_cast<UINT32>(rect.right), static_cast<UINT32>(rect.bottom));
        (void)d2d_factory_->CreateHwndRenderTarget(D2D1::RenderTargetProperties(),
                                                   D2D1::HwndRenderTargetProperties(hwnd_, size), &render_target_);
    }
}

void RecordingOverlay::discard_resources() noexcept { render_target_.Reset(); }

void RecordingOverlay::render() noexcept {
    if (render_target_ == nullptr || write_factory_ == nullptr) return;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> brush;
    Microsoft::WRL::ComPtr<IDWriteTextFormat> format;
    (void)render_target_->CreateSolidColorBrush(D2D1::ColorF(0x17202B, 0.96F), &brush);
    (void)write_factory_->CreateTextFormat(L"SimSun", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL,
                                           DWRITE_FONT_STRETCH_NORMAL, 16.0F, L"zh-CN", &format);
    render_target_->BeginDraw();
    render_target_->Clear(D2D1::ColorF(0x10151D, 0.0F));
    if (brush != nullptr) {
        render_target_->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(0, 0, kWidth, kHeight), 28, 28), brush.Get());
        brush.Reset();
        (void)render_target_->CreateSolidColorBrush(D2D1::ColorF(0x62D7C5), &brush);
        render_target_->FillEllipse(D2D1::Ellipse(D2D1::Point2F(32, 33), 7, 7), brush.Get());
        brush.Reset();
        (void)render_target_->CreateSolidColorBrush(D2D1::ColorF(0xF0F4F8), &brush);
        const wchar_t* text = state_label(state_);
        if (format != nullptr) render_target_->DrawTextW(text, static_cast<UINT32>(wcslen(text)), format.Get(), D2D1::RectF(56, 18, 210, 50), brush.Get());
    }
    (void)render_target_->EndDraw();
}

}  // namespace aevocis::ui
