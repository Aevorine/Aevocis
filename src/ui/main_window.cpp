#include "aevocis/ui/main_window.hpp"

#include "aevocis/platform/windows/messages.hpp"

#include <commctrl.h>
#include <d2d1.h>
#include <dwmapi.h>
#include <dwrite.h>
#include <windowsx.h>

#include <algorithm>
#include <cmath>
#include <ctime>
#include <cwctype>
#include <string>

namespace aevocis::ui {

using Microsoft::WRL::ComPtr;

namespace {

constexpr wchar_t kClassName[] = L"AevocisNativeCppWindow";
constexpr int kWidth = 440;
constexpr int kHeight = 680;
// Every literal in compute_layout()/render_content() is authored against this baseline; the
// actual monitor DPI is compared against it to derive MainWindow::dpi_scale().
constexpr UINT kDefaultDpi = 96;
// Must not collide with Application::kIdleTimerId (also 1, also SetTimer'd on this same HWND
// from main.cpp) -- a shared id on the same window means the later SetTimer call silently
// overrides the earlier one's interval, and Application::handle_message's WM_TIMER branch (which
// runs first, via message_handler_, before MainWindow's own switch) swallows every WM_TIMER for
// id 1 by returning true, so this window's own fade-in tick would never run and opacity_ would
// stay at 0 forever -- a permanently blank window, not a startup race.
constexpr UINT_PTR kAnimTimerId = 401;
constexpr UINT kAnimIntervalMs = 33;
constexpr int kSearchEditId = 501;
constexpr float kCardHeight = 66.0F;
constexpr float kCardGap = 10.0F;

struct Layout {
    D2D1_RECT_F status_row;
    D2D1_RECT_F theme_button;
    D2D1_RECT_F settings_button;
    D2D1_RECT_F search_pill;
    D2D1_RECT_F history_area;
    D2D1_ELLIPSE record_button;
    D2D1_RECT_F hint_text;
    D2D1_RECT_F clear_all_button;
    D2D1_RECT_F back_button;
    D2D1_RECT_F trigger_mode_card;
    D2D1_RECT_F appearance_card;
    D2D1_RECT_F stats_card;
};

[[nodiscard]] Layout compute_layout() noexcept {
    constexpr float w = static_cast<float>(kWidth);
    constexpr float h = static_cast<float>(kHeight);
    Layout layout{};
    layout.status_row = D2D1::RectF(20.0F, 12.0F, w - 100.0F, 52.0F);
    layout.theme_button = D2D1::RectF(w - 92.0F, 12.0F, w - 52.0F, 52.0F);
    layout.settings_button = D2D1::RectF(w - 44.0F, 12.0F, w - 4.0F, 52.0F);
    layout.search_pill = D2D1::RectF(20.0F, 60.0F, w - 20.0F, 96.0F);
    layout.history_area = D2D1::RectF(20.0F, 110.0F, w - 20.0F, h - 190.0F);
    layout.record_button = D2D1::Ellipse(D2D1::Point2F(w / 2.0F, h - 140.0F), 38.0F, 38.0F);
    layout.hint_text = D2D1::RectF(20.0F, h - 46.0F, w - 140.0F, h - 16.0F);
    layout.clear_all_button = D2D1::RectF(w - 96.0F, h - 46.0F, w - 20.0F, h - 16.0F);
    layout.back_button = D2D1::RectF(16.0F, 12.0F, 92.0F, 52.0F);
    layout.trigger_mode_card = D2D1::RectF(20.0F, 70.0F, w - 20.0F, 150.0F);
    layout.appearance_card = D2D1::RectF(20.0F, 166.0F, w - 20.0F, 246.0F);
    layout.stats_card = D2D1::RectF(20.0F, 262.0F, w - 20.0F, 384.0F);
    return layout;
}

[[nodiscard]] bool contains_case_insensitive(std::wstring_view haystack, std::wstring_view needle) noexcept {
    if (needle.empty()) {
        return true;
    }
    const auto to_lower = [](wchar_t ch) { return static_cast<wchar_t>(std::towlower(ch)); };
    const auto it = std::search(haystack.begin(), haystack.end(), needle.begin(), needle.end(),
                                [&](wchar_t a, wchar_t b) { return to_lower(a) == to_lower(b); });
    return it != haystack.end();
}

[[nodiscard]] std::wstring format_time(std::int64_t epoch_seconds) {
    if (epoch_seconds == 0) {
        return L"—";
    }
    const auto raw = static_cast<std::time_t>(epoch_seconds);
    std::tm local{};
    if (localtime_s(&local, &raw) != 0) {
        return L"—";
    }
    wchar_t buffer[32]{};
    if (std::wcsftime(buffer, ARRAYSIZE(buffer), L"%m-%d %H:%M", &local) == 0) {
        return L"—";
    }
    return buffer;
}

[[nodiscard]] COLORREF to_colorref(D2D1_COLOR_F color) noexcept {
    return RGB(static_cast<BYTE>(color.r * 255.0F + 0.5F), static_cast<BYTE>(color.g * 255.0F + 0.5F),
              static_cast<BYTE>(color.b * 255.0F + 0.5F));
}

[[nodiscard]] D2D1_COLOR_F button_fill_color(core::AppState state, const Palette& palette) noexcept {
    switch (state) {
    case core::AppState::Capturing: return palette.record;
    case core::AppState::Cancelled:
    case core::AppState::Failed: return palette.muted;
    case core::AppState::Idle:
    case core::AppState::Starting:
    case core::AppState::Recognizing:
    case core::AppState::PostProcessing:
    case core::AppState::Confirming:
    case core::AppState::Injecting:
    default: return palette.accent;
    }
}

[[nodiscard]] std::wstring state_label(core::AppState state, core::ErrorCode error) {
    if (state == core::AppState::Failed && error == core::ErrorCode::RecognitionUnavailable) {
        return L"识别模型未就绪";
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
    case core::AppState::Failed: return L"失败";
    }
    return L"就绪";
}

void draw_text(ID2D1RenderTarget* target, IDWriteFactory* write_factory, const std::wstring& text, D2D1_RECT_F rect,
              float size, D2D1_COLOR_F color, DWRITE_FONT_WEIGHT weight, DWRITE_TEXT_ALIGNMENT align,
              const wchar_t* family = L"Segoe UI") noexcept {
    if (target == nullptr || write_factory == nullptr || text.empty()) {
        return;
    }
    ComPtr<IDWriteTextFormat> format;
    (void)write_factory->CreateTextFormat(family, nullptr, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                          size, L"zh-CN", &format);
    if (format == nullptr) {
        return;
    }
    (void)format->SetTextAlignment(align);
    (void)format->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP);
    ComPtr<ID2D1SolidColorBrush> brush;
    (void)target->CreateSolidColorBrush(color, &brush);
    if (brush != nullptr) {
        target->DrawTextW(text.data(), static_cast<UINT32>(text.size()), format.Get(), rect, brush.Get(),
                          D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
    }
}

void draw_glow_circle(ID2D1RenderTarget* target, D2D1_POINT_2F center, float radius, D2D1_COLOR_F color, float alpha) noexcept {
    ComPtr<ID2D1SolidColorBrush> brush;
    for (int layer = 4; layer >= 1; --layer) {
        const float grow = static_cast<float>(layer) * 5.0F;
        const float layer_alpha = 0.05F * static_cast<float>(5 - layer) * alpha;
        brush.Reset();
        (void)target->CreateSolidColorBrush(D2D1::ColorF(color.r, color.g, color.b, layer_alpha), &brush);
        if (brush != nullptr) {
            target->FillEllipse(D2D1::Ellipse(center, radius + grow, radius + grow), brush.Get());
        }
    }
}

}  // namespace

MainWindow::MainWindow(HINSTANCE instance) noexcept : instance_(instance) {}

MainWindow::~MainWindow() {
    if (tooltip_ != nullptr) {
        DestroyWindow(tooltip_);
    }
    if (search_bk_brush_ != nullptr) {
        DeleteObject(search_bk_brush_);
    }
    if (search_font_ != nullptr) {
        DeleteObject(search_font_);
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
    // The process declares per-monitor-v2 DPI awareness (see main.cpp), so Windows never
    // auto-scales this window -- without this, kWidth/kHeight are used as physical pixels
    // verbatim, and on anything above 100% scale the whole app renders far smaller on screen
    // than every other (non-aware) app, which reads as "the UI looks broken". Resize to the
    // real monitor's DPI before any resource/content is created; render_content() applies the
    // matching Direct2D scale transform, and native child controls are sized in the affected
    // functions below.
    dpi_ = GetDpiForWindow(hwnd_);
    if (dpi_ != kDefaultDpi) {
        RECT window_rect{0, 0, MulDiv(kWidth, static_cast<int>(dpi_), static_cast<int>(kDefaultDpi)),
                         MulDiv(kHeight, static_cast<int>(dpi_), static_cast<int>(kDefaultDpi))};
        AdjustWindowRectExForDpi(&window_rect, WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX, FALSE,
                                 WS_EX_TOOLWINDOW, dpi_);
        SetWindowPos(hwnd_, nullptr, 0, 0, window_rect.right - window_rect.left, window_rect.bottom - window_rect.top,
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }
    apply_dark_titlebar();
    create_resources();
    create_search_edit();
    create_tooltips();
    return composition_ready_ || fallback_target_ != nullptr;
}

void MainWindow::set_icon(HICON icon) noexcept { icon_ = icon; }

void MainWindow::set_message_handler(MessageHandler handler) { message_handler_ = std::move(handler); }

void MainWindow::set_trigger_mode_handler(Action handler) { trigger_mode_handler_ = std::move(handler); }

void MainWindow::set_history_clear_handler(Action handler) { history_clear_handler_ = std::move(handler); }

void MainWindow::set_theme_handler(Action handler) { theme_handler_ = std::move(handler); }

void MainWindow::set_first_paint_handler(Action handler) { first_paint_handler_ = std::move(handler); }

void MainWindow::set_theme(ThemeMode theme) noexcept {
    theme_ = theme;
    apply_dark_titlebar();
    update_search_brush();
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
    if (composition_ready_) {
        opacity_ = 0.0F;
        target_opacity_ = 1.0F;
        surface_.set_opacity(0.0F);
    }
    ShowWindow(hwnd_, SW_SHOWNORMAL);
    SetForegroundWindow(hwnd_);
    (void)UpdateWindow(hwnd_);
    if (composition_ready_) {
        SetTimer(hwnd_, kAnimTimerId, kAnimIntervalMs, nullptr);
    }
}

void MainWindow::hide() noexcept { ShowWindow(hwnd_, SW_HIDE); }

bool MainWindow::visible() const noexcept { return hwnd_ != nullptr && IsWindowVisible(hwnd_) != FALSE; }

void MainWindow::toggle_theme() noexcept {
    theme_ = next_theme(theme_);
    if (theme_handler_) {
        theme_handler_();
    }
    apply_dark_titlebar();
    update_search_brush();
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
    focus_index_ = 0;
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void MainWindow::set_state(core::AppState state) noexcept {
    const bool was_capturing = state_ == core::AppState::Capturing;
    state_ = state;
    error_ = core::ErrorCode::None;
    if (!was_capturing && state == core::AppState::Capturing) {
        SetTimer(hwnd_, kAnimTimerId, kAnimIntervalMs, nullptr);
    }
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void MainWindow::set_error(core::ErrorCode error) noexcept {
    error_ = error;
    state_ = core::AppState::Failed;
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void MainWindow::add_history(std::string text, std::int64_t epoch_seconds) {
    if (text.empty()) {
        return;
    }
    const int length = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (length <= 0) {
        return;
    }
    std::wstring wide(static_cast<std::size_t>(length), L'\0');
    (void)MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), wide.data(), length);
    history_.insert(history_.begin(), HistoryEntry{std::move(wide), epoch_seconds});
    if (history_.size() > 20) {
        history_.pop_back();
    }
    refresh_search();
}

void MainWindow::clear_history() noexcept {
    history_.clear();
    refresh_search();
}

void MainWindow::refresh_search() noexcept {
    if (search_edit_ != nullptr) {
        wchar_t buffer[256]{};
        GetWindowTextW(search_edit_, buffer, ARRAYSIZE(buffer));
        search_query_ = buffer;
    }
    visible_history_.clear();
    for (std::size_t index = 0; index < history_.size(); ++index) {
        if (contains_case_insensitive(history_[index].text, search_query_)) {
            visible_history_.push_back(index);
        }
    }
    if (hwnd_ != nullptr) {
        InvalidateRect(hwnd_, nullptr, FALSE);
    }
}

void MainWindow::create_search_edit() noexcept {
    // Unlike the Direct2D content (scaled via render_content()'s transform), this is a real
    // Win32 child window: its position/size and font must be scaled to physical pixels by hand.
    const float scale = dpi_scale();
    if (search_font_ == nullptr) {
        search_font_ = CreateFontW(-static_cast<int>(std::lround(16.0F * scale)), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                                   DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                   DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
    }
    const Layout layout = compute_layout();
    const int x = static_cast<int>(std::lround((layout.search_pill.left + 40.0F) * scale));
    const int y = static_cast<int>(std::lround((layout.search_pill.top + 8.0F) * scale));
    const int w = static_cast<int>(std::lround((layout.search_pill.right - layout.search_pill.left - 56.0F) * scale));
    const int h = static_cast<int>(std::lround((layout.search_pill.bottom - layout.search_pill.top - 16.0F) * scale));
    search_edit_ = CreateWindowExW(0, L"EDIT", L"", WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL, x, y, w, h, hwnd_,
                                   reinterpret_cast<HMENU>(static_cast<INT_PTR>(kSearchEditId)), instance_, nullptr);
    if (search_edit_ == nullptr) {
        return;
    }
    if (search_font_ != nullptr) {
        SendMessageW(search_edit_, WM_SETFONT, reinterpret_cast<WPARAM>(search_font_), TRUE);
    }
    (void)SendMessageW(search_edit_, EM_SETCUEBANNER, TRUE, reinterpret_cast<LPARAM>(L"搜索历史记录"));
    update_search_brush();
}

void MainWindow::update_search_brush() noexcept {
    if (search_bk_brush_ != nullptr) {
        DeleteObject(search_bk_brush_);
        search_bk_brush_ = nullptr;
    }
    const Palette palette = palette_for(theme_);
    search_bk_brush_ = CreateSolidBrush(to_colorref(palette.panel));
    if (search_edit_ != nullptr) {
        InvalidateRect(search_edit_, nullptr, TRUE);
    }
}

void MainWindow::apply_dark_titlebar() noexcept {
    if (hwnd_ == nullptr) {
        return;
    }
    const Palette palette = palette_for(theme_);
    const BOOL dark = theme_ == ThemeMode::DarkGlass ? TRUE : FALSE;
    (void)DwmSetWindowAttribute(hwnd_, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(dark));
    const COLORREF caption = to_colorref(palette.canvas);
    (void)DwmSetWindowAttribute(hwnd_, DWMWA_CAPTION_COLOR, &caption, sizeof(caption));
    const COLORREF text_color = to_colorref(palette.ink);
    (void)DwmSetWindowAttribute(hwnd_, DWMWA_TEXT_COLOR, &text_color, sizeof(text_color));
    const DWM_WINDOW_CORNER_PREFERENCE corner = DWMWCP_ROUND;
    (void)DwmSetWindowAttribute(hwnd_, DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(corner));
}

void MainWindow::tick_animation() noexcept {
    bool active = false;
    if (opacity_ != target_opacity_) {
        constexpr float kStep = 0.25F;
        opacity_ = opacity_ < target_opacity_ ? std::min(target_opacity_, opacity_ + kStep)
                                              : std::max(target_opacity_, opacity_ - kStep);
        if (composition_ready_) {
            surface_.set_opacity(opacity_);
        }
        active = true;
    }
    if (state_ == core::AppState::Capturing) {
        active = true;
    }
    InvalidateRect(hwnd_, nullptr, FALSE);
    if (!active) {
        KillTimer(hwnd_, kAnimTimerId);
    }
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
            if (composition_ready_) {
                surface_.resize(LOWORD(lparam), HIWORD(lparam));
            } else {
                discard_resources();
                create_resources();
            }
        }
        return 0;
    case WM_DPICHANGED: {
        // Fires when the window moves to a monitor with a different scale factor. Resizing to
        // Windows' suggested rect (lparam) triggers the WM_SIZE handler above, which already
        // resizes the swap chain/fallback target to match -- only the DPI-dependent native
        // child controls (not reached by that path, or by render_content()'s transform) need
        // rebuilding here.
        dpi_ = HIWORD(wparam);
        const auto* suggested_rect = reinterpret_cast<const RECT*>(lparam);
        SetWindowPos(hwnd_, nullptr, suggested_rect->left, suggested_rect->top,
                    suggested_rect->right - suggested_rect->left, suggested_rect->bottom - suggested_rect->top,
                    SWP_NOZORDER | SWP_NOACTIVATE);
        if (search_edit_ != nullptr) {
            DestroyWindow(search_edit_);
            search_edit_ = nullptr;
        }
        if (search_font_ != nullptr) {
            DeleteObject(search_font_);
            search_font_ = nullptr;
        }
        create_search_edit();
        if (tooltip_ != nullptr) {
            DestroyWindow(tooltip_);
            tooltip_ = nullptr;
        }
        create_tooltips();
        InvalidateRect(hwnd_, nullptr, FALSE);
        return 0;
    }
    case WM_TIMER:
        if (wparam == kAnimTimerId) {
            tick_animation();
        }
        return 0;
    case WM_CTLCOLOREDIT: {
        if (reinterpret_cast<HWND>(lparam) == search_edit_) {
            const Palette palette = palette_for(theme_);
            HDC dc = reinterpret_cast<HDC>(wparam);
            SetTextColor(dc, to_colorref(palette.ink));
            SetBkColor(dc, to_colorref(palette.panel));
            SetBkMode(dc, OPAQUE);
            return reinterpret_cast<LRESULT>(search_bk_brush_);
        }
        break;
    }
    case WM_COMMAND:
        if (reinterpret_cast<HWND>(lparam) == search_edit_ && HIWORD(wparam) == EN_CHANGE) {
            refresh_search();
            return 0;
        }
        break;
    case WM_LBUTTONUP: {
        // lparam is real client-area physical pixels; build_focus_regions() stays in the same
        // 96-DPI-baseline space as compute_layout() (Direct2D's transform scales the drawing,
        // not this), so the click point has to come back down to that space to compare.
        const float scale = dpi_scale();
        const POINT point{static_cast<LONG>(std::lround(static_cast<float>(GET_X_LPARAM(lparam)) / scale)),
                          static_cast<LONG>(std::lround(static_cast<float>(GET_Y_LPARAM(lparam)) / scale))};
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
        break;
    }
    return DefWindowProcW(hwnd_, message, wparam, lparam);
}

std::vector<MainWindow::FocusRegion> MainWindow::build_focus_regions() {
    std::vector<FocusRegion> regions;
    const Layout layout = compute_layout();
    const auto to_rect = [](D2D1_RECT_F r) {
        return RECT{static_cast<LONG>(r.left), static_cast<LONG>(r.top), static_cast<LONG>(r.right), static_cast<LONG>(r.bottom)};
    };
    if (settings_open_) {
        regions.push_back({to_rect(layout.back_button), [this] {
                                settings_open_ = false;
                                focus_index_ = 0;
                                InvalidateRect(hwnd_, nullptr, FALSE);
                            }});
        regions.push_back({to_rect(layout.trigger_mode_card), [this] {
                                toggle_mode_ = !toggle_mode_;
                                if (trigger_mode_handler_) trigger_mode_handler_();
                                InvalidateRect(hwnd_, nullptr, FALSE);
                            }});
        return regions;
    }
    regions.push_back({to_rect(layout.theme_button), [this] { toggle_theme(); }});
    regions.push_back({to_rect(layout.settings_button), [this] { open_settings(); }});
    regions.push_back({to_rect(layout.clear_all_button), [this] {
                            if (history_clear_handler_) history_clear_handler_();
                        }});
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
    if (write_factory_ == nullptr) {
        (void)DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                  reinterpret_cast<IUnknown**>(write_factory_.GetAddressOf()));
    }
    // Physical client-area pixels, already accounting for the DPI resize in create() (or a
    // later WM_DPICHANGED) -- this is what the swap chain/render target must match, not the
    // 96-DPI-baseline kWidth/kHeight literals compute_layout() is authored against.
    RECT client_rect{};
    GetClientRect(hwnd_, &client_rect);
    const int client_width = std::max<int>(client_rect.right - client_rect.left, 1);
    const int client_height = std::max<int>(client_rect.bottom - client_rect.top, 1);
    if (!composition_ready_ && fallback_target_ == nullptr) {
        composition_ready_ = surface_.attach(hwnd_, client_width, client_height);
    }
    if (!composition_ready_ && fallback_target_ == nullptr) {
        if (fallback_factory_ == nullptr) {
            (void)D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, fallback_factory_.GetAddressOf());
        }
        if (fallback_factory_ != nullptr) {
            const auto properties = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT,
                                                                  D2D1::PixelFormat(DXGI_FORMAT_UNKNOWN, D2D1_ALPHA_MODE_IGNORE));
            const auto size = D2D1::SizeU(static_cast<UINT32>(client_width), static_cast<UINT32>(client_height));
            (void)fallback_factory_->CreateHwndRenderTarget(properties, D2D1::HwndRenderTargetProperties(hwnd_, size),
                                                            &fallback_target_);
        }
    }
}

void MainWindow::discard_resources() noexcept { fallback_target_.Reset(); }

void MainWindow::render() noexcept {
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

void MainWindow::render_content(ID2D1RenderTarget* target) noexcept {
    if (target == nullptr) {
        return;
    }
    const Palette palette = palette_for(theme_);
    const Layout layout = compute_layout();
    const float alpha = composition_ready_ ? opacity_ : 1.0F;

    ComPtr<ID2D1SolidColorBrush> brush;
    if (composition_ready_) {
        target->Clear(D2D1::ColorF(palette.canvas.r, palette.canvas.g, palette.canvas.b, alpha));
    } else {
        target->Clear(palette.canvas);
    }
    // Clear() ignores the render target's transform and always fills the whole physical
    // surface, so it's set here, after Clear and before any other drawing. Every rect/point
    // below is still authored against the 96-DPI compute_layout() space; this one transform
    // maps that whole space onto the real (possibly DPI-scaled) physical swap chain/render
    // target instead of every literal in this function needing to be scaled by hand.
    target->SetTransform(D2D1::Matrix3x2F::Scale(dpi_scale(), dpi_scale()));

    const auto fill_rounded = [&](D2D1_RECT_F rect, D2D1_COLOR_F color, float radius) {
        brush.Reset();
        (void)target->CreateSolidColorBrush(D2D1::ColorF(color.r, color.g, color.b, color.a * alpha), &brush);
        if (brush != nullptr) {
            target->FillRoundedRectangle(D2D1::RoundedRect(rect, radius, radius), brush.Get());
        }
    };

    // status dot + label
    brush.Reset();
    (void)target->CreateSolidColorBrush(D2D1::ColorF(palette.accent.r, palette.accent.g, palette.accent.b, alpha), &brush);
    if (brush != nullptr) {
        target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(layout.status_row.left + 5.0F, layout.status_row.top + 20.0F), 4.0F, 4.0F),
                            brush.Get());
    }
    draw_text(target, write_factory_.Get(), state_label(state_, error_),
             D2D1::RectF(layout.status_row.left + 16.0F, layout.status_row.top + 6.0F, layout.status_row.right, layout.status_row.top + 30.0F),
             13.0F, D2D1::ColorF(palette.muted.r, palette.muted.g, palette.muted.b, alpha), DWRITE_FONT_WEIGHT_SEMI_BOLD,
             DWRITE_TEXT_ALIGNMENT_LEADING);

    // top-right icon buttons
    fill_rounded(layout.theme_button, palette.panel, 12.0F);
    fill_rounded(layout.settings_button, palette.panel, 12.0F);
    draw_text(target, write_factory_.Get(), theme_ == ThemeMode::DarkGlass ? L"◑" : L"◐", layout.theme_button, 17.0F,
             D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, alpha), DWRITE_FONT_WEIGHT_NORMAL,
             DWRITE_TEXT_ALIGNMENT_CENTER, L"Segoe UI Symbol");
    draw_text(target, write_factory_.Get(), L"⚙", layout.settings_button, 18.0F,
             D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, alpha), DWRITE_FONT_WEIGHT_NORMAL,
             DWRITE_TEXT_ALIGNMENT_CENTER, L"Segoe UI Symbol");

    if (settings_open_) {
        fill_rounded(layout.back_button, palette.panel, 12.0F);
        draw_text(target, write_factory_.Get(), L"← 返回", layout.back_button, 13.0F,
                 D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, alpha), DWRITE_FONT_WEIGHT_SEMI_BOLD,
                 DWRITE_TEXT_ALIGNMENT_CENTER);

        fill_rounded(layout.trigger_mode_card, palette.panel, 16.0F);
        draw_text(target, write_factory_.Get(), L"触发方式", D2D1::RectF(layout.trigger_mode_card.left + 18, layout.trigger_mode_card.top + 14, layout.trigger_mode_card.right - 16, layout.trigger_mode_card.top + 40),
                 12.0F, D2D1::ColorF(palette.muted.r, palette.muted.g, palette.muted.b, alpha), DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT_LEADING);
        draw_text(target, write_factory_.Get(), toggle_mode_ ? L"切换模式 · 点按开始/停止" : L"按住模式 · 按住 Right Ctrl 说话",
                 D2D1::RectF(layout.trigger_mode_card.left + 18, layout.trigger_mode_card.top + 42, layout.trigger_mode_card.right - 16, layout.trigger_mode_card.top + 74),
                 15.0F, D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, alpha), DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING);

        fill_rounded(layout.appearance_card, palette.panel, 16.0F);
        draw_text(target, write_factory_.Get(), L"外观", D2D1::RectF(layout.appearance_card.left + 18, layout.appearance_card.top + 14, layout.appearance_card.right - 16, layout.appearance_card.top + 40),
                 12.0F, D2D1::ColorF(palette.muted.r, palette.muted.g, palette.muted.b, alpha), DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT_LEADING);
        draw_text(target, write_factory_.Get(), std::wstring(theme_name(theme_)) + L" · 点击顶部图标切换",
                 D2D1::RectF(layout.appearance_card.left + 18, layout.appearance_card.top + 42, layout.appearance_card.right - 16, layout.appearance_card.top + 74),
                 15.0F, D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, alpha), DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING);

        fill_rounded(layout.stats_card, palette.panel, 16.0F);
        draw_text(target, write_factory_.Get(), L"使用统计", D2D1::RectF(layout.stats_card.left + 18, layout.stats_card.top + 14, layout.stats_card.right - 16, layout.stats_card.top + 40),
                 12.0F, D2D1::ColorF(palette.muted.r, palette.muted.g, palette.muted.b, alpha), DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT_LEADING);
        draw_text(target, write_factory_.Get(), L"今日  " + std::to_wstring(stats_.dictations_today) + L" 次",
                 D2D1::RectF(layout.stats_card.left + 18, layout.stats_card.top + 44, layout.stats_card.right - 16, layout.stats_card.top + 68),
                 14.0F, D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, alpha), DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING);
        draw_text(target, write_factory_.Get(), L"累计  " + std::to_wstring(stats_.dictations_total) + L" 次",
                 D2D1::RectF(layout.stats_card.left + 18, layout.stats_card.top + 72, layout.stats_card.right - 16, layout.stats_card.top + 96),
                 14.0F, D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, alpha), DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING);
        draw_text(target, write_factory_.Get(), L"累计字数  " + std::to_wstring(stats_.characters_total),
                 D2D1::RectF(layout.stats_card.left + 18, layout.stats_card.top + 100, layout.stats_card.right - 16, layout.stats_card.top + 124),
                 14.0F, D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, alpha), DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING);
    } else {
        // search pill background (the real EDIT child sits inset on top of this)
        fill_rounded(layout.search_pill, palette.panel, (layout.search_pill.bottom - layout.search_pill.top) / 2.0F);
        const float lens_cx = layout.search_pill.left + 22.0F;
        const float lens_cy = (layout.search_pill.top + layout.search_pill.bottom) / 2.0F;
        brush.Reset();
        (void)target->CreateSolidColorBrush(D2D1::ColorF(palette.muted.r, palette.muted.g, palette.muted.b, alpha), &brush);
        if (brush != nullptr) {
            target->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(lens_cx - 1.0F, lens_cy - 1.0F), 5.0F, 5.0F), brush.Get(), 1.6F);
            target->DrawLine(D2D1::Point2F(lens_cx + 3.0F, lens_cy + 3.0F), D2D1::Point2F(lens_cx + 7.0F, lens_cy + 7.0F), brush.Get(), 1.6F);
        }

        target->PushAxisAlignedClip(layout.history_area, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        if (visible_history_.empty()) {
            draw_text(target, write_factory_.Get(), history_.empty() ? L"还没有识别记录" : L"没有匹配结果",
                     D2D1::RectF(layout.history_area.left, layout.history_area.top + 12, layout.history_area.right, layout.history_area.top + 40),
                     13.0F, D2D1::ColorF(palette.muted.r, palette.muted.g, palette.muted.b, alpha), DWRITE_FONT_WEIGHT_NORMAL,
                     DWRITE_TEXT_ALIGNMENT_LEADING);
        } else {
            float y = layout.history_area.top;
            for (const auto index : visible_history_) {
                if (y + kCardHeight > layout.history_area.bottom) {
                    break;
                }
                const D2D1_RECT_F card = D2D1::RectF(layout.history_area.left, y, layout.history_area.right, y + kCardHeight);
                fill_rounded(card, palette.panel_alt, 14.0F);
                draw_text(target, write_factory_.Get(), history_[index].text,
                         D2D1::RectF(card.left + 16, card.top + 10, card.right - 16, card.top + 36), 13.5F,
                         D2D1::ColorF(palette.ink.r, palette.ink.g, palette.ink.b, alpha), DWRITE_FONT_WEIGHT_NORMAL,
                         DWRITE_TEXT_ALIGNMENT_LEADING);
                brush.Reset();
                (void)target->CreateSolidColorBrush(D2D1::ColorF(palette.border.r, palette.border.g, palette.border.b, 0.6F * alpha), &brush);
                if (brush != nullptr) {
                    target->DrawLine(D2D1::Point2F(card.left + 16, card.top + 42), D2D1::Point2F(card.right - 16, card.top + 42), brush.Get(), 1.0F);
                }
                draw_text(target, write_factory_.Get(), format_time(history_[index].epoch_seconds),
                         D2D1::RectF(card.left + 16, card.top + 46, card.right - 16, card.top + 62), 11.0F,
                         D2D1::ColorF(palette.muted.r, palette.muted.g, palette.muted.b, alpha), DWRITE_FONT_WEIGHT_NORMAL,
                         DWRITE_TEXT_ALIGNMENT_LEADING);
                y += kCardHeight + kCardGap;
            }
        }
        target->PopAxisAlignedClip();

        const D2D1_COLOR_F button_color = button_fill_color(state_, palette);
        draw_glow_circle(target, layout.record_button.point, layout.record_button.radiusX, palette.glow, alpha * 0.5F);
        brush.Reset();
        (void)target->CreateSolidColorBrush(D2D1::ColorF(button_color.r, button_color.g, button_color.b, alpha), &brush);
        if (brush != nullptr) {
            target->FillEllipse(layout.record_button, brush.Get());
        }

        draw_text(target, write_factory_.Get(), L"按住 Right Ctrl 说话，松开自动输入到当前窗口", layout.hint_text, 11.5F,
                 D2D1::ColorF(palette.muted.r, palette.muted.g, palette.muted.b, alpha), DWRITE_FONT_WEIGHT_NORMAL,
                 DWRITE_TEXT_ALIGNMENT_LEADING);
        draw_text(target, write_factory_.Get(), L"清空", layout.clear_all_button, 13.0F,
                 D2D1::ColorF(palette.accent.r, palette.accent.g, palette.accent.b, alpha), DWRITE_FONT_WEIGHT_SEMI_BOLD,
                 DWRITE_TEXT_ALIGNMENT_TRAILING);
    }

    if (focus_index_ >= 0 && GetFocus() == hwnd_) {
        const auto regions = build_focus_regions();
        if (focus_index_ < static_cast<int>(regions.size())) {
            const RECT& region = regions[static_cast<std::size_t>(focus_index_)].rect;
            brush.Reset();
            (void)target->CreateSolidColorBrush(D2D1::ColorF(palette.accent.r, palette.accent.g, palette.accent.b, alpha), &brush);
            if (brush != nullptr) {
                target->DrawRectangle(D2D1::RectF(static_cast<float>(region.left) - 2.0F, static_cast<float>(region.top) - 2.0F,
                                                  static_cast<float>(region.right) + 2.0F, static_cast<float>(region.bottom) + 2.0F),
                                      brush.Get(), 2.0F);
            }
        }
    }
}

void MainWindow::create_tooltips() noexcept {
    INITCOMMONCONTROLSEX controls{sizeof(INITCOMMONCONTROLSEX), ICC_WIN95_CLASSES};
    (void)InitCommonControlsEx(&controls);
    tooltip_ = CreateWindowExW(WS_EX_TOPMOST, TOOLTIPS_CLASSW, nullptr, WS_POPUP | TTS_ALWAYSTIP, CW_USEDEFAULT, CW_USEDEFAULT,
                               CW_USEDEFAULT, CW_USEDEFAULT, hwnd_, nullptr, instance_, nullptr);
    if (tooltip_ == nullptr) {
        return;
    }
    const Layout layout = compute_layout();
    // Like the search edit box, TOOLINFOW::rect is real Win32 client-area pixels, not
    // Direct2D-transformed DIPs -- scale explicitly rather than passing the raw layout rect.
    const float scale = dpi_scale();
    const auto to_rect = [scale](D2D1_RECT_F r) {
        return RECT{static_cast<LONG>(std::lround(r.left * scale)), static_cast<LONG>(std::lround(r.top * scale)),
                    static_cast<LONG>(std::lround(r.right * scale)), static_cast<LONG>(std::lround(r.bottom * scale))};
    };
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
    add_tooltip(1, to_rect(layout.theme_button), const_cast<wchar_t*>(L"切换主题"));
    add_tooltip(2, to_rect(layout.settings_button), const_cast<wchar_t*>(L"打开设置"));
    add_tooltip(3, to_rect(layout.clear_all_button), const_cast<wchar_t*>(L"清空历史"));
    add_tooltip(4, to_rect(layout.back_button), const_cast<wchar_t*>(L"返回主界面"));
    add_tooltip(5, to_rect(layout.trigger_mode_card), const_cast<wchar_t*>(L"切换按住或切换录音"));
    const RECT record_rect{
        static_cast<LONG>(std::lround((layout.record_button.point.x - layout.record_button.radiusX) * scale)),
        static_cast<LONG>(std::lround((layout.record_button.point.y - layout.record_button.radiusY) * scale)),
        static_cast<LONG>(std::lround((layout.record_button.point.x + layout.record_button.radiusX) * scale)),
        static_cast<LONG>(std::lround((layout.record_button.point.y + layout.record_button.radiusY) * scale))};
    add_tooltip(6, record_rect, const_cast<wchar_t*>(L"按住 Right Ctrl 说话"));
}

}  // namespace aevocis::ui
