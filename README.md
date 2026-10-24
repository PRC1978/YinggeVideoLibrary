# 映格视频库

Windows 本地视频管理工具。支持文件夹扫描、缩略图、分类与标签、搜索、元数据搜刮、AI 辅助审核和内嵌网页。

## 安装

从 [Releases](https://github.com/PRC1978/YinggeVideoLibrary/releases) 下载 `YinggeVideoLibrary-Setup.exe`。安装程序为当前 Windows 用户安装，无须管理员权限。需要 Windows x64。

视频文件不会复制到安装目录。设置、索引和缩略图保存在用户目录下的 `.YinggeVideoLibrary`，升级或卸载安装程序不会自动删除这些数据。首次启动会尝试从旧版缓存导入。

软件会在后台检查 GitHub 上的新版本，也可以在“设置 → 软件更新”中手动检查。下载的安装包经过 GitHub Release 提供的 SHA-256 校验后才会启动；安装由用户完成。

## 构建

需要 .NET 9 SDK。生成单文件 Windows 程序：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o '..\发布'
```

使用 Inno Setup 7 的 `ISCC.exe` 编译 `YinggeVideoLibrary.iss`，安装包输出到 `..\安装包`。发布新版本时，同步更新 `.csproj` 和安装脚本中的版本号，创建对应的 `vX.Y.Z` GitHub Release，并上传名称固定为 `YinggeVideoLibrary-Setup.exe` 的安装包。

## 数据与凭据

本地视频、索引、缩略图和用户填写的 API 密钥不属于源码仓库，也不会随安装包上传。启用在线 AI 功能后，软件会把对应任务所需的标题等信息发送到用户自行配置的 API 服务；本地规则分类无需联网。

本仓库暂未附带开源许可证；公开源码不代表自动授予再分发或修改许可。
