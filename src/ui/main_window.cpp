#include "aevocis/ui/main_window.hpp"

#include "aevocis/platform/windows/messages.hpp"

#include <commctrl.h>
#include <d2d1.h>
#include <dwrite.h>
#include <windowsx.h>

#include <algorithm>
#include <string>

namespace aevocis::ui {

using Microsoft::WRL::ComPtr;
using aevocis::platform::windows::kCommandSettings;
using aevocis::platform::windows::kCommandTheme;

namespace {

constexpr wchar_t kClassName[] = L"AevocisNativeCppWindow";
constexpr int kWidth = 680;
constexpr int kHeight = 480;

struct Colors {
    D2D1_COLOR_F background;
    D2D1_COLOR_F surface;
    D2D1_COLOR_F ink;
    D2D1_COLOR_F muted;
    D2D1_COLOR_F accent;
    D2D1_COLOR_F border;
};

[[nodiscard]] Colors colors(ThemeMode theme) noexcept {
    switch (theme) {
    case ThemeMode::DarkGlass:
        return {D2D1::ColorF(0x10151D), D2D1::ColorF(0x17202B), D2D1::ColorF(0xF0F4F8), D2D1::ColorF(0x9BA9B8),
                D2D1::ColorF(0x62D7C5), D2D1::ColorF(0x2D3A49)};
    case ThemeMode::HighContrast:
        // D1: WCAG-AA-level contrast (near-black ink on near-white surface, thick dark border)
        // for users who specifically need maximum legibility over long-attention comfort.
        return {D2D1::ColorF(0xFFFFFF), D2D1::ColorF(0xFFFFFF), D2D1::ColorF(0x000000), D2D1::ColorF(0x1A1A1A),
                D2D1::ColorF(0x0047AB), D2D1::ColorF(0x000000)};
    case ThemeMode::Sepia:
        // D1: warm low-blue-light palette for long evening sessions.
        return {D2D1::ColorF(0xEFE3CE), D2D1::ColorF(0xF7EEDD), D2D1::ColorF(0x4A3B28), D2D1::ColorF(0x8A7657),
                D2D1::ColorF(0xB8763D), D2D1::ColorF(0xD9C7A3)};
    case ThemeMode::Paper:
    default:
        return {D2D1::ColorF(0xF5F2EA), D2D1::ColorF(0xFFFDF8), D2D1::ColorF(0x29333D), D2D1::ColorF(0x77818A),
                D2D1::ColorF(0x2A9D8F), D2D1::ColorF(0xDDD7CA)};
    }
}

[[nodiscard]] const wchar_t* theme_name(ThemeMode theme) noexcept {
    switch (theme) {
    case ThemeMode::DarkGlass: return L"Dark Glass";
    case ThemeMode::HighContrast: return L"High Contrast";
    case ThemeMode::Sepia: return L"Sepia";
    case ThemeMode::Paper:
    default: return L"Paper";
    }
}

[[nodiscard]] ThemeMode next_theme(ThemeMode theme) noexcept {
    switch (theme) {
    case ThemeMode::Paper: return ThemeMode::DarkGlass;
    case ThemeMode::DarkGlass: return ThemeMode::HighContrast;
    case ThemeMode::HighContrast: return ThemeMode::Sepia;
    case ThemeMode::Sepia:
    default: return ThemeMode::Paper;
    }
}

[[nodiscard]] std::wstring state_label(core::AppState state, core::ErrorCode error) {
    if (state == core::AppState::Failed && error == core::ErrorCode::RecognitionUnavailable) {
        return L"识别模型未就绪";
    }
    if (state == core::AppState::Failed) {
        return L"失败";
    }
    switch (state) {
    case core::AppState::Idle: return L"就绪";
    case core::AppState::Starting: return L"准备中";
    case core::AppState::Capturing: return L"录音中";
    case core::AppState::Recognizing: return L"识别中";
    case core::AppState::PostProcessing: return L"整理中";
    case core::AppState::Confirming: return L"待确认";
    case core::AppState::Injecting: return L"输入中";
    case core::AppState::Cancelled: return L"已取消";
    case core::AppState::Failed: return error == core::ErrorCode::RecognitionUnavailable ? L"识别模型未就绪" : L"失败";
    }
    return L"就绪";
}

}  // namespace

MainWindow::MainWindow(HINSTANCE instance) noexcept : instance_(instance) {}

MainWindow::~MainWindow() {
    if (tooltip_ != nullptr) {
        DestroyWindow(tooltip_);
    }
    discard_resources();
    if (hwnd_ != nullptr) {
        DestroyWindow(hwnd_);
    }
}

bool MainWindow::create() noexcept {
    WNDCLASSEXW window_class{};
    window_class.cbSize = sizeof(window_class);
    window_class.hInstance = instance_;
    window_class.lpfnWndProc = window_proc;
    window_class.lpszClassName = kClassName;
    window_class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    window_class.hIcon = icon_;
    window_class.hIconSm = icon_;
    window_class.hbrBackground = nullptr;
    window_class.style = CS_HREDRAW | CS_VREDRAW;
    if (RegisterClassExW(&window_class) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        return false;
    }
    hwnd_ = CreateWindowExW(WS_EX_TOOLWINDOW, kClassName, L"Aevocis", WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
                            CW_USEDEFAULT, CW_USEDEFAULT, kWidth, kHeight, nullptr, nullptr, instance_, this);
    if (hwnd_ == nullptr) {
        return false;
    }
    create_resources();
    create_tooltips();
    return render_target_ != nullptr;
}

void MainWindow::set_icon(HICON icon) noexcept { icon_ = icon; }

void MainWindow::set_message_handler(MessageHandler handler) { message_handler_ = std::move(handler); }

void MainWindow::set_trigger_mode_handler(Action handler) { trigger_mode_handler_ = std::move(handler); }

void MainWindow::set_history_clear_handler(Action handler) { history_clear_handler_ = std::move(handler); }

void MainWindow::set_theme_handler(Action handler) { theme_handler_ = std::move(handler); }

void MainWindow::set_first_paint_handler(Action handler) { first_paint_handler_ = std::move(handler); }

void MainWindow::set_theme(ThemeMode theme) noexcept {
    theme_ = theme;
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void MainWindow::set_trigger_mode(bool toggle) noexcept {
    toggle_mode_ = toggle;
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void MainWindow::show_or_hide() noexcept {
    if (visible()) {
        hide();
    } else {
        show();
    }
}

void MainWindow::show() noexcept {
    ShowWindow(hwnd_, SW_SHOWNORMAL);
    SetForegroundWindow(hwnd_);
    (void)UpdateWindow(hwnd_);
}

void MainWindow::hide() noexcept { ShowWindow(hwnd_, SW_HIDE); }

bool MainWindow::visible() const noexcept { return hwnd_ != nullptr && IsWindowVisible(hwnd_) != FALSE; }

void MainWindow::toggle_theme() noexcept {
    theme_ = next_theme(theme_);
    if (theme_handler_) {
        theme_handler_();
    }
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void MainWindow::set_stats(SessionStats stats) noexcept {
    stats_ = stats;
    if (settings_open_) {
        InvalidateRect(hwnd_, nullptr, FALSE);
    }
}

void MainWindow::open_settings() noexcept {
    settings_open_ = true;
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void MainWindow::set_state(core::AppState state) noexcept {
    state_ = state;
    error_ = core::ErrorCode::None;
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void MainWindow::set_error(core::ErrorCode error) noexcept {
    error_ = error;
    state_ = core::AppState::Failed;
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void MainWindow::add_history(std::string text) {
    if (text.empty()) {
        return;
    }
    const int length = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (length <= 0) {
        return;
    }
    std::wstring wide(static_cast<std::size_t>(length), L'\0');
    (void)MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), wide.data(), length);
    history_.insert(history_.begin(), std::move(wide));
    if (history_.size() > 20) {
        history_.pop_back();
    }
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void MainWindow::clear_history() noexcept {
    history_.clear();
    InvalidateRect(hwnd_, nullptr, FALSE);
}

LRESULT CALLBACK MainWindow::window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept {
    MainWindow* self = reinterpret_cast<MainWindow*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* create = reinterpret_cast<const CREATESTRUCTW*>(lparam);
        self = static_cast<MainWindow*>(create->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        self->hwnd_ = hwnd;
    }
    if (self != nullptr) {
        return self->handle_window_message(message, wparam, lparam);
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

LRESULT MainWindow::handle_window_message(UINT message, WPARAM wparam, LPARAM lparam) noexcept {
    if (message_handler_ && message_handler_(message, wparam, lparam)) {
        return 0;
    }
    switch (message) {
    case WM_PAINT: {
        PAINTSTRUCT paint{};
        BeginPaint(hwnd_, &paint);
        render();
        EndPaint(hwnd_, &paint);
        if (!first_paint_fired_) {
            first_paint_fired_ = true;
            if (first_paint_handler_) {
                first_paint_handler_();
            }
        }
        return 0;
    }
    case WM_SIZE:
        if (wparam != SIZE_MINIMIZED) {
            discard_resources();
            create_resources();
        }
        return 0;
    case WM_LBUTTONUP: {
        const POINT point{GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
        const auto regions = build_focus_regions();
        for (const auto& region : regions) {
            if (point.x >= region.rect.left && point.x <= region.rect.right && point.y >= region.rect.top &&
                point.y <= region.rect.bottom) {
                if (region.activate) {
                    region.activate();
                }
                break;
            }
        }
        return 0;
    }
    case WM_KEYDOWN:
        handle_key_down(wparam);
        return 0;
    case WM_SETFOCUS:
        if (focus_index_ < 0) {
            focus_index_ = 0;
            InvalidateRect(hwnd_, nullptr, FALSE);
        }
        return 0;
    case WM_CLOSE:
        hide();
        return 0;
    case WM_DESTROY:
        return 0;
    default:
        return DefWindowProcW(hwnd_, message, wparam, lparam);
    }
}

std::vector<MainWindow::FocusRegion> MainWindow::build_focus_regions() {
    std::vector<FocusRegion> regions;
    // D4: identical rects to create_tooltips() by construction -- every region a mouse can
    // click and hover a tooltip on is also a region Tab can reach and Enter/Space can activate.
    if (settings_open_) {
        regions.push_back({RECT{12, 12, 96, 68}, [this] {
                                settings_open_ = false;
                                focus_index_ = 0;
                                InvalidateRect(hwnd_, nullptr, FALSE);
                            }});
        regions.push_back({RECT{20, 132, 340, 244}, [this] {
                                toggle_mode_ = !toggle_mode_;
                                if (trigger_mode_handler_) trigger_mode_handler_();
                                InvalidateRect(hwnd_, nullptr, FALSE);
                            }});
    }
    regions.push_back({RECT{kWidth - 150, 12, kWidth - 82, 62}, [this] { toggle_theme(); }});
    regions.push_back({RECT{kWidth - 76, 12, kWidth - 20, 62}, [this] {
                            settings_open_ = true;
                            focus_index_ = 0;
                            InvalidateRect(hwnd_, nullptr, FALSE);
                        }});
    if (!settings_open_) {
        regions.push_back({RECT{kWidth - 126, 364, kWidth - 20, 430}, [this] {
                                if (history_clear_handler_) history_clear_handler_();
                            }});
    }
    return regions;
}

void MainWindow::handle_key_down(WPARAM virtual_key) noexcept {
    const auto regions = build_focus_regions();
    if (regions.empty()) {
        return;
    }
    if (virtual_key == VK_TAB) {
        const bool shift_held = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
        const int count = static_cast<int>(regions.size());
        focus_index_ = focus_index_ < 0 ? 0 : (focus_index_ + (shift_held ? -1 : 1) + count) % count;
        InvalidateRect(hwnd_, nullptr, FALSE);
        return;
    }
    if ((virtual_key == VK_RETURN || virtual_key == VK_SPACE) && focus_index_ >= 0 &&
        focus_index_ < static_cast<int>(regions.size())) {
        if (regions[static_cast<std::size_t>(focus_index_)].activate) {
            regions[static_cast<std::size_t>(focus_index_)].activate();
        }
    }
}

void MainWindow::create_resources() noexcept {
    if (hwnd_ == nullptr) {
        return;
    }
    RECT rect{};
    GetClientRect(hwnd_, &rect);
    if (d2d_factory_ == nullptr) {
        (void)D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2d_factory_.GetAddressOf());
    }
    if (write_factory_ == nullptr) {
        (void)DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(write_factory_.GetAddressOf()));
    }
    if (d2d_factory_ != nullptr && render_target_ == nullptr) {
        const auto properties = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT,
                                                               D2D1::PixelFormat(DXGI_FORMAT_UNKNOWN, D2D1_ALPHA_MODE_IGNORE));
        const auto size = D2D1::SizeU(static_cast<UINT32>(rect.right - rect.left), static_cast<UINT32>(rect.bottom - rect.top));
        (void)d2d_factory_->CreateHwndRenderTarget(properties, D2D1::HwndRenderTargetProperties(hwnd_, size), &render_target_);
    }
}

void MainWindow::discard_resources() noexcept { render_target_.Reset(); }

void MainWindow::draw_text(const std::wstring& value, D2D1_RECT_F rect, float size, bool english) noexcept {
    if (render_target_ == nullptr || write_factory_ == nullptr) {
        return;
    }
    ComPtr<IDWriteTextFormat> format;
    (void)write_factory_->CreateTextFormat(english ? L"Times New Roman" : L"SimSun", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                                           DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, L"zh-CN", &format);
    if (format == nullptr) {
        return;
    }
    ComPtr<ID2D1SolidColorBrush> brush;
    const Colors palette = colors(theme_);
    (void)render_target_->CreateSolidColorBrush(palette.ink, &brush);
    if (brush != nullptr) {
        render_target_->DrawTextW(value.data(), static_cast<UINT32>(value.size()), format.Get(), rect, brush.Get(), D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
    }
}

void MainWindow::render() noexcept {
    if (render_target_ == nullptr) {
        create_resources();
    }
    if (render_target_ == nullptr) {
        return;
    }
    const Colors palette = colors(theme_);
    ComPtr<ID2D1SolidColorBrush> brush;
    (void)render_target_->CreateSolidColorBrush(palette.background, &brush);
    render_target_->BeginDraw();
    render_target_->Clear(palette.background);
    const auto fill = [&](D2D1_RECT_F rect, D2D1_COLOR_F color) {
        brush.Reset();
        (void)render_target_->CreateSolidColorBrush(color, &brush);
        if (brush != nullptr) {
            render_target_->FillRectangle(rect, brush.Get());
        }
    };
    fill(D2D1::RectF(0, 0, static_cast<float>(kWidth), 68), palette.surface);
    fill(D2D1::RectF(24, 28, 36, 40), palette.accent);
    draw_text(state_label(state_, error_), D2D1::RectF(52, 22, 190, 50), 16.0F);
    draw_text(theme_ == ThemeMode::Paper ? L"◐ 主题" : L"◑ 主题", D2D1::RectF(kWidth - 150.0F, 22, kWidth - 86.0F, 50), 13.0F);
    draw_text(L"⚙", D2D1::RectF(kWidth - 68.0F, 19, kWidth - 28.0F, 54), 22.0F, true);

    if (settings_open_) {
        draw_text(L"返回", D2D1::RectF(24, 22, 84, 52), 14.0F);
        draw_text(L"快捷键", D2D1::RectF(40, 112, 170, 146), 18.0F);
        draw_text(toggle_mode_ ? L"当前  切换模式" : L"当前  按住模式", D2D1::RectF(40, 158, 300, 190), 15.0F);
        draw_text(L"外观", D2D1::RectF(40, 286, 170, 320), 18.0F);
        draw_text(theme_name(theme_), D2D1::RectF(40, 332, 260, 366), 16.0F, true);
        // C5: today / all-time dictation counts and total characters, computed by the caller
        // from HistoryStore and handed in via set_stats -- purely a readout, no click target.
        draw_text(L"使用统计", D2D1::RectF(380, 112, 560, 146), 18.0F);
        draw_text(L"今日  " + std::to_wstring(stats_.dictations_today) + L" 次", D2D1::RectF(380, 158, 620, 188), 15.0F, true);
        draw_text(L"累计  " + std::to_wstring(stats_.dictations_total) + L" 次", D2D1::RectF(380, 192, 620, 222), 15.0F, true);
        draw_text(L"累计字数  " + std::to_wstring(stats_.characters_total), D2D1::RectF(380, 226, 640, 256), 15.0F, true);
    } else {
        fill(D2D1::RectF(0, 68, static_cast<float>(kWidth), 292), palette.surface);
        fill(D2D1::RectF(260, 118, 420, 278), palette.accent);
        draw_text(L"按住右 Ctrl 说话", D2D1::RectF(216, 300, 468, 334), 20.0F);
        draw_text(L"最近记录", D2D1::RectF(32, 386, 150, 414), 14.0F);
        draw_text(L"清空", D2D1::RectF(kWidth - 90.0F, 386, kWidth - 32.0F, 414), 13.0F);
        if (history_.empty()) {
            draw_text(L"", D2D1::RectF(32, 426, 620, 452), 14.0F);
        } else {
            draw_text(history_.front(), D2D1::RectF(32, 426, 638, 454), 14.0F);
        }
    }
    // D4: visible focus ring around whichever region Tab currently lands on, only while this
    // window actually holds keyboard focus -- keeps mouse-only use visually unchanged.
    if (focus_index_ >= 0 && GetFocus() == hwnd_) {
        const auto regions = build_focus_regions();
        if (focus_index_ < static_cast<int>(regions.size())) {
            const RECT& region = regions[static_cast<std::size_t>(focus_index_)].rect;
            brush.Reset();
            (void)render_target_->CreateSolidColorBrush(palette.accent, &brush);
            if (brush != nullptr) {
                render_target_->DrawRectangle(D2D1::RectF(static_cast<float>(region.left) - 2.0F, static_cast<float>(region.top) - 2.0F,
                                                          static_cast<float>(region.right) + 2.0F, static_cast<float>(region.bottom) + 2.0F),
                                              brush.Get(), 2.0F);
            }
        }
    }
    const HRESULT result = render_target_->EndDraw();
    if (result == D2DERR_RECREATE_TARGET) {
        discard_resources();
    }
}

void MainWindow::create_tooltips() noexcept {
    INITCOMMONCONTROLSEX controls{sizeof(INITCOMMONCONTROLSEX), ICC_WIN95_CLASSES};
    (void)InitCommonControlsEx(&controls);
    tooltip_ = CreateWindowExW(WS_EX_TOPMOST, TOOLTIPS_CLASSW, nullptr, WS_POPUP | TTS_ALWAYSTIP,
                               CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, hwnd_, nullptr, instance_, nullptr);
    if (tooltip_ == nullptr) {
        return;
    }
    const auto add_tooltip = [this](UINT_PTR id, RECT rect, wchar_t* text) {
        TOOLINFOW info{};
        info.cbSize = sizeof(info);
        info.uFlags = TTF_SUBCLASS;
        info.hwnd = hwnd_;
        info.uId = id;
        info.lpszText = text;
        info.rect = rect;
        (void)SendMessageW(tooltip_, TTM_ADDTOOLW, 0, reinterpret_cast<LPARAM>(&info));
    };
    add_tooltip(1, RECT{kWidth - 150, 12, kWidth - 82, 62}, const_cast<wchar_t*>(L"切换主题"));
    add_tooltip(2, RECT{kWidth - 76, 12, kWidth - 20, 62}, const_cast<wchar_t*>(L"打开设置"));
    add_tooltip(3, RECT{kWidth - 126, 364, kWidth - 20, 430}, const_cast<wchar_t*>(L"清空历史"));
    add_tooltip(4, RECT{12, 12, 96, 68}, const_cast<wchar_t*>(L"返回主界面"));
    add_tooltip(5, RECT{20, 132, 340, 244}, const_cast<wchar_t*>(L"切换按住或切换录音"));
}

}  // namespace aevocis::ui
