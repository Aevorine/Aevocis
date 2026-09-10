# Windows 操作指南

## 快速使用

1. 启动 `aevocis.exe`。
2. 把光标放入需要输入文字的软件。
3. 按住右 Ctrl 讲话。
4. 松开右 Ctrl，识别完成后文字会回到开始录音的窗口。

首次使用会按需加载本地 SenseVoice int8 中英文识别模型，第一次结果可能比后续听写慢；模型加载完成后会复用内存，不重复加载。模型只在本机 CPU 运行，线程数最多 4 个。

## 快捷键

| 操作 | 默认快捷键 |
| --- | --- |
| 按住录音 | 右 Ctrl（可在设置中更改，见下） |
| 显示/隐藏主窗口 | Ctrl + Alt + H |
| 托盘显示/隐藏 | 托盘左键 |
| 托盘功能菜单 | 托盘右键 |
| 切换主题 | 主窗口右上角主题图标或托盘菜单 |
| 检查更新 | 托盘右键 -> 检查更新 |

### 自定义语音识别快捷键

打开设置面板，点击“语音识别快捷键”卡片，然后：

- 想用**单个按键**：直接按下并松开该键（默认的 Right Ctrl 就是这样绑定的）。
- 想用**组合键**：按住 Ctrl / Alt / Shift / Win 中的一个或多个，再按下主键（例如 Ctrl + Alt + Space）。
- 按 Esc 取消；点击卡片右上角“重置默认”可随时回到 Right Ctrl。

绑定立即生效，无需重启，并保存在 `%LOCALAPPDATA%\Aevocis\settings.json` 的
`push_to_talk_virtual_key` / `push_to_talk_modifiers` 两个字段中。

**不会影响其他快捷键。** 键盘钩子只读取按键、从不拦截，所以被绑定的键在其他软件里照常工作；
反过来，为了避免别的快捷键顺带触发录音，以下绑定会被拒绝并在卡片上说明原因：

| 被拒绝的绑定 | 原因 |
| --- | --- |
| 左侧 Ctrl / Alt / Shift / Win 单键 | 它们承载了绝大多数系统与应用组合键 |
| 字母、数字、空格、回车、方向键等单键 | 打字时会被频繁按到 |
| Win + 单键 | 几乎都被 Windows 系统占用 |
| Ctrl+C/V/X/Z/S…、Alt+Tab、Alt+F4 等 | 通用系统快捷键 |
| Ctrl+Alt+H、Ctrl+Alt+Z、Ctrl+Shift+P | 已被本软件的显示/隐藏、撤销、命令面板占用 |

右侧的 Right Ctrl / Right Alt / Right Shift / Right Win、F1–F24、CapsLock 等单键，以及任意
带修饰键的组合键都可以自由绑定。

录音只在**仅按下该快捷键本身**时触发：如果同时按住了绑定之外的任何按键，或者多按了绑定之外的
修饰键，都不会开始录音，因此不会打断任何组合键操作。

## 语音命令

| 说出内容 | 行为 |
| --- | --- |
| 取消 / 删除这段 / 算了不要了 | 不输入本句；如果已有上一段输入，尝试发送等量退格 |
| 换行 | 发送一次回车 |
| 一段英文 + 全部大写 | 只把前面的英文内容转为大写后输入；普通英文不会被强制大写 |

语音命令必须在录音结束后保持原目标窗口为前台；焦点变化时会拒绝操作。

## 主题与界面

Paper 和 Dark Glass 可实时切换。主窗口内容区不重复显示页面名称；导航图标、设置、录音模式和清空历史均有悬停提示。录音悬浮条不激活目标软件，不改变文字输入焦点。公式界面目前没有公式渲染入口，因此未引入 KaTeX；后续增加公式内容时再接入 KaTeX 渲染层。

## Claude Code 调用

在已有 Aevocis 进程运行时，可以从本地终端调用：

```powershell
build\x64-release\aevocis_cli.exe --status
build\x64-release\aevocis_cli.exe --show
build\x64-release\aevocis_cli.exe --theme
build\x64-release\aevocis_cli.exe --inject "需要输入的文本"
```

命令通过当前用户专属命名管道进入主实例；`--inject` 仍然检查当前前台窗口，任务忙时返回 busy，不排队覆盖正在进行的听写。

## 发布形态

便携版是 `dist/Aevocis-portable` 目录，直接运行其中的 `aevocis.exe`；安装版是 `dist/Aevocis-0.2.2-Setup.exe`。模型、运行时 DLL、图标和 `aevocis_cli.exe` 已随安装包提供，文件摘要见 `dist/SHA256SUMS.txt`。安装程序会创建开始菜单、桌面和开机启动快捷方式；快捷方式与托盘使用同一份 Aevocis.ico。
