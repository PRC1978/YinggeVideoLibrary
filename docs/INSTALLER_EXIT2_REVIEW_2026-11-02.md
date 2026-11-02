# 1.6.0 安装器退出码 2 静态复核

未运行安装器，也未修改系统缩放或已安装程序。

Inno Setup 官方文档将安装程序退出码 `2` 定义为：用户在安装开始前取消向导，或在开场确认框选择“否”。它不是“安装过程发生致命错误”的退出码。两次尝试都没有保存完整命令行和 `/LOG` 日志，所以只能确认“安装未开始”，不能确定取消来自开场提示、测试自动化还是其他交互。安装包能否完成安装与升级仍未验证。官方说明：<https://jrsoftware.org/ishelp/topic_setupexitcodes.htm>；静默参数说明：<https://jrsoftware.org/ishelp/topic_setupcmdline.htm>。

`MainWindow.Updates.cs` 的应用内更新只读取 Release 中的 `YinggeVideoLibrary-win-x64.exe`；`YinggeVideoLibrary-Setup.exe` 是手动安装用资产。前者的固定名称是应用内更新的硬要求，后者的固定名称用于 README 下载链接与发布约定。WorkBuddy 生成的版本后缀安装包已**复制**为标准发布文件名，原文件未改动。

本地暂存目录（不在 Git 中）：`C:\Users\yunwu\WorkBuddy\2026-10-31-23-02-00\final\release-staging-1.6.0`。

| 文件 | 字节数 | SHA-256 |
| --- | ---: | --- |
| `YinggeVideoLibrary-Setup.exe` | 51,890,545 | `5893f2bf0fffd087a44c5b289f84ab56fea1a84c967729778d6ebd5dec1cf651` |
| `YinggeVideoLibrary-win-x64.exe` | 174,067,918 | `e009e52712e419d21e8548650fbc8e528a1fbdfdf8e479f0e7c21200cfddf511` |

`SHA256SUMS.txt` 已按上述两个标准文件名生成。便携程序的 ProductVersion 为 `1.6.0+3d81be8…`；安装包 FileVersion 为 `1.6.0.0`。这些静态核对不替代安装后启动或安装版升级验收。
