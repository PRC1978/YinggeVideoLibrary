# 加密报错日志

设置 → 数据与备份 → 报错日志提供“加密导出日志包”“在本机解密查看”“上传加密日志包”。

- 导出包含当前运行会话最近 50 条消息，以及数据目录 `diagnostics` 中最近 12 份、每份不超过 512 KB 的 JSON 诊断。操作前会预览条数和部分文件名。包内可能含私人路径、视频标题或错误详情；不要把解密后的 JSON 上传到公共服务。
- 用户输入至少 12 字符的口令。程序使用随机盐、PBKDF2-SHA256（600,000 次）派生密钥，再以 AES-256-GCM 加密并认证整个 JSON。口令不写入配置、不传给 SSH；丢失后无法恢复。输出文件扩展名 `.yglog`。本机解密查看会生成明文 JSON，仅保存在用户选定的位置。
- 上传前必须再次输入正确口令，完成解密认证。程序随后只把已认证的密文字节写入临时 `.yglog` 文件；通过 Windows OpenSSH 连接用户在本机填写的 SSH 目标、端口和私钥。连接信息保存在 `%LOCALAPPDATA%\YinggeVideoLibrary\error-upload-settings.json`，不放入公开源码或视频库备份。启用严格主机指纹校验和非交互密钥模式，远端只接收 `~/yingge-logs/yingge-error-*.yglog`。上传完成后删除本机临时密文。不会上传明文、口令或视频文件。
- 若用户没有预先在 `known_hosts` 记录正确的服务器指纹，或 SSH 私钥不可用，上传失败；程序不会自动接受未知主机指纹。

## 验证

- Release 构建 0 警告、0 错误；隔离目录 `--self-test-features` 通过，包括加密往返、错误口令、篡改拒绝、上传目标限制。结果 `%TEMP%\yingge-encrypted-report-final-features.json`，`realLibraryRead=false`。
- `--self-test-error-upload` 只创建合成文字并按本机连接设置上传加密包。2026-11-04 通过；服务器上返回 218 字节 `.yglog`，文件头为 `YGLOG1`，检查不到合成明文。结果 `%TEMP%\yingge-encrypted-upload-test.json`。
- 未上传用户真实日志，未读取真实视频库。当前运行消息只保留最近 50 条，重启后不再存在；持久的启动诊断仍可由 `diagnostics` 收集。服务器端只负责保存密文，解密需把包下载回本机并使用应用中的“在本机解密查看”。
