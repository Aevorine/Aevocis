#pragma once

#include <atomic>
#include <functional>
#include <string>
#include <thread>

namespace aevocis::platform::windows {

class CommandPipeServer {
public:
    using Handler = std::function<std::string(std::string)>;

    CommandPipeServer() = default;
    CommandPipeServer(const CommandPipeServer&) = delete;
    CommandPipeServer& operator=(const CommandPipeServer&) = delete;
    ~CommandPipeServer();

    [[nodiscard]] bool start(Handler handler) noexcept;
    void stop() noexcept;

private:
    void loop() noexcept;

    Handler handler_;
    std::jthread worker_;
    std::atomic_bool stopping_{false};
};

class CommandPipeClient {
public:
    [[nodiscard]] static std::string request(const std::string& command) noexcept;
};

}  // namespace aevocis::platform::windows
