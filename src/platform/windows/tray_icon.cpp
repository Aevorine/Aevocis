#include "aevocis/platform/windows/tray_icon.hpp"

#include "aevocis/platform/windows/messages.hpp"

#include <shellapi.h>
#include <strsafe.h>

namespace aevocis::platform::windows {

TrayIcon::~TrayIcon() { remove(); }

bool TrayIcon::install(HWND owner, UINT callback_message, HICON icon) noexcept {
    remove();
    if (owner == nullptr || callback_message == 0) {
        return false;
    }
    NOTIFYICONDATAW data{};
    data.cbSize = sizeof(data);
    data.hWnd = owner;
    data.uID = 1;
    data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    data.uCallbackMessage = callback_message;
    data.hIcon = icon != nullptr ? icon : LoadIconW(nullptr, IDI_APPLICATION);
    (void)StringCchCopyW(data.szTip, ARRAYSIZE(data.szTip), L"Aevocis");
    installed_ = Shell_NotifyIconW(NIM_ADD, &data) != FALSE;
    if (installed_) {
        owner_ = owner;
        callback_message_ = callback_message;
        icon_ = data.hIcon;
    }
    return installed_;
}

void TrayIcon::remove() noexcept {
    if (installed_) {
        NOTIFYICONDATAW data{};
        data.cbSize = sizeof(data);
        data.hWnd = owner_;
        data.uID = 1;
        (void)Shell_NotifyIconW(NIM_DELETE, &data);
        installed_ = false;
        owner_ = nullptr;
        callback_message_ = 0;
        icon_ = nullptr;
    }
}

void TrayIcon::show_menu() const noexcept {
    if (!installed_) {
        return;
    }
    POINT point{};
    if (GetCursorPos(&point) == FALSE) {
        return;
    }
    HMENU menu = CreatePopupMenu();
    if (menu == nullptr) {
        return;
    }
    (void)AppendMenuW(menu, MF_STRING, kCommandToggle, L"显示 / 隐藏");
    (void)AppendMenuW(menu, MF_STRING, kCommandTheme, L"切换主题");
    (void)AppendMenuW(menu, MF_STRING, kCommandSettings, L"设置");
     (void)AppendMenuW(menu, MF_STRING, kCommandAutostart, L"开机启动");
     (void)AppendMenuW(menu, MF_STRING, kCommandUpdate, L"检查更新");
    (void)AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    (void)AppendMenuW(menu, MF_STRING, kCommandQuit, L"退出");
    (void)SetForegroundWindow(owner_);
    const UINT command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY, point.x, point.y, 0, owner_, nullptr);
    if (command != 0) {
        (void)PostMessageW(owner_, WM_COMMAND, command, 0);
    }
    (void)DestroyMenu(menu);
}

}  // namespace aevocis::platform::windows
