# Aevocis（超语音）for Windows

按一个快捷键 → 讲话 → 本地识别 → 自动把文字打进当前正在用的软件。全程离线，不联网，不上传任何语音内容。

这是 [Starmel/OpenSuperWhisper](https://github.com/Starmel/OpenSuperWhisper)（macOS 版）的 Windows 移植版并持续独立演进，仓库地址：[Aevorine/Aevocis](https://github.com/Aevorine/Aevocis)。

## 下载使用（不想自己编译）

去 [Releases](https://github.com/Aevorine/Aevocis/releases) 下载最新版的 `Aevocis-win-Setup.exe`，双击安装即可（自带 .NET 运行时，不需要额外安装）。托盘菜单的"检查更新"/"下载新版本"可以直接在应用内一键升级到新版本，不需要每次都回这里手动下载。

## 操作指南（快捷键一览）

| 操作 | 效果 |
|---|---|
| **按住** 右 Ctrl（默认，设置里可改绑） | 开始录音；松开后自动识别并把文字打进当前光标所在的位置——ChatGPT、Claude Code、VS Code、Word、浏览器、微信都可以 |
| **Ctrl+Alt+H**（默认，设置里可改绑，支持任意 Ctrl/Alt/Shift/Win 组合键） | 任意界面下直接显示/隐藏主界面，不用先切到本程序；再按一次即可退出/收起界面 |
| **左键单击**托盘图标 | 效果等同于上面的显示/隐藏快捷键：显示主界面；再点一次隐藏（隐藏后任务栏不显示该窗口，不是最小化） |
| **右键单击**托盘图标 | 弹出功能菜单（见下） |

托盘右键菜单：

- **显示主界面** — 打开主窗口，看最近的听写历史（支持关键词搜索）、清空历史
- **设置...** — 改快捷键、切换识别引擎/语言/麦克风、编辑专业词典、按软件单独设置提示词/快捷键、配置口头命令与语音宏、导出导入全部设置
- **检查更新** / **下载新版本 vX.X.X**（发现新版本时才出现）— 一键下载并重启到新版本，不用手动跑安装包
- **重试初始化** — 模型或热键没加载成功时用这个重试（托盘提示文字会说明原因）
- **退出** — 真正关闭程序（点窗口右上角的 × 只是隐藏到托盘，不会退出）

主窗口顶部悬浮在录音指示灯上可以看到同样的快捷键提示（鼠标停留即弹出）。

## 识别引擎

默认使用「闪电」引擎（SenseVoice-small int8，随安装包一起分发）：中文识别准确率高、启动快、常驻内存低。设置里也可以切换到 Whisper（`small`/`medium`/`large-v3-turbo`，按需从 Hugging Face 下载），支持中英混合识别与逐句流式展示。两种引擎切换都是热切换，不需要重启程序。

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
| `OpenSuperWhisper.Hotkeys` | 全局按住说话快捷键（Windows 底层键盘钩子）+ 全局显示/隐藏窗口快捷键（RegisterHotKey） |
| `OpenSuperWhisper.Audio` | 麦克风录音、多设备自动选优 |
| `OpenSuperWhisper.Recognition` | 语音识别（SenseVoice / Whisper.net 双引擎） |
| `OpenSuperWhisper.TextInjection` | 把识别结果打字输入到当前聚焦的窗口 |
| `OpenSuperWhisper.Storage` | 设置、历史记录、专业词典的本地存储 |
| `OpenSuperWhisper.Core` | 各模块之间的接口约定 |

设计草图见 [`design/mockups`](design/mockups)。

## 隐私与安全

- **完全离线**：识别在本机完成，不联网、不上传、无遥测；仅"检查更新"和按需下载更大 Whisper 模型时会联网。
- 设置和历史记录存放在 `%LOCALAPPDATA%\OpenSuperWhisper\`，仅当前 Windows 账户可读。
- 出问题时的诊断日志在同一目录下的 `log.txt`。

## License

MIT，见 [LICENSE](LICENSE)。沿用自 [Starmel/OpenSuperWhisper](https://github.com/Starmel/OpenSuperWhisper)。
