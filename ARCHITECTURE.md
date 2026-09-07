# Aevocis Native C++ 架构

## 已锁定方案

Windows 版本采用纯 C++ Core + Win32 + Direct2D/DirectWrite。C++ Core 不依赖 GUI、音频设备、模型或网络；平台适配层只提供 Windows 能力；界面层只消费状态和事件。

## 模块边界

| 层 | 目录 | 责任 | 不负责 |
| --- | --- | --- | --- |
| Core | `include/aevocis/core` | 状态机、单任务调度、音频缓冲、识别接口、文本后处理 | HWND、注册表、模型文件、绘制 |
| Windows | `include/aevocis/platform/windows` | WASAPI、全局快捷键、键盘钩子、目标窗口、安全注入、托盘、单实例 | 业务状态决策、界面布局 |
| UI | `include/aevocis/ui` | Direct2D 绘制、DirectWrite 字体、主题切换、工具提示、页面交互 | 识别、录音、文件存储 |
| App | `src/app` | 依赖装配、消息循环、功能编排 | 低层平台细节 |

## 不重叠任务规则

`TaskCoordinator` 只有 `Idle -> Starting` 可以创建新会话。识别、后处理和输入全部属于当前会话；新的快捷键触发在会话回到 `Idle` 前拒绝。工作线程由 `std::jthread` 管理，停止请求、线程连接和资源释放均为 RAII。

## 目标窗口安全规则

开始录音时保存 `HWND + PID`。识别完成后、输入前以及每个 Unicode 输入块之前检查窗口仍存在、PID 未改变且仍是前台窗口。焦点切换时放弃输入，避免把内容写入错误软件。

## 性能规则

- 低级键盘钩子只过滤注入事件并投递轻量 Windows 消息，不执行录音、模型、磁盘或网络操作。
- SenseVoice 识别器在应用启动阶段后台加载一次，单会话复用，识别线程上限为逻辑处理器数与 4 的较小值。
- WASAPI 采用事件驱动共享模式，音频缓冲设有 120 秒上限。
- GUI 只在状态或内容变化时重绘，绘制采用 Direct2D 硬件加速目标；主题状态不触碰识别状态。
- 更新检查、历史持久化和崩溃上报放到后台队列，且不会进入快捷键回调。

## 阶段状态

当前已建立 Core/Windows/UI 的可编译骨架，保留 `native-rust` 与 `src-reference` 作为行为基线。SenseVoice 模型装配、持久化、发布打包和真实跨应用验收在后续阶段完成前，不删除参考实现。
