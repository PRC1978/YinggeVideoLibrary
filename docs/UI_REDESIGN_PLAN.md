# 映格视频库 前端重构与主题系统 设计稿（第一阶段）

基线：`525f73f`（codex/next-integration 分支头）→ 工作树 `codex/ui-redesign`。
日期：2026-11-03。状态：方向核对稿，尚未动代码。

## 一、目标与非目标

目标
- 重做信息架构与交互：视频库、分类与图形分类、文件夹、搜刮任务、人工审核、人物、推荐、在线、插件、设置共 10 个一级页面。
- 四套可即时切换、重启后恢复的视觉风格：简洁浅色 / 深色影院 / 紧凑高密度 / 高对比护眼。
- 适配 800×550、1240×780、1600×1000 与 100%/125%/150% 缩放；按钮不裁切、键盘可达。

非目标（保持不变）
- 数据格式、搜刮队列、审核事务、插件、更新、备份的功能与语义一律保留。
- 分类图不删节点、不删关系线、不删操作。
- 不在 UI 线程做海报解码、大量卡片重建或整库遍历。
- 不安装、不推送、不发布；测试只用 `--data-dir` 隔离库。

## 二、新信息架构

| 一级页 | 现状来源 | 二级结构与关键状态 | 主要改动 |
| --- | --- | --- | --- |
| 视频库 | 侧栏「全部/最近/收藏」+ 卡片流/完整列表 | 页内分段视图（全部/最近/收藏）；搜索+排序工具栏；批量选择栏；分页跳页 | 三个侧栏项合并为一个页面，视图切换改为页内分段控件 |
| 分类与图形分类 | CategoryPanel + SphereGraph（MainWindow.SphereGraph.cs 1666 行） | 二级：分类工作台 ｜ 球形关系图 | 节点、真实共现边、拖动预览、撤销记录全部保留；只做视觉令牌化 |
| 文件夹 | FoldersPanel + FolderWorkspace | 扫描根管理 + 图形目录浏览；并入原状态页「本地文件检查」 | 状态页拆分迁移，功能不删 |
| 搜刮任务 | ScrapePanel（RegroupScrapePanel 已分三个 Tab） | 二级：任务与队列 ｜ 来源与凭据；底部并入原状态页「后台任务/运行消息」 | 审核 Tab 迁出为独立一级页 |
| 人工审核 | 搜刮页 Tab2 + ReviewWorkflow/Candidates/ReviewFieldDialog | 二级：审核队列（筛选/命名视图）｜ 候选详情（逐字段对比 + 封面对比/大图）｜ 审核历史（多次撤销、CSV/JSON 导出） | 升级为一级页；候选资料与封面在审核主视图直接可见 |
| 人物 | 独立 RoleProfilesWindow | 嵌入主窗口为一级页（人物列表 ｜ 档案详情/关联作品） | 由独立窗口改为页内视图；OpenRoleProfiles 保留兼容入口 |
| 推荐 | RecommendationPanel | 推荐理由、减少此类、换一批、在线主题 | 视觉令牌化，逻辑不动 |
| 在线 | OnlinePanel（WebView2） | 站点切换、缩放、外部打开 | 工具栏收紧，逻辑不动 |
| 插件 | PluginsPanel（现 ShowView 高亮误指 SettingsNav） | 插件列表、权限声明、日志、插件搜刮 | 成为正式导航项并修复高亮归属 |
| 设置 | SettingsPanel + SettingsLayout（已有 9 张折叠卡片，含外观分区） | 主题选择进外观分区；其余分组保持：视频库/搜刮/插件/AI/播放/在线/启动/更新/数据备份 | 主题切换器 + 搜索设置主题保留 |

原「状态」页取消为独立一级页：本地文件检查→文件夹页；后台任务与运行消息→搜刮任务页底部。无功能删除。

## 三、主题系统方案

- 新增 `UiTheme`（静态语义令牌：Surface/SurfaceAlt/Text/TextSecondary/Accent/Border/SelectionBackground…），底层是 Application.Resources 中一组未冻结 `SolidColorBrush`，XAML 用 `DynamicResource`，代码后置改为读取令牌。
- 四个主题=四组令牌值 + 密度令牌（字号/间距/卡片尺寸/圆角）：浅色（现状微调）、深色影院（深底+海报放大+暖金强调）、紧凑高密度（小字号小间距小卡片）、高对比护眼（浅绿底+深色文字+明显焦点框）。
- 持久化：`LibraryConfig`（MainWindow.xaml.cs:35-134）增加 `uiTheme` 字符串字段（默认 `light`），走现有 SaveConfig 三路合并写盘（进程级 Mutex + 临时文件 + .bak）；启动时在窗口初始化前应用；切换即时生效（DynamicResource 自动刷新，无需重建视觉树）。
- 迁移面（实测）：C# 侧 200 处 `new SolidColorBrush` + 254 处 `Color.FromRgb` + 154 处 `Brushes.*`，分布在 20+ 个分部类（最高频：xaml.cs 38、FolderWorkspace 21、MetadataRatings 16、SphereGraph 15、TagBrowser 14）；XAML 侧约 29 种硬编码色。已有唯一接近令牌的抽象：SettingsLayout.cs:238 `Tint()`（40 处调用），可作为迁移桥。分批替换，不一次全改：先壳层与卡片模板，再各页面分部类。

## 四、改动范围（按文件）

| 文件 | 改动类型 |
| --- | --- |
| UiTheme.cs（新增） | 主题令牌、四套主题定义、应用/切换/持久化接口 |
| Themes/*.xaml（新增 4 个资源字典） | 各主题令牌值与关键控件样式覆盖 |
| App.xaml | 引入主题资源字典 |
| MainWindow.xaml | 壳层重排：新侧栏（10 项）、页内分段视图、审核页容器；颜色改令牌 |
| MainWindow.xaml.cs | ShowView 增加 review/roles 目标；视图合并（全部/最近/收藏）；插件高亮修复 |
| MainWindow.ScrapeLayout.cs | 二级页改为「任务与队列 / 来源与凭据」；审核内容迁出 |
| MainWindow.ReviewWorkflow.cs / Candidates.cs / ReviewFieldDialog.cs / ScrapeCover.cs | 审核一级页布局：队列+详情+封面对比并列可见；逻辑不改 |
| MainWindow.RoleProfiles.cs / RoleProfilesWindow.cs（纯 C# 窗口，1067 行，无 XAML） | 人物嵌入主窗口：新建页内宿主复用其构建逻辑；保留独立窗口为兼容入口 |
| MainWindow.SettingsLayout.cs / SettingsOptions.cs | 主题选择器放进已有的外观分区（BuildAppearanceOptions :56）；设置项写 uiTheme |
| MainWindow.xaml.cs（LibraryConfig） | 新增 uiTheme 字段 |
| MainWindow.FolderWorkspace.cs / StatusPanel 相关 | 状态页内容迁入文件夹页与搜刮页 |
| 其余 20+ 个含硬编码颜色的分部类 | 逐文件换令牌，不改行为 |
| MainWindow.UiAcceptance.cs / App.xaml.cs | 新增 `--diagnose-ui-theme`（四主题×三档窗口截图+布局断言）；扩展既有布局诊断 |

估算：新增约 6 个文件；修改约 30 个文件；不删除任何业务文件。

## 五、验收计划（第二阶段）

1. Release 构建 0 警告 0 错误。
2. `--self-test-features` 等既有离线自检全部退出码 0（证明功能语义未变）。
3. 新增 `--diagnose-ui-theme`：四主题 × 800×550/1240×780/1600×1000 离屏截图 + 横向裁切断言；125%/150% DPI 按既有方式补测。
4. 主题切换→重启→恢复：`--data-dir` 隔离库中切换后检查 config.json 的 uiTheme，重启进程确认恢复。
5. 性能对照：同一 3 万部合成库（`--ui-fixture`），改前改后各跑搜索布局与完整显示耗时、最长单次界面停顿，方法同 UI_CARD_REUSE 验收。
6. 分类图节点数/边数断言：改前改后一致（图索引逻辑不动，以现有诊断为证）。
7. 交付：提交号、构建与测试结果、三档窗口×缩放截图、保留功能清单、未验证项。

## 六、已识别风险

- 硬编码颜色替换量大（228 处），可能出现个别遗漏导致某主题下对比度差——靠四主题截图逐页核对兜底。
- 人物页嵌入主窗口涉及 RoleProfilesWindow 的宿主改造，工作量集中；若超范围，退回「保留独立窗口+导航入口」方案（等待你的决策）。
- 状态页拆分涉及 3 个接收方，需逐条核对既有诊断入口（缺失文件自检）指向新位置。
