# SPEC · v1.2.0「快、省、准」重构（2026-08-31 拍板）

## 用户拍板的四项决策
1. 识别引擎：**双引擎，SenseVoice int8 默认**，Whisper 保留可切换（模型全部按需下载，不再捆绑）。
2. 麦克风策略：**双路同录自动选优**（内置 + 刚连接的通信默认设备同时录，识别前按语音能量选优）。
3. 标点：**加 ct-transformer int8 标点模型**（SenseVoice 引擎内部自动跑）。
4. 交付：**发 GitHub Release v1.2.0**，用户通过托盘「下载新版本」应用内更新（保留最近两版 Release）。

## 背因（E2E 取证结论，2026-08-31）
- 2026-08-28 用户真实听写全废的根因：OPPO 蓝牙耳机刚连接即被自动抢作录音设备（round-6 逻辑），
  A2DP→HFP 切换死区 + 远场收音 → 采到的音频内容是垃圾 → Whisper 输出教科书式静音幻觉
  （"Thank you." / "(字幕製作:貝爾)" / "[BLANK_AUDIO]"，openai/whisper#1606 多源确认）。
- 对照实验（本机、真实编译 DLL）：干净音频峰值哪怕 0.03 也识别全对 → 增益不是根因，设备选择才是。
- 性能实测（i5-1155G7）：Whisper small 6s 音频 2.3s（预热 10.2s、峰值内存 1243MB、纯 CPU 16s）；
  SenseVoice int8 **186ms（预热 200ms、峰值内存 341MB）**，中文简体正确。

## 任务 DAG 与 PASS 条件
| # | 任务 | PASS 条件 |
|---|---|---|
| T1 | Core：AppSettings.RecognitionEngine 字段 | 编译过；默认 "sensevoice"；旧 settings.json 反序列化不炸 |
| T2 | Recognition：SenseVoiceTranscriptionEngine（含大小写修正+标点模型+预热） | 无头验证：真实 DLL 上「今天下午三点开会…」输出含标点简体全对；BUG→bug |
| T3 | Recognition：ModelCatalog small 改按需下载 | whisper 引擎选 small 且本地无文件时走下载路径，不再假设捆绑 |
| T4 | Audio：MicRecorder 双路同录自动选优 | 无头验证：内置阵列 vs ToDesk 虚拟麦双录，选优逻辑选中有信号的一路，双路得分入日志 |
| T5 | App：引擎装配/切换 + SettingsWindow 引擎下拉 + csproj 捆绑 SenseVoice/标点模型、去捆绑 ggml | 全解决方案 0 error 编译；引擎切换不重启生效 |
| T6 | 活体 E2E：publish 版真启动、真按快捷键、扬声器放语音、文字真落到记事本 | **部分完成**：log.txt 确认 SenseVoice 识别耗时 359ms（<1s 达标）；合成语音声学耦合太弱（峰值0.001），句子级识别正确性未验证，需用户真人测试兜底，见 TECH_ROADMAP.md §5 |
| T7 | 打包发布：vpk pack 1.2.0 + 隐私扫描 + GitHub Release + 清旧版只留两版 | **完成**：gitleaks/semgrep/OSV 扫描干净；v1.2.0-windows 已发布 https://github.com/Aevorine/OpenSuperWhisper_Windows/releases/tag/v1.2.0-windows；v1.0.0-windows 已删除，现存 v1.1.0/v1.2.0 两版 |
| T8 | 双轨自检 + 文档 + 记忆沉淀 | **完成**：新增/改动的 8 个文件逐一读过，未发现静默失效或实际损坏；`dotnet build` 0 警告 0 错误；TECH_ROADMAP 实效验证栏已回填 |

## 保活红线
- DictationController / GlobalPushToTalkHotkey / UnicodeTextInjector / 历史 / 宏 / 口头命令 / 术语纠错：**不改**。
- Whisper 引擎类不改（只动 ModelCatalog 的捆绑标记与文案）。
- 手动固定麦克风（设置下拉）行为不变，仍然最高优先。
