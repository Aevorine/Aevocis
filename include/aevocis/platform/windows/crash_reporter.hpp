#pragma once

namespace aevocis::platform::windows {

class CrashReporter {
public:
    static void install() noexcept;
    // E2: asks Windows Restart Manager to relaunch the process after a crash or hang, without
    // any extra watchdog process (a second process would itself be new attack surface and
    // resource cost). Excludes the reboot case so this never competes with the existing
    // HKCU Run autostart toggle for who launches the app after a system restart.
    static void register_auto_restart() noexcept;
};

}  // namespace aevocis::platform::windows
