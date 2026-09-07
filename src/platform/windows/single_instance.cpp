#include "aevocis/platform/windows/single_instance.hpp"

namespace aevocis::platform::windows {

SingleInstance::SingleInstance(const wchar_t* name) noexcept : mutex_(CreateMutexW(nullptr, TRUE, name)) {
    primary_ = mutex_ != nullptr && GetLastError() != ERROR_ALREADY_EXISTS;
}

SingleInstance::~SingleInstance() {
    if (mutex_ != nullptr) {
        (void)ReleaseMutex(mutex_);
        (void)CloseHandle(mutex_);
    }
}

}  // namespace aevocis::platform::windows
