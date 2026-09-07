#pragma once

#include "aevocis/core/state.hpp"

#include <condition_variable>
#include <functional>
#include <mutex>
#include <optional>
#include <stop_token>
#include <thread>

namespace aevocis::core {

class TaskCoordinator {
public:
    [[nodiscard]] std::optional<SessionId> try_start(TargetToken target);
    [[nodiscard]] bool transition(SessionId id, AppState next, ErrorCode error = ErrorCode::None);
    [[nodiscard]] bool request_cancel(SessionId id);
    [[nodiscard]] bool cancel_requested(SessionId id) const;
    [[nodiscard]] bool active() const;
    [[nodiscard]] SessionSnapshot snapshot() const;
    void finish(SessionId id);

private:
    mutable std::mutex mutex_;
    SessionSnapshot snapshot_{};
    SessionId next_id_{1};
};

class SingleTaskScheduler {
public:
    using Job = std::function<void(std::stop_token, SessionId)>;

    SingleTaskScheduler() = default;
    SingleTaskScheduler(const SingleTaskScheduler&) = delete;
    SingleTaskScheduler& operator=(const SingleTaskScheduler&) = delete;
    ~SingleTaskScheduler();

    [[nodiscard]] bool submit(TargetToken target, Job job);
    [[nodiscard]] bool transition(SessionId id, AppState next, ErrorCode error = ErrorCode::None);
    [[nodiscard]] bool cancel_requested(SessionId id) const;
    [[nodiscard]] bool active() const;
    void request_cancel();
    void wait();

private:
    TaskCoordinator coordinator_;
    mutable std::mutex worker_mutex_;
    std::jthread worker_;
};

}  // namespace aevocis::core
