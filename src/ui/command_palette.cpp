#include "aevocis/ui/command_palette.hpp"

#include <commctrl.h>
#include <dwmapi.h>
#include <uxtheme.h>

#include <algorithm>
#include <cwctype>

namespace aevocis::ui {

namespace {

constexpr wchar_t kClassName[] = L"AevocisNativeCppCommandPalette";
constexpr int kWidth = 420;
constexpr int kHeight = 320;
constexpr UINT_PTR kEditSubclassId = 1;
constexpr COLORREF kBackground = RGB(0x1C, 0x1C, 0x1E);
constexpr COLORREF kRow = RGB(0x24, 0x24, 0x26);
constexpr COLORREF kSelected = RGB(0x2C, 0x2C, 0x2E);
constexpr COLORREF kText = RGB(0xF5, 0xF5, 0xF7);
constexpr COLORREF kAccent = RGB(0x5F, 0xD9, 0xC3);

[[nodiscard]] bool contains_case_insensitive(std::wstring_view haystack, std::wstring_view needle) noexcept {
    if (needle.empty()) {
        return true;
    }
    auto to_lower = [](wchar_t ch) { return static_cast<wchar_t>(std::towlower(ch)); };
    const auto it = std::search(haystack.begin(), haystack.end(), needle.begin(), needle.end(),
                                [&](wchar_t a, wchar_t b) { return to_lower(a) == to_lower(b); });
    return it != haystack.end();
}

}  // namespace

CommandPalette::CommandPalette(HINSTANCE instance) noexcept : instance_(instance) {}

CommandPalette::~CommandPalette() {
    if (hwnd_ != nullptr) {
        DestroyWindow(hwnd_);
    }
    if (font_ != nullptr) {
        DeleteObject(font_);
    }
    if (background_brush_ != nullptr) {
        DeleteObject(background_brush_);
    }
}

void CommandPalette::set_commands(std::vector<PaletteCommand> commands) { commands_ = std::move(commands); }

bool CommandPalette::visible() const noexcept { return hwnd_ != nullptr && IsWindowVisible(hwnd_) != FALSE; }

void CommandPalette::toggle(HWND owner) noexcept {
    if (visible()) {
        close();
        return;
    }
    if (hwnd_ == nullptr) {
        if (background_brush_ == nullptr) {
            background_brush_ = CreateSolidBrush(kBackground);
        }
        WNDCLASSEXW window_class{};
        window_class.cbSize = sizeof(window_class);
        window_class.hInstance = instance_;
        window_class.lpfnWndProc = window_proc;
        window_class.lpszClassName = kClassName;
        window_class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        window_class.hbrBackground = background_brush_;
        if (RegisterClassExW(&window_class) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
            return;
        }
        RECT work{};
        SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
        const int x = (work.left + work.right - kWidth) / 2;
        const int y = (work.top + work.bottom - kHeight) / 3;
        hwnd_ = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, kClassName, L"命令面板", WS_POPUP | WS_BORDER, x, y,
                                kWidth, kHeight, owner, nullptr, instance_, this);
        if (hwnd_ == nullptr) {
            return;
        }
        const BOOL dark = TRUE;
        (void)DwmSetWindowAttribute(hwnd_, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(dark));
        const DWM_WINDOW_CORNER_PREFERENCE corner = DWMWCP_ROUND;
        (void)DwmSetWindowAttribute(hwnd_, DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(corner));
        create_controls();
    }
    SetWindowTextW(edit_, L"");
    refresh_filter();
    ShowWindow(hwnd_, SW_SHOW);
    SetForegroundWindow(hwnd_);
    SetFocus(edit_);
}

void CommandPalette::close() noexcept {
    if (hwnd_ != nullptr) {
        ShowWindow(hwnd_, SW_HIDE);
    }
}

void CommandPalette::create_controls() noexcept {
    edit_ = CreateWindowExW(0, L"EDIT", L"", WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL, 14, 14, kWidth - 28, 28, hwnd_,
                            nullptr, instance_, nullptr);
    list_ = CreateWindowExW(0, L"LISTBOX", L"",
                            WS_CHILD | WS_VISIBLE | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_NOINTEGRALHEIGHT | WS_VSCROLL,
                            14, 50, kWidth - 28, kHeight - 64, hwnd_, nullptr, instance_, nullptr);
    (void)SetWindowSubclass(edit_, edit_subclass_proc, kEditSubclassId, reinterpret_cast<DWORD_PTR>(this));
    (void)SetWindowTheme(list_, L"DarkMode_Explorer", nullptr);
    if (font_ == nullptr) {
        font_ = CreateFontW(-16, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                            CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
    }
    SendMessageW(edit_, WM_SETFONT, reinterpret_cast<WPARAM>(font_), TRUE);
    SendMessageW(list_, WM_SETFONT, reinterpret_cast<WPARAM>(font_), TRUE);
    (void)SendMessageW(list_, LB_SETITEMHEIGHT, 0, 30);
}

void CommandPalette::refresh_filter() noexcept {
    wchar_t buffer[256]{};
    GetWindowTextW(edit_, buffer, ARRAYSIZE(buffer));
    const std::wstring_view filter(buffer);
    SendMessageW(list_, LB_RESETCONTENT, 0, 0);
    filtered_indices_.clear();
    for (std::size_t index = 0; index < commands_.size(); ++index) {
        if (contains_case_insensitive(commands_[index].label, filter)) {
            SendMessageW(list_, LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(commands_[index].label.c_str()));
            filtered_indices_.push_back(index);
        }
    }
    if (!filtered_indices_.empty()) {
        SendMessageW(list_, LB_SETCURSEL, 0, 0);
    }
}

void CommandPalette::draw_item(const DRAWITEMSTRUCT& item) const noexcept {
    if (item.itemID == static_cast<UINT>(-1)) {
        return;
    }
    const bool selected = (item.itemState & ODS_SELECTED) != 0;
    HBRUSH row_brush = CreateSolidBrush(selected ? kSelected : kRow);
    FillRect(item.hDC, &item.rcItem, row_brush);
    DeleteObject(row_brush);
    if (selected) {
        HBRUSH accent_brush = CreateSolidBrush(kAccent);
        RECT stripe = item.rcItem;
        stripe.right = stripe.left + 3;
        FillRect(item.hDC, &stripe, accent_brush);
        DeleteObject(accent_brush);
    }
    wchar_t text[256]{};
    const LRESULT length = SendMessageW(item.hwndItem, LB_GETTEXT, item.itemID, reinterpret_cast<LPARAM>(text));
    if (length != LB_ERR) {
        RECT text_rect = item.rcItem;
        text_rect.left += 16;
        SetBkMode(item.hDC, TRANSPARENT);
        SetTextColor(item.hDC, kText);
        DrawTextW(item.hDC, text, -1, &text_rect, DT_SINGLELINE | DT_VCENTER | DT_LEFT | DT_END_ELLIPSIS);
    }
}

void CommandPalette::activate_selected() noexcept {
    const LRESULT selection = SendMessageW(list_, LB_GETCURSEL, 0, 0);
    if (selection == LB_ERR || static_cast<std::size_t>(selection) >= filtered_indices_.size()) {
        return;
    }
    const auto command_index = filtered_indices_[static_cast<std::size_t>(selection)];
    close();
    if (commands_[command_index].activate) {
        commands_[command_index].activate();
    }
}

LRESULT CALLBACK CommandPalette::edit_subclass_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR,
                                                    DWORD_PTR ref_data) noexcept {
    auto* self = reinterpret_cast<CommandPalette*>(ref_data);
    if (message == WM_KEYDOWN && self != nullptr) {
        if (wparam == VK_DOWN || wparam == VK_UP) {
            const LRESULT current = SendMessageW(self->list_, LB_GETCURSEL, 0, 0);
            const LRESULT count = SendMessageW(self->list_, LB_GETCOUNT, 0, 0);
            if (count > 0) {
                LRESULT next = current == LB_ERR ? 0 : current + (wparam == VK_DOWN ? 1 : -1);
                next = std::clamp<LRESULT>(next, 0, count - 1);
                SendMessageW(self->list_, LB_SETCURSEL, static_cast<WPARAM>(next), 0);
            }
            return 0;
        }
        if (wparam == VK_RETURN) {
            self->activate_selected();
            return 0;
        }
        if (wparam == VK_ESCAPE) {
            self->close();
            return 0;
        }
    }
    if (message == WM_CHAR && (wparam == VK_RETURN || wparam == VK_ESCAPE)) {
        return 0;
    }
    return DefSubclassProc(hwnd, message, wparam, lparam);
}

LRESULT CALLBACK CommandPalette::window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept {
    auto* self = reinterpret_cast<CommandPalette*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* create = reinterpret_cast<const CREATESTRUCTW*>(lparam);
        self = static_cast<CommandPalette*>(create->lpCreateParams);
        self->hwnd_ = hwnd;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    return self != nullptr ? self->handle_message(message, wparam, lparam) : DefWindowProcW(hwnd, message, wparam, lparam);
}

LRESULT CommandPalette::handle_message(UINT message, WPARAM wparam, LPARAM lparam) noexcept {
    switch (message) {
    case WM_CTLCOLOREDIT:
    case WM_CTLCOLORLISTBOX: {
        HDC dc = reinterpret_cast<HDC>(wparam);
        SetTextColor(dc, kText);
        SetBkColor(dc, kBackground);
        SetBkMode(dc, OPAQUE);
        return reinterpret_cast<LRESULT>(background_brush_);
    }
    case WM_MEASUREITEM: {
        auto* measure = reinterpret_cast<MEASUREITEMSTRUCT*>(lparam);
        if (measure != nullptr) {
            measure->itemHeight = 30;
        }
        return TRUE;
    }
    case WM_DRAWITEM: {
        const auto* item = reinterpret_cast<const DRAWITEMSTRUCT*>(lparam);
        if (item != nullptr && item->CtlID == 0 && item->hwndItem == list_) {
            draw_item(*item);
            return TRUE;
        }
        break;
    }
    case WM_COMMAND:
        if (reinterpret_cast<HWND>(lparam) == edit_ && HIWORD(wparam) == EN_CHANGE) {
            refresh_filter();
            return 0;
        }
        if (reinterpret_cast<HWND>(lparam) == list_ && HIWORD(wparam) == LBN_DBLCLK) {
            activate_selected();
            return 0;
        }
        break;
    case WM_ACTIVATE:
        if (LOWORD(wparam) == WA_INACTIVE) {
            close();
        }
        return 0;
    case WM_CLOSE:
        close();
        return 0;
    default:
        break;
    }
    return DefWindowProcW(hwnd_, message, wparam, lparam);
}

}  // namespace aevocis::ui
