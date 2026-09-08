#pragma once

#include <d2d1.h>

#include <cstdint>

namespace aevocis::ui {

enum class ThemeMode : std::uint8_t { Paper, DarkGlass, HighContrast, Sepia };

struct Palette {
    D2D1_COLOR_F canvas;
    D2D1_COLOR_F panel;
    D2D1_COLOR_F panel_alt;
    D2D1_COLOR_F ink;
    D2D1_COLOR_F muted;
    D2D1_COLOR_F accent;
    D2D1_COLOR_F border;
    D2D1_COLOR_F glow;
    D2D1_COLOR_F record;
};

[[nodiscard]] Palette palette_for(ThemeMode theme) noexcept;
[[nodiscard]] const wchar_t* theme_name(ThemeMode theme) noexcept;
[[nodiscard]] ThemeMode next_theme(ThemeMode theme) noexcept;

constexpr float kCornerLarge = 20.0F;
constexpr float kCornerMedium = 14.0F;
constexpr float kCornerPill = 999.0F;

}  // namespace aevocis::ui
