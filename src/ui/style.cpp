#include "aevocis/ui/style.hpp"

namespace aevocis::ui {

Palette palette_for(ThemeMode theme) noexcept {
    switch (theme) {
    case ThemeMode::DarkGlass:
        return {D2D1::ColorF(0x1C1C1E), D2D1::ColorF(0x2C2C2E), D2D1::ColorF(0x242426),
                D2D1::ColorF(0xF5F5F7), D2D1::ColorF(0x9A9AA0), D2D1::ColorF(0x5FD9C3),
                D2D1::ColorF(0x3A3A3C), D2D1::ColorF(0xFFFFFF), D2D1::ColorF(0xFF453A)};
    case ThemeMode::HighContrast:
        return {D2D1::ColorF(0xFFFFFF), D2D1::ColorF(0xFFFFFF), D2D1::ColorF(0xF0F0F0),
                D2D1::ColorF(0x000000), D2D1::ColorF(0x1A1A1A), D2D1::ColorF(0x0047AB),
                D2D1::ColorF(0x000000), D2D1::ColorF(0x0047AB), D2D1::ColorF(0xC81E1E)};
    case ThemeMode::Sepia:
        return {D2D1::ColorF(0xEFE3CE), D2D1::ColorF(0xF7EEDD), D2D1::ColorF(0xF1E4CB),
                D2D1::ColorF(0x4A3B28), D2D1::ColorF(0x8A7657), D2D1::ColorF(0xB8763D),
                D2D1::ColorF(0xD9C7A3), D2D1::ColorF(0xB8763D), D2D1::ColorF(0xA8452E)};
    case ThemeMode::Paper:
    default:
        return {D2D1::ColorF(0xF5F2EA), D2D1::ColorF(0xFFFFFF), D2D1::ColorF(0xF3F0E8),
                D2D1::ColorF(0x22282E), D2D1::ColorF(0x767F87), D2D1::ColorF(0x2A9D8F),
                D2D1::ColorF(0xE1DBCB), D2D1::ColorF(0x2A9D8F), D2D1::ColorF(0xE0533D)};
    }
}

const wchar_t* theme_name(ThemeMode theme) noexcept {
    switch (theme) {
    case ThemeMode::DarkGlass: return L"暗色玻璃";
    case ThemeMode::HighContrast: return L"高对比度";
    case ThemeMode::Sepia: return L"暖褐色";
    case ThemeMode::Paper:
    default: return L"纸感";
    }
}

ThemeMode next_theme(ThemeMode theme) noexcept {
    switch (theme) {
    case ThemeMode::DarkGlass: return ThemeMode::HighContrast;
    case ThemeMode::HighContrast: return ThemeMode::Sepia;
    case ThemeMode::Sepia: return ThemeMode::Paper;
    case ThemeMode::Paper:
    default: return ThemeMode::DarkGlass;
    }
}

}  // namespace aevocis::ui
