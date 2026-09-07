#pragma once

#include <windows.h>

namespace aevocis::platform::windows {

constexpr UINT kKeyboardMessage = WM_APP + 1;
constexpr UINT kTrayMessage = WM_APP + 2;
constexpr UINT kStatusMessage = WM_APP + 3;
constexpr UINT kHistoryMessage = WM_APP + 4;
constexpr UINT kRecognizerMessage = WM_APP + 5;
constexpr UINT kCommandMessage = WM_APP + 6;
constexpr UINT kCommandToggle = 41001;
constexpr UINT kCommandTheme = 41002;
constexpr UINT kCommandSettings = 41003;
constexpr UINT kCommandQuit = 41004;
constexpr UINT kCommandAutostart = 41005;

}  // namespace aevocis::platform::windows
