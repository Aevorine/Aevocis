# Aevocis Native C++

Windows 版本的本地语音输入工具。按下全局快捷键讲话，松开后在识别完成时把文本安全地输入到开始录音时的前台软件。

## 当前里程碑

本目录是 Windows 原生 C++ 版本。当前已完成 Core 状态机、单任务调度器、文本管线、WASAPI 采集器、Win32 快捷键/托盘/单实例/目标窗口校验/Unicode 注入接口，以及 Paper 与 Dark Glass Direct2D 界面骨架。

行为迁移已依据旧实现完成核对；仓库只保留 C++ 源码和必要的构建、安装、测试文件，模型资源在发布包中提供。

SenseVoice int8 采用首次听写按需加载策略：空闲时不加载模型内存，第一次录音结束后加载并完成识别；后续听写复用已加载模型。运行时使用官方 sherpa-onnx CPU C API，线程数最多 4 个。

## 构建

在 VS 2022 x64 开发者命令行中运行：

```powershell
cmake --preset x64-release
cmake --build --preset x64-release
ctest --preset x64-release
```

构建输出位于 `build/x64-release`。项目开启 `compile_commands.json`，便于 clangd 检查。

## 操作基线

- 右 Ctrl：按住录音，松开结束（默认键，可在设置中重新绑定）。
- Ctrl+Alt+H：显示/隐藏主窗口。
- 托盘左键：显示/隐藏。
- 托盘右键：显示/隐藏、切换主题、设置、开机启动、退出。
- 主窗口右上角：切换 Paper / Dark Glass 或打开设置。
- Claude Code 或其他本地工具可调用 `aevocis_cli.exe --status`、`--show`、`--theme` 和 `--inject "文本"`。

## 验收边界

当前程序会在识别器未装配模型时明确显示“识别模型未就绪”，不会伪造识别成功，也不会向目标软件注入空文本；正式安装包已包含 SenseVoice 模型、标点模型、运行时 DLL 和 Aevocis 图标。托盘“检查更新”会通过 WinHTTP 读取 GitHub 最新 Release，下载前校验 SHA-256，校验通过后启动安装程序。完整功能以 `MIGRATION_MATRIX.md` 和 `APP_METRICS.md` 的实测状态为准。
