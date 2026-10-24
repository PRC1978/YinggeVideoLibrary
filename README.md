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

本地视频、索引和缩略图不属于源码仓库。TMDB Token 和 AI API Key 保存在当前 Windows 用户的凭据管理器中；新版本启动时会迁移旧配置文件中的明文密钥，并清理配置备份。启用在线 AI 功能后，软件会把对应任务所需的标题等信息发送到用户自行配置的 API 服务；本地规则分类无需联网。本机 OpenAI 兼容接口可使用 `http://127.0.0.1:端口/v1` 且不需要密钥。

AI 请求按顺序限速并重试暂时性错误。每日费用上限由用户填写的输入、输出 Token 费率估算，实际费用以 API 服务商账单为准。搜刮候选在本机缓存三天；审核记录和历史保存在共享数据目录。确实无匹配或可信度低的结果会显示在视频卡片上，并自动打上可追踪的“素人”标签；网络错误不会被当作无匹配。

“搜刮”页提供批量入队、范围预览和可保存的跳过规则，包括文件夹及其子文件夹、已看状态、标签、文件名关键词和最小大小。筛选规则也会作用于已排队但尚未处理的视频。标题搜刮会展示按匹配分数及来源权重排序的多个候选。审核页可按状态、来源和可信度筛选，批量审批前显示预览，并提供撤销、完整历史及 CSV 导出。

重复视频检测只比较同样大小的视频，再在后台计算完整 SHA-256；不会自动删除文件。保存的智能筛选器在搜索框右键菜单中管理。文件夹变动由监听器增量更新共享索引，分类图在数据没有变化时不重复构建。

本仓库暂未附带开源许可证；公开源码不代表自动授予再分发或修改许可。
