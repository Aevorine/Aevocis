# 超语音 OpenSuperWhisper for Windows

按一个快捷键 → 讲话 → 本地识别 → 自动把文字打进当前正在用的软件。全程离线，不联网，不上传任何语音内容。

这是 [Starmel/OpenSuperWhisper](https://github.com/Starmel/OpenSuperWhisper)（macOS 版）的 Windows 移植版，核心识别引擎沿用同一套 [Whisper.net](https://github.com/sandrohanea/whisper.net)（whisper.cpp 的 .NET 绑定）。

## 下载使用（不想自己编译）

去 [Releases](../../releases) 下载最新的压缩包，解压后双击 `OpenSuperWhisper.App.exe` 即可，不需要安装 .NET 或任何其他运行环境（自带）。

## 怎么用

1. 启动后程序常驻系统托盘（任务栏右下角，可能在"显示隐藏的图标"里）。
2. **按住右 Ctrl 说话，松开自动把识别结果打字输入到当前光标所在的位置**——ChatGPT、Claude Code、VSCode、Word、浏览器、微信、邮件都可以。
3. 左键单击托盘图标：显示/隐藏主界面（历史记录）。再点一次隐藏，不会退出程序。
4. 右键单击托盘图标：
   - **显示主界面** — 看最近说过的话，或清空历史记录
   - **设置...** — 改快捷键（点一下输入框，按下想用的键即可绑定）、改识别语言
   - **重试初始化** — 模型没加载成功时用这个重试
   - **退出** — 真正关闭程序

首次启动会自动使用内置的 `tiny.en`（英文、体积最小）识别模型；识别效果和更大模型的取舍会在后续版本里做成可选项。

## 从源码编译

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
cd OpenSuperWhisper.App
dotnet build ..\OpenSuperWhisper.sln
dotnet run --project .
```

## 项目结构

一个模块一个职责，互不依赖具体实现（通过 `OpenSuperWhisper.Core` 的接口耦合）：

| 项目 | 负责什么 |
|---|---|
| `OpenSuperWhisper.App` | 主程序：托盘图标、主窗口、设置窗口、录音提示条，把其余模块组装起来 |
| `OpenSuperWhisper.Hotkeys` | 全局按住说话快捷键（Windows 底层键盘钩子） |
| `OpenSuperWhisper.Audio` | 麦克风录音 |
| `OpenSuperWhisper.Recognition` | 语音识别（Whisper.net / whisper.cpp） |
| `OpenSuperWhisper.TextInjection` | 把识别结果打字输入到当前聚焦的窗口 |
| `OpenSuperWhisper.Storage` | 设置与历史记录的本地存储 |
| `OpenSuperWhisper.Core` | 各模块之间的接口约定 |

设计草图见 [`design/mockups`](design/mockups)。

## 隐私与安全

- **完全离线**：识别在本机完成，不联网、不上传、无遥测。
- 设置和历史记录存放在 `%LOCALAPPDATA%\OpenSuperWhisper\`，仅当前 Windows 账户可读。
- 出问题时的诊断日志在同一目录下的 `log.txt`。

## License

MIT，见 [LICENSE](LICENSE)。沿用自 [Starmel/OpenSuperWhisper](https://github.com/Starmel/OpenSuperWhisper)。
