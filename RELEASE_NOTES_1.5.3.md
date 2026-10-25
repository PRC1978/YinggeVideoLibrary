# 映格视频库 1.5.3

## 修复软件内更新

- 修复 1.5.1、1.5.2 下载完成后提示“下载文件的大小、SHA-256 或程序身份与目标版本不符”的问题。旧版将下载文件暂存为以 `.part` 结尾的路径，但程序身份校验要求文件名以 `.exe` 结尾，因此正确下载的文件也会被拒绝。新版的临时文件以 `.part.exe` 结尾，仍须同时通过文件大小、SHA-256 和程序身份校验才能替换旧程序。
- 更新失败时保留旧程序，不会启动安装向导或覆盖原有视频资料。安装版与免安装版仍使用当前 Windows 用户的 `%USERPROFILE%\.YinggeVideoLibrary` 数据目录。

## 从 1.5.1 或 1.5.2 升级

旧版程序自身仍带有上述缺陷，无法通过普通的软件内下载完成**第一次**升级。请关闭旧版后，下载并运行 [1.5.3 安装包](https://github.com/PRC1978/YinggeVideoLibrary/releases/download/v1.5.3/YinggeVideoLibrary-Setup.exe)。使用免安装版时，可下载 [1.5.3 独立程序](https://github.com/PRC1978/YinggeVideoLibrary/releases/download/v1.5.3/YinggeVideoLibrary-win-x64.exe)，将其改名为 `映格视频库.exe` 后替换原目录中的同名文件。此操作不移动视频或当前用户的数据目录。升级到 1.5.3 后，后续版本可继续在软件内更新并重启。

## 验收范围

- Windows x64 Release 隔离构建：0 警告、0 错误。
- 更新专项离线诊断通过：正确大小、SHA-256 和程序身份的文件被接受；截断文件、错误 SHA-256、以及大小与 SHA-256 均匹配但身份不符的其他程序被拒绝。
- 此次诊断不代表所有网络、代理和杀毒软件环境均已实测。GitHub Release 下载仍需网络可用，目标程序目录仍需当前用户可写。

本版继续使用 [PolyForm Noncommercial 1.0.0](LICENSE)。
