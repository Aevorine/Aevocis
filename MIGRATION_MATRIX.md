# C# / Rust 到 C++ 功能迁移表

状态含义：`已建立` 表示模块边界、实现和最小验证已落地；`待接入` 表示功能契约明确但实现尚未完成；`待验收` 表示实现已具备但仍需要在目标软件中实测；`已验证` 表示已有自动化或模型级真实证据，但不替代跨软件人工验收。

| 原功能 | C# 参考 | Rust 当前实现 | C++ 目标模块 | 当前状态 | 验收方式 |
| --- | --- | --- | --- | --- | --- |
| 全局按键监听 | `GlobalPushToTalkHotkey` | `hotkey.rs` | `keyboard_hook` | 已建立 | 记事本按键事件 |
| 按住模式 | `PushToTalkMode` | `hotkey.rs` | App + Core 状态机 | 已建立 | 按下/松开一次 |
| 切换模式 | `PushToTalkMode` | `hotkey.rs` | App + Core 状态机 | 已建立 | 连按两次 |
| 目标窗口 | `ActiveWindowInfo` | `target.rs` | `TargetWindowToken` | 已建立 | 焦点切换 |
| 单任务 | `DictationController` | `audio.rs` / `main.rs` | `SingleTaskScheduler` | 已建立 | 快速重复触发 |
| WASAPI 录音 | `MicRecorder` | `cpal` 适配 | `WasapiRecorder` | 已建立 | 真实麦克风录音 |
| SenseVoice | `SenseVoiceTranscriptionEngine` | `recognizer.rs` | `IRecognizer` + sherpa C API 适配 | 已验证 | 官方模型加载、CPU int8、中文/英文同一模型 |
| 标点修正 | `PunctuationFixer` | `punctuation.rs` | `TextPipeline` + 标点适配 | 已建立 | 中英文句尾 |
| 术语词典 | `TermDictionary` | `term_dictionary.rs` | Core 术语规则 | 已建立 | 术语替换 |
| 噪声过滤 | `NoiseFilter` | `recognizer.rs` | `TextPipeline` | 已建立 | 噪声短语 |
| 语音命令 | `VoiceCommandMatcher` | `voice.rs` | Core 命令匹配器 | 已建立 | 取消/设置命令 |
| 语音宏 | `MacroExecutor` | `voice.rs` | Windows 宏执行器 | 待接入 | 宏动作链 |
| Unicode 注入 | `UnicodeTextInjector` | `inject.rs` | `TextInjector` | 已建立 | 中英文长文本 |
| 注入前焦点复核 | C# 输入安全策略 | `inject.rs` | `TargetWindowToken` | 已建立 | 切换焦点保护 |
| 历史记录 | `HistoryStore` | `history.rs` | Windows Storage | 已建立 | 重启后读取 |
| 设置持久化 | `SettingsStore` | `settings.rs` | Windows Storage | 已建立 | 修改后重启 |
| 术语持久化 | `TermDictionaryStore` | `term_dictionary.rs` | Windows Storage | 已建立 | 文件级读写；界面编辑待接入 |
| 草稿确认 | `DraftConfirmationService` | `draft_confirm.rs` | UI Confirming 状态 | 待接入 | 注入前确认 |
| 托盘单击 | `App.xaml.cs` | `main.rs` | `TrayIcon` | 已建立 | 显示/隐藏 |
| 托盘菜单 | `App.xaml.cs` | `main.rs` | `TrayIcon` | 已建立 | 右键菜单 |
| 显示/隐藏快捷键 | `GlobalToggleWindowHotkey` | `show_hide_hotkey.rs` | `GlobalHotkey` | 已建立 | Ctrl+Alt+H |
| 单实例 | `Mutex` | `single_instance.rs` | `SingleInstance` | 已建立 | 二次启动 |
| 开机启动 | `AutoStart` | `autostart.rs` | Windows Registry adapter | 已建立 | 托盘菜单切换 |
| 更新检查 | `UpdateManager` | `update.rs` | WinHTTP updater | 已建立 | GitHub latest Release、SHA-256、安装程序 |
| 崩溃报告 | `CrashReporter` | `crash_reporter.rs` | Minidump/report adapter | 待接入 | 故障演练 |
| 主窗口 | WPF `MainWindow` | Slint `main_window` | Direct2D `MainWindow` | 已建立 | 实际启动 |
| 录音悬浮条 | WPF overlay | Slint inline overlay | Direct2D overlay | 已建立 | 状态消息驱动；需真实录音验收 |
| 设置页 | WPF `SettingsWindow` | Slint settings | Direct2D settings | 已建立 | 点击设置 |
| 词典页 | WPF `TermDictionaryWindow` | Slint terms | Direct2D terms | 待接入 | 编辑词条 |
| Paper 主题 | WPF resource dictionary | Slint theme | Direct2D palette | 已建立 | 切换 |
| Dark Glass 主题 | WPF resource dictionary | Slint theme | Direct2D palette | 已建立 | 切换 |
| 统一中文字体 | SimSun | Slint fallback | DirectWrite SimSun | 已建立 | 字体检查 |
| 统一英文/标点字体 | Times New Roman | Slint fallback | DirectWrite Times New Roman | 已建立 | 英文显示 |
| 图标工具提示 | WPF ToolTip | Slint ToolTip | Win32 tooltip | 已建立 | 指针停留 |
| 安装包 | Inno Setup | Inno Setup | Inno Setup 6 | 已验证 | 临时目录静默安装并启动 |
| 便携版 | ZIP/目录 | ZIP/目录 | 单目录发布 | 已建立 | 主程序、CLI、模型和 SHA-256 清单 |
| Claude Code 调用 | CLI/IPC 契约 | 未迁移 | 本地 CLI/命令协议 | 已建立 | `aevocis_cli --status/show/theme/inject` |
