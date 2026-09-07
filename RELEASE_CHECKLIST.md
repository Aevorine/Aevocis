# 发布候选检查清单

只有以下项目全部有证据，才允许创建 GitHub Release：

- [x] Debug/Release CMake 构建和 CTest 通过。
- [x] SenseVoice、标点模型与官方归档摘要及成员哈希一致。
- [ ] 便携版、安装版真实启动；已完成安装版进程响应、CLI IPC 和单实例基础证据，托盘、主题、快捷键和焦点安全仍需交互桌面验收。
- [ ] 记事本、浏览器、Office 输入测试保留原始结果。
- [x] Semgrep 源码扫描无 ERROR/WARNING（有效配置扫描 62 个文件，0 findings）。
- [x] 源码目录 Gitleaks 扫描无命中。
- [ ] Gitleaks 全历史扫描无 BLOCK；模型二进制误报已由安全负责人批准审计范围后再处理。
- [ ] 发布目录隐私扫描和候选文件哈希已锁定。
- [ ] 未包含签名私钥、用户配置、历史记录、崩溃转储、模型下载缓存和本机路径。
- [ ] GitHub Releases 目标版本、资产清单、SHA-256、隐私扫描结果和删除范围已锁定。

当前状态：本地构建、安装版启动和模型字节核验已完成；发布门仍为 BLOCK，原因是 Gitleaks 对官方模型二进制的规则命中尚未获得允许该误报进入发布资产的独立批准，且真实跨软件语音输入验收尚未完成。

## 当前候选锁定信息

- 版本：`0.1.0`，目标：Windows x64。
- 便携版：`dist/Aevocis-portable`；安装包：`dist/Aevocis-0.2.2-Setup.exe`。
- 哈希清单：`dist/SHA256SUMS.txt`，对应当前便携版 6 个文件。
- 源码安全结果：Semgrep 0 findings、0 errors；源码目录 Gitleaks 无命中；双轨自检 0/0。
- 发布目录安全结果：Gitleaks 2 个命中，均位于官方 SenseVoice 模型二进制；与官方归档成员 SHA-256 一致，但在独立审批前保持 BLOCK。
- 签名：未写入或生成代码签名私钥；当前只有 SHA-256 校验，未伪造签名。
- 外部发布：未上传 GitHub，未删除旧 Release，未删除 `native-rust`、`src-reference`、`nr-worktrees` 或旧构建物。
