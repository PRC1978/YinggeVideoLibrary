<div align="center">
  <img src="docs/images/yingge-mark.svg" width="80" height="80" alt="映格视频库图标">
  <h1>映格视频库</h1>
  <p>把分散在硬盘里的视频，整理成可搜索、可审核、可探索的个人资料库。</p>
  <p><strong>Windows x64</strong> · <strong>v1.5.3</strong> · <a href="LICENSE">源码公开 · 仅限非商业使用</a></p>
  <p>
    <a href="https://github.com/PRC1978/YinggeVideoLibrary/releases/download/v1.5.3/YinggeVideoLibrary-Setup.exe"><strong>下载安装版</strong></a>
    &nbsp;·&nbsp;
    <a href="https://github.com/PRC1978/YinggeVideoLibrary/releases/download/v1.5.3/YinggeVideoLibrary-win-x64.exe"><strong>下载免安装版</strong></a>
  </p>
  <p>
    <a href="#它能做什么">功能</a> ·
    <a href="#开始使用">上手</a> ·
    <a href="#数据与联网">数据与联网</a> ·
    <a href="docs/USER_GUIDE.md">详细指南</a> ·
    <a href="RELEASE_NOTES_1.5.3.md">1.5.3 发布说明</a>
  </p>
</div>

---

映格视频库是 Windows 本地视频管理工具。它扫描你指定的文件夹，为视频建立索引，再把分类、标签、人物、来源资料和审核记录放在同一个界面。**视频原文件留在原位置**；只整理本地视频时，无需注册账号或配置 API Key。

## 它能做什么

| 想做的事 | 映格提供的功能 |
| --- | --- |
| 找到硬盘里的视频 | 多文件夹扫描、缩略图、搜索、分页、图形文件夹浏览；按分类、标签和观看状态筛选。 |
| 批量整理资料库 | 全库或当前筛选结果选择、跨页 Shift 连选、批量分类与标签、重复视频识别；部分批量操作可撤销。 |
| 补全作品信息 | 按番号、标题和适用的文件夹名称查找多个来源；保留候选、匹配证据、可信度及逐字段来源。 |
| 控制搜刮结果 | 先预览再入队，按文件夹等条件跳过；队列可暂停、继续或取消，低可信度和冲突结果进入人工审核。 |
| 探索关联内容 | 球形分类关系图、人物与关联作品、个人和平台评分、带理由的本地推荐。 |
| 管理长期使用的数据 | 定期备份与保留轮数、跨设备导入预览、可选 AI 辅助、软件内更新并重启。 |

<p align="center"><img src="docs/images/workflow.svg" width="900" alt="视频索引、搜刮审核与分类探索的功能示意图"></p>
<p align="center"><small>功能关系示意；不展示用户的视频、路径或封面。</small></p>

## 开始使用

1. **下载并打开。** 推荐使用[安装版](https://github.com/PRC1978/YinggeVideoLibrary/releases/download/v1.5.3/YinggeVideoLibrary-Setup.exe)，按向导安装即可；也可把[免安装版](https://github.com/PRC1978/YinggeVideoLibrary/releases/download/v1.5.3/YinggeVideoLibrary-win-x64.exe)放在当前用户可写的文件夹中直接运行。安装版只为当前 Windows 用户安装，无需管理员权限。
2. **添加视频文件夹。** 在“全部”页点击“添加文件夹”。扫描后可用列表、分类页或“文件夹”页浏览；视频仍保留在原目录。
3. **按需搜刮。** 在“搜刮”页选择来源和跳过条件，先预览入队范围。搜刮会显示多个候选和来源证据；在审核页确认后再采用不确定的资料。

安装版与免安装版共用当前 Windows 用户的数据目录。软件可检查 GitHub Release 中的新版本，显示更新内容；选择更新后下载并校验独立程序，退出旧程序、替换并重启。**软件所在目录须可写**，否则按提示手动下载安装包。下载文件及校验值见[本版 Release](https://github.com/PRC1978/YinggeVideoLibrary/releases/tag/v1.5.3)。

**从 1.5.1 或 1.5.2 升级：** 这两个版本的软件内下载校验有缺陷，可能在下载完成后提示“大小、SHA-256 或程序身份与目标版本不符”。首次升级到 1.5.3 时，请关闭旧版并运行本页的安装包；使用免安装版的用户可下载新版程序，将其改名为 `映格视频库.exe` 后替换原目录中的同名文件。此操作不移动当前 Windows 用户的数据目录。升到 1.5.3 后，后续版本可继续使用软件内更新。具体原因见[1.5.3 发布说明](RELEASE_NOTES_1.5.3.md)。

## 数据与联网

- **本地数据：** 默认保存在 `%USERPROFILE%\.YinggeVideoLibrary`，包括索引、缩略图、分类和审核历史；添加视频库不会复制视频文件。
- **接口凭据：** TMDB Token、AI API Key 等保存在当前 Windows 用户的凭据管理器中。备份不包含这些凭据。
- **可选联网：** 启用在线搜刮或云端 AI 后，相关查询才会发送到所选服务；本地浏览、分类和规则处理无需连接这些服务。各来源的适用范围、凭据要求及已验证范围见[详细指南](docs/USER_GUIDE.md)和[1.5.2 发布说明](RELEASE_NOTES_1.5.2.md)。

## 更多文档

- [使用与维护指南](docs/USER_GUIDE.md)：搜刮来源、队列、审核、分类、人物、备份、设置和源码构建。
- [1.5.3 发布说明](RELEASE_NOTES_1.5.3.md)：本次更新修复与升级方法。
- [1.5.2 发布说明](RELEASE_NOTES_1.5.2.md)：人物档案自动搜刮与非商业许可变更。
- [版本方案](NEXT_VERSION_PLAN.md)：原阶段目标与功能对照；计划项不等同于当前已实现功能。
- [非商业使用许可](LICENSE)：本项目源码公开，个人及其他许可允许的非商业用途可使用、修改和分发；商业用途请先通过 [GitHub Issues](https://github.com/PRC1978/YinggeVideoLibrary/issues) 联系 PRC1978 取得单独授权。第三方组件、标志和外部资料遵循各自的许可与来源要求，详见[指南中的说明](docs/USER_GUIDE.md#许可与第三方资料)。
