#pragma once

#include <windows.h>

namespace aevocis::platform::windows {

class SingleInstance {
public:
    explicit SingleInstance(const wchar_t* name) noexcept;
    SingleInstance(const SingleInstance&) = delete;
    SingleInstance& operator=(const SingleInstance&) = delete;
    ~SingleInstance();

    [[nodiscard]] bool primary() const noexcept { return primary_; }

private:
    HANDLE mutex_{};
    bool primary_{false};
};

}  // namespace aevocis::platform::windows
