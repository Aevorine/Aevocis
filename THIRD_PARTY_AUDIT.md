# 第三方依赖审计记录

## 审计结论

当前只采用系统 Windows SDK/Visual Studio Build Tools、CMake/Ninja、Direct2D/DirectWrite/WASAPI 和官方 sherpa-onnx 发布包。没有将签名私钥、用户模型路径或本机隐私数据写入项目。

## sherpa-onnx

- 来源：`k2-fsa/sherpa-onnx` GitHub 官方 release
- 版本：`v1.13.7`
- Windows 开发包：`sherpa-onnx-v1.13.7-win-x64-static-MT-Release.tar.bz2`
- SHA-256：`5f41cb4cbf027cb0597f7afb8c99b7233ddc6945677c2f6d1bbd15b857220d71`
- Windows 库包：`sherpa-onnx-v1.13.7-win-x64-static-MT-Release-lib.tar.bz2`
- 库包 SHA-256：`04734146fb3a21a297604c586ea826346dbb167c19b9ccc79c1f85d39f490395`
- 使用限制：仅在构建缓存中使用；发布前执行许可证、依赖、隐私和摘要复核。

## 模型资源字节核验

- SenseVoice 官方归档：`sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.tar.bz2`
- 归档 SHA-256：`7305f7905bfcf77fa0b39388a313f3da35c68d971661a65475b56fb2162c8e63`
- 当前 `Models/sensevoice/model.int8.onnx` SHA-256：`12ca1a2ae7ecf3e0019ef2822307ee0b5cadc9196569e379b4c4026f8205276d`
- 当前 `Models/sensevoice/tokens.txt` SHA-256：`f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc`
- 标点官方归档：`sherpa-onnx-punct-ct-transformer-zh-en-vocab272727-2024-04-12-int8.tar.bz2`
- 归档 SHA-256：`c0d5aa5f8eeb686032345e180bedf39319dc2e0556781c6264bcadba8328a6e1`
- 当前 `Models/punct/model.int8.onnx` SHA-256：`65a3fb9f5ad7bfb96bf69e0dc4481df97f6ee60513c1d94ce981ba6effd524b1`

Gitleaks 在 SenseVoice 模型二进制的同一偏移触发 `github-oauth` 与 `github-pat` 两条规则。模型文件与官方归档成员逐字节一致，说明命中是模型内容的二进制误报；安全门仍按 BLOCK 记录，未添加忽略规则，未将发布扫描伪装成 PASS。

## 打包工具

- Inno Setup 6.7.3，来源为 `JRSoftware.InnoSetup` winget 清单；本机安装路径为用户程序目录。
- 代码签名证书未配置时只生成 SHA-256 清单，不伪造签名。

## 未采用

不引入 Qt、Android SDK、移动端依赖、vcpkg 全量依赖树或未锁定网络包。
