# 捆绑模型（不入 git，构建/打包机需先放好）

| 路径 | 内容 | 来源 |
|---|---|---|
| `sensevoice/model.int8.onnx` (226MB) + `sensevoice/tokens.txt` | 闪电引擎（SenseVoice-small int8） | https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.tar.bz2 |
| `punct/model.int8.onnx` (72MB) | ct-transformer 中英标点恢复 int8 | https://github.com/k2-fsa/sherpa-onnx/releases/download/punctuation-models/sherpa-onnx-punct-ct-transformer-zh-en-vocab272727-2024-04-12-int8.tar.bz2 |

Whisper 的 ggml 模型自 v1.2.0 起不再捆绑，运行时按需下载到
`%LOCALAPPDATA%\OpenSuperWhisper\Models`（见 `ModelCatalog` / `ModelDownloadService`）。

## 许可
- sherpa-onnx 代码：Apache-2.0。
- SenseVoice-small 模型权重：FunASR Model Open Source License（允许商用与再分发，须保留署名，
  见 `sensevoice/LICENSE-MODEL.txt` 与 https://github.com/FunAudioLLM/SenseVoice/issues/279 官方澄清）。
