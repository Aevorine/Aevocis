#include "aevocis/core/task_scheduler.hpp"

#include <utility>

namespace aevocis::core {

namespace {

[[nodiscard]] bool valid_transition(const SessionSnapshot& snapshot, AppState next) noexcept {
    switch (snapshot.state) {
    case AppState::Idle:
        return next == AppState::Starting;
    case AppState::Starting:
        return next == AppState::Capturing || next == AppState::Injecting || next == AppState::Cancelled || next == AppState::Failed;
    case AppState::Capturing:
        return next == AppState::Recognizing || next == AppState::Cancelled || next == AppState::Failed;
    case AppState::Recognizing:
        return next == AppState::PostProcessing || next == AppState::Cancelled || next == AppState::Failed;
    case AppState::PostProcessing:
        return next == AppState::Confirming || next == AppState::Injecting || next == AppState::Cancelled || next == AppState::Failed;
    case AppState::Confirming:
        return next == AppState::Injecting || next == AppState::Cancelled || next == AppState::Failed;
    case AppState::Injecting:
        return next == AppState::Idle || next == AppState::Failed || next == AppState::Cancelled;
    case AppState::Cancelled:
    case AppState::Failed:
        return next == AppState::Idle;
    }
    return false;
}

}  // namespace

std::optional<SessionId> TaskCoordinator::try_start(TargetToken target) {
    std::scoped_lock lock(mutex_);
    if (snapshot_.state != AppState::Idle || !target.valid()) {
        return std::nullopt;
    }
    snapshot_ = SessionSnapshot{next_id_++, AppState::Starting, target, ErrorCode::None, false};
    return snapshot_.id;
}

bool TaskCoordinator::transition(SessionId id, AppState next, ErrorCode error) {
    std::scoped_lock lock(mutex_);
    if (snapshot_.id != id || !valid_transition(snapshot_, next)) {
        return false;
    }
    snapshot_.state = next;
    snapshot_.error = error;
    return true;
}

bool TaskCoordinator::request_cancel(SessionId id) {
    std::scoped_lock lock(mutex_);
    if (snapshot_.id != id || snapshot_.state == AppState::Idle || snapshot_.state == AppState::Failed ||
        snapshot_.state == AppState::Cancelled) {
        return false;
    }
    snapshot_.cancel_requested = true;
    return true;
}

bool TaskCoordinator::cancel_requested(SessionId id) const {
    std::scoped_lock lock(mutex_);
    return snapshot_.id == id && snapshot_.cancel_requested;
}

bool TaskCoordinator::active() const {
    std::scoped_lock lock(mutex_);
    return snapshot_.state != AppState::Idle;
}

SessionSnapshot TaskCoordinator::snapshot() const {
    std::scoped_lock lock(mutex_);
    return snapshot_;
}

void TaskCoordinator::finish(SessionId id) {
    std::scoped_lock lock(mutex_);
    if (snapshot_.id != id) {
        return;
    }
    snapshot_.state = AppState::Idle;
    snapshot_.error = ErrorCode::None;
    snapshot_.cancel_requested = false;
}

SingleTaskScheduler::~SingleTaskScheduler() { wait(); }

bool SingleTaskScheduler::submit(TargetToken target, Job job) {
    if (!job) {
        return false;
    }
    std::scoped_lock lock(worker_mutex_);
    const auto session = coordinator_.try_start(target);
    if (!session.has_value()) {
        return false;
    }
    if (worker_.joinable()) {
        worker_.join();
    }
    worker_ = std::jthread([this, session = *session, job = std::move(job)](std::stop_token stop) {
        try {
            job(std::move(stop), session);
        } catch (...) {
            (void)coordinator_.transition(session, AppState::Failed, ErrorCode::RecognitionFailed);
        }
        coordinator_.finish(session);
    });
    return true;
}

bool SingleTaskScheduler::transition(SessionId id, AppState next, ErrorCode error) {
    return coordinator_.transition(id, next, error);
}

bool SingleTaskScheduler::cancel_requested(SessionId id) const { return coordinator_.cancel_requested(id); }

bool SingleTaskScheduler::active() const { return coordinator_.active(); }

void SingleTaskScheduler::request_cancel() {
    std::scoped_lock lock(worker_mutex_);
    const auto snapshot = coordinator_.snapshot();
    if (snapshot.state != AppState::Idle) {
        (void)coordinator_.request_cancel(snapshot.id);
        if (worker_.joinable()) {
            worker_.request_stop();
        }
    }
}

void SingleTaskScheduler::wait() {
    std::scoped_lock lock(worker_mutex_);
    if (worker_.joinable()) {
        worker_.request_stop();
        worker_.join();
    }
}

const char* to_string(AppState state) noexcept {
    switch (state) {
    case AppState::Idle: return "idle";
    case AppState::Starting: return "starting";
    case AppState::Capturing: return "capturing";
    case AppState::Recognizing: return "recognizing";
    case AppState::PostProcessing: return "post_processing";
    case AppState::Confirming: return "confirming";
    case AppState::Injecting: return "injecting";
    case AppState::Cancelled: return "cancelled";
    case AppState::Failed: return "failed";
    }
    return "unknown";
}

const char* to_string(ErrorCode error) noexcept {
    switch (error) {
    case ErrorCode::None: return "none";
    case ErrorCode::Busy: return "busy";
    case ErrorCode::TargetChanged: return "target_changed";
    case ErrorCode::AudioUnavailable: return "audio_unavailable";
    case ErrorCode::RecognitionUnavailable: return "recognition_unavailable";
    case ErrorCode::RecognitionFailed: return "recognition_failed";
    case ErrorCode::InjectionFailed: return "injection_failed";
    case ErrorCode::Cancelled: return "cancelled";
    case ErrorCode::InvalidConfiguration: return "invalid_configuration";
    }
    return "unknown";
}

}  // namespace aevocis::core
