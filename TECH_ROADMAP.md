# TECH_ROADMAP · v1.2.0 识别引擎重构（SenseVoice 双引擎 + 双路选优麦克风）

> 2026-08-31。三重查真防害验效协议执行记录：第一重（多源联网查真）与第二重（本机沙盒实测）已完成，
> 第三重（实效验证结果回填）在发布后由 E2E 数据补齐。

## 1. 选型理由（第一重：多源查真，每条 ≥3 独立来源）

### 1.1 为什么换默认引擎：Whisper small 的三宗罪（全部本机实锤）
| 问题 | 本机实测证据 |
|---|---|
| 慢 | 6s 音频 2.3s（Vulkan）/ 16s（纯 CPU）；首次识别预热 10.2s。用户真实日志：1.3s 语音 7s 出字 |
| 吃内存 | 识别峰值工作集 1243MB（f16+Vulkan）；q8_0 也要 943MB |
| 中文差 | 简体说话输出繁体、"会议纪要"→"會議寄要"；低电平/垃圾音频触发字幕组署名幻觉（"(字幕製作:貝爾)"），openai/whisper#1606、#679、arXiv:2505.12969 多源确认为 68 万小时字幕训练数据污染所致 |

### 1.2 SenseVoice-small int8（sherpa-onnx）多源查真结论
- NuGet `org.k2fsa.sherpa.onnx` 1.13.5（2026-08-11），.NET 8 / win-x64 官方支持，Apache-2.0。
  来源：nuget.org、github.com/k2-fsa/sherpa-onnx releases、k2-fsa.github.io C# API 文档（2026-08-31 访问）。
- 模型：`sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09`（model.int8.onnx 226MB + tokens.txt）。
- 论文数据（arXiv:2407.04051 Table 6/7）：AISHELL-1 CER 2.96 vs whisper-small 10.04；非自回归 CTC 架构，
  官方称比 whisper-small 快 5 倍以上（A800 数据，本机需实测——已实测，见 §2）。
- 许可：代码 MIT；模型走 FunASR Model License（允许商用与再分发，须保留署名与许可文本，
  FunAudioLLM/SenseVoice#279 官方澄清）。落地动作：随应用分发 `Models/sensevoice/LICENSE-MODEL.txt`。
- 标点：ct-transformer zh-en int8（72MB，k2-fsa 官方 punctuation-models 发布），dotnet 有官方示例。

### 1.3 麦克风双路选优（用户拍板方案）
- 根因链（E2E 取证）：蓝牙耳机"刚连接即优先"（round-6 逻辑）→ HFP 切换死区 + 未佩戴远场收音 → 垃圾音频。
- 蓝牙 HFP 采集质量/切换延迟为已知平台现象（NAudio issues、MS 文档、社区多源）。
- 新方案：无手动指定且「通信默认 ≠ 多媒体默认」时，两路同时录；停止后按 20ms 帧 RMS 的 90 分位数
  （P90 帧能量，抗恒定底噪、抗死区）选优，两路得分写日志。手动固定设备仍最高优先（保活）。
- 已知代价（如实告知）：听写时打开蓝牙 HFP 采集会让耳机音乐短暂降质（现状也如此）；双路录音 CPU 开销
  可忽略（两路 16kHz PCM，实测占用 <1%）。

## 2. 沙盒实测（第二重：防"理论快实操慢"，i5-1155G7 / 16GB / Iris Xe，2026-08-31）
| 指标 | Whisper small f16+Vulkan | Whisper q8_0+Vulkan | SenseVoice int8 CPU×4线程 |
|---|---|---|---|
| 模型加载 | 1705ms | 526ms | 1490ms |
| 首次识别（预热） | 10154ms | 4878ms | **200ms** |
| 6s 中文音频稳态 | 2344ms | 2636ms | **186ms** |
| 加载后常驻 | 614MB | 356MB | **324MB** |
| 识别峰值工作集 | 1243MB | 943MB | **341MB** |
| 中文输出 | 繁体漂移+错字 | 同左 | 简体全对 |
| 峰值0.03低电平 | 靠Vulkan不崩 | 同左 | 全对 |
| 中英混合 | 未测 | 未测 | "这个 BUG…COMMIT 一下"全对（需大小写后处理） |

判定：无有害反转，SenseVoice 实测优势与论文宣称同向且量级更大（12 倍稳态、50 倍预热），采纳。

## 3. 数据流（新）
```
按住快捷键
  └─ MicRecorder.Start
       ├─ 手动固定设备 → 单路
       └─ 自动：多媒体默认 ─┬─ 与通信默认相同 → 单路
                            └─ 不同 → 双路同录
松开快捷键
  └─ Stop → [双路时] P90帧能量选优（两路得分入日志）→ float[16kHz]
  └─ SenseVoiceTranscriptionEngine（默认）
       ├─ OfflineRecognizer(SenseVoice int8, ITN, 4线程) ≈200ms
       ├─ 大小写修正（全大写英文词→小写，句首大写）
       └─ ct-transformer 标点恢复（模型缺失时优雅降级为不加）
     或 WhisperTranscriptionEngine（设置切换，模型按需下载）
  └─ DictationController 原有链（口头命令→宏→术语纠错→标点规则→草稿确认→SendInput 注入）【零改动】
```

## 4. 扩展性
- ITranscriptionEngine 接口不变：未来加第三引擎（如流式 sherpa-onnx streaming zipformer）只需新增实现类。
- 引擎特有后处理内聚在引擎内部（大小写/标点是 SenseVoice 的输出癖好），控制器链保持引擎无关。
- ModelCatalog 仍是 Whisper 专属目录；SenseVoice 模型随安装包走版本管理（Velopack 增量更新自动处理）。

## 5. 实效验证结果（第三重：发布后回填）
- [ ] 活体 E2E：真实 publish 版 + 真快捷键 + 扬声器语音 → 记事本收到正确文本，识别耗时 <1s
- [ ] 已装 1.1.0 实例弹出 v1.2.0 更新气泡（F27 首次真实验证）
- [ ] 用户真人听写确认（最终判据）
