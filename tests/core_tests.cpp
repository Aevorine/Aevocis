#include "aevocis/core/audio_buffer.hpp"
#include "aevocis/core/task_scheduler.hpp"
#include "aevocis/core/text_pipeline.hpp"
#include "aevocis/core/voice.hpp"

#include <cassert>
#include <chrono>
#include <thread>

using namespace aevocis::core;

int main() {
    const TargetToken target{1, 2};
    SingleTaskScheduler scheduler;
    const bool started = scheduler.submit(target, [](std::stop_token, SessionId) { std::this_thread::sleep_for(std::chrono::milliseconds(50)); });
    if (!started) return 1;
    assert(!scheduler.submit(target, [](std::stop_token, SessionId) {}));
    scheduler.wait();

    AudioBuffer buffer(3);
    const float first[] = {1.0F, 2.0F};
    const float second[] = {3.0F, 4.0F};
    assert(buffer.append(first));
    assert(!buffer.append(second));

    const auto processed = TextPipeline::process("  你好   世界  ", {{"世界", "Aevocis"}});
    assert(processed.text == "你好 Aevocis。");
    const auto command = TextPipeline::process("取消", {});
    assert(command.voice_command_candidate);
    const auto chinese_terminal = TextPipeline::process("你好", {});
    assert(chinese_terminal.text == "你好。");
    const auto commands = VoiceCommandMatcher::defaults();
    const auto enter = VoiceCommandMatcher::match("换行。", commands);
    assert(enter.has_value() && enter->action == VoiceCommandAction::SendEnter);
    const auto upper = VoiceCommandMatcher::match("hello world 全部大写。", commands);
    assert(upper.has_value() && upper->remaining_text == "hello world");
    return 0;
}
