#pragma once

#include <functional>
#include <string>
#include <vector>

#include <windows.h>

namespace aevocis::ui {

struct PaletteCommand {
    std::wstring label;
    std::function<void()> activate;
};

// C3: a small filterable command list (Ctrl+Shift+P by convention) that jumps straight to any
// action the tray menu / settings panel / hotkeys already expose, for users who'd rather type a
// few letters than hunt through the UI. Built from native Edit + ListBox controls rather than
// custom Direct2D drawing like the rest of the app -- a utility popup that only needs to exist
// for a few seconds doesn't carry its own render-target lifecycle, and native list/edit
// controls give correct IME and keyboard behavior for free.
class CommandPalette {
public:
    explicit CommandPalette(HINSTANCE instance) noexcept;
    CommandPalette(const CommandPalette&) = delete;
    CommandPalette& operator=(const CommandPalette&) = delete;
    ~CommandPalette();

    void set_commands(std::vector<PaletteCommand> commands);
    void toggle(HWND owner) noexcept;
    [[nodiscard]] bool visible() const noexcept;

private:
    static LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    static LRESULT CALLBACK edit_subclass_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR id,
                                                DWORD_PTR ref_data) noexcept;
    LRESULT handle_message(UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    void create_controls() noexcept;
    void refresh_filter() noexcept;
    void activate_selected() noexcept;
    void close() noexcept;

    HINSTANCE instance_{};
    HWND hwnd_{};
    HWND edit_{};
    HWND list_{};
    std::vector<PaletteCommand> commands_;
    std::vector<std::size_t> filtered_indices_;
};

}  // namespace aevocis::ui
