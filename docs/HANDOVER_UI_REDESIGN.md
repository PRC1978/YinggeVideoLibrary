# 「映格视频库」前端重做 · 交接文档（转 GPT 继续）

> 交接时间：2026-11-03 12:55 ｜ 交接原因：前一助手额度用尽
> 本文档是唯一权威交接物：读完即可继续，不需要翻聊天记录。

## 0. 任务总目标（用户原话要点）

完全重做 Windows WPF 应用「映格视频库」的前端视觉与页面组织，以新品牌图标（钴蓝/靛蓝/紫）为基础，做克制、清晰、有科技感的桌面软件。提供五种可即时切换并持久化的界面模式：

1. **原版界面**（classic）：保留原布局/侧栏/入口的完整回退模式，**不只是原配色**——原导航、独立状态页、独立人物窗口都必须可用。
2. **科技浅色**（tech）：清爽底色、钴蓝品牌色。
3. **深色影院**（cinema）：深色背景、海报优先、暖金点缀。
4. **紧凑高密度**（compact）：同屏更多内容，文字可读。
5. **高对比护眼**（contrast）：高对比文字、明显焦点框。

硬性要求：不为五模式复制业务逻辑（数据/命令/搜刮队列/审核事务/插件共用）；保留全部现有功能（分类图节点连线、搜刮队列、审核门禁、插件、AI、备份恢复、更新）；UI 线程不做海报解码/整库卡片重建；切换模式尽量保留当前页面与状态。

## 1. 仓库与分支

- 主仓库（**不要动**）：`C:\Users\yunwu\WorkBuddy\2026-11-03-yingge-next-integration`（分支 codex/next-integration，基线提交 `525f73f`）
- 工作树（**在这里工作**）：`C:\Users\yunwu\WorkBuddy\2026-11-03-yingge-ui-redesign`，分支 `codex/ui-redesign`
- 构建：`cd` 到工作树后 `dotnet build -c Release`（必须 0 警告 0 错误）；可执行文件 `bin/Release/net9.0-windows/映格视频库.exe`
- 禁令：不修改主工作树、不读取真实视频库（测试一律 `--data-dir` 隔离库）、不安装、不推送、不发布。

## 2. 已完成（三个提交，全部构建绿、诊断过）

| 提交 | 内容 |
|---|---|
| `6bed168` | UiTheme 令牌系统（五模式调色板+密度）；uiTheme 配置字段+三路合并；设置→外观五模式单选；MainWindow.xaml 全令牌化（0 十六进制）；新图标接入程序/安装包（旧 app.ico 保留）；RoleProfilesWindow 拆分为 RoleProfilesView + 薄壳窗口 |
| `c649267` | 模式感知导航：classic 原样；新模式用新 IA（视频库分段 全部/最近/收藏、审核一级页、人物嵌入、插件正式导航项、状态卡按用途迁移）；ArrangeScrapeGroups 可反复重排；人物视图生命周期探针 |
| `45128f4` | App.xaml 全局控件隐式主题；C# 203 处颜色迁移（42 文件）；hex helper 改道 UiTheme.FromLegacyHex；`--diagnose-ui-theme` 五模式×三档×多页截图+不变量断言 |

## 3. 核心架构（改了会踩坑的地方）

### 3.1 双画刷集（UiTheme.cs，最重要）
- `liveBrushes`：C# 用。`UiTheme.Apply()` **就地改 Color**，所以 C# 一次性赋值（`x.Foreground = UiTheme.TextPrimaryBrush`）能跟随切换。**禁止把 live 画刷放进 Style setter**（会冻结）。
- `brushes`（显示集）：XAML 用，经 `UiThemeState.Instance` 绑定（`{Binding TextPrimaryBrush, Source={x:Static local:UiThemeState.Instance}}`）。Style setter 会冻结画刷，所以 Apply 时**换实例**+`PropertyChanged(null)` 全量通知。
- 新增 C# 颜色一律 `UiTheme.XxxBrush`；新增 XAML 颜色一律走 UiThemeState 绑定；hex 遗留走 `UiTheme.FromLegacyHex`。
- 五模式 id：`classic/tech/cinema/compact/contrast`；默认 `classic`（旧用户进原版，主动选才切新版）。

### 3.2 模式感知外壳（MainWindow.ChromeModes.cs）
- `ApplyInterfaceMode()`：切导航可见性、`ArrangeScrapeGroups()` 重排审核组、`RelocateStatusCards()` 迁移状态卡、回落不存在的页。启动时和切换模式时调用。
- 审核控件树在 classic 是搜刮页 Tab2，在新模式是 ReviewPanel 一级页宿主（同一批控件实例，只重挂父级）。
- 人物：classic `OpenRoleProfiles()` 弹窗；新模式 `ShowView("roles")` 嵌入 `RoleProfilesView`（懒创建，关窗 Cleanup，忙碌时拦截关闭）。
- `RoleProfilesView` 的 Cleanup **不能挂 Unloaded**（切页会卸载）；已挂 Loaded 重载数据。

### 3.3 关键既有机制（不要破坏）
- 视图切换中枢 `ShowView(string)`（MainWindow.xaml.cs ~1571）；`RegroupScrapePanel`（ScrapeLayout.cs）按锚点把控件分组。
- 配置：`LibraryConfig`（xaml.cs:35）→ `config.json`，三路合并 `MainWindow.ConfigMerge.cs`，新字段要同步 Clone+Merge 两处。
- 海报解码全在后台（PosterSource getter 不解码）；卡片复用 RecycledCardWrap/RecycledCardRow。
- 隔离：`--data-dir <绝对路径>`（AppPaths.cs 全参数扫描，位置无关）。

## 4. 诊断命令（顺序：诊断参数必须在 --data-dir 之前！）

```bash
EXE="./bin/Release/net9.0-windows/映格视频库.exe"
# 启动+页面验证（--roles/--settings/--scrape/--selection/--folders 等附加验证）
timeout 90 "$EXE" --diagnose-ui-startup out.json --roles --data-dir "D:\test\lib"
# 五模式×800×550/1240×780/1600×1000×关键页截图 + 导航不变量 + 样式探针
timeout 300 "$EXE" --diagnose-ui-theme "D:\test\shots" --data-dir "D:\test\lib"
# 搜刮页三档截图
timeout 120 "$EXE" --diagnose-ui-scrape "D:\test\scrape" --data-dir "D:\test\lib"
# 造 600/30000 部合成库（性能对照用）
"$EXE" --ui-fixture "D:\test\lib600" 600
```
坑：被强杀的进程会留 `.library-session.lock`，重跑前删掉；诊断分支按 `e.Args[0]` 分流，把 `--data-dir` 放第一个会开真实 GUI 挂起。

## 5. 遗留工作（按优先级）

1. **审核页/候选详情视觉验收**：审核队列行、候选比较、封面对比对话框在深色模式的截图核对（`--diagnose-ui-theme` 已出 review 页截图，目录 `artifacts/uitest/theme4/`）。
2. **C# 颜色残留**：`Color.FromArgb` 13 处、裸 `Color.FromRgb`（非画刷，渐变/画笔/阴影）42 处、 exotic Brushes.*（MidnightBlue 等）少量；`ColorOf("#")` 渐变（推荐页品牌带为刻意保留的钴蓝，注意甄别）。
3. **紧凑/高对比密度接入**：UiThemeState 已有 FontBase/CardWidth 等数值属性，卡片模板和页面字号尚未绑定密度（紧凑模式目前只换色，未真正「高密度」）。
4. **性能对照**：600/30000 部库改前改后搜索/翻页/滚动/分类图/主题切换耗时（沿用既有 --scroll 诊断与 UiFixtureGenerator）。
5. **键盘回归**：Tab/Enter/Esc 可达性抽查；高对比焦点框验证。
6. **已知小问题**：ScrapeQueue.cs「继续轮询」按钮在深色下偏亮（Action 样式正常但该按钮疑似有局部覆盖）；推荐页头部渐变带是刻意保留的品牌钴蓝（文字已固定白）。
7. **三档×三缩放截图**：100%/125%/150% DPI 未跑（离屏渲染固定 96dpi，需要真机或 DPI 模拟）。

## 6. 已踩过的坑（别再踩）

- **LibraryConfig.UiTheme 属性遮蔽 UiTheme 类型**：CS0236，默认值要 `global::YinggeVideoLibrary.UiTheme.Classic`。
- **System.Drawing vs System.Windows.Media**：Color/ColorConverter/RadioButton/Panel 歧义，显式限定。
- **窗口级隐式样式遮蔽 App 级**：MainWindow.xaml 原有隐式 ComboBox 样式（居中）导致 App.xaml 样式不生效；已合并删除。再遇到样式「不生效」先查 Window.Resources。
- **Style setter 冻结画刷**：不可就地改色，必须换实例+通知（见 3.1）。
- **批量 sed 替换 Brushes.***：会误伤 `WpfBrushes.White`、`System.Windows.Media.Brushes.White` 全限定写法；替换后必须 grep `WpfUiTheme|Media.UiTheme` 检查。
- **测试库目录**：工作树内 `artifacts/`已 gitignore；隔离库放 `artifacts/uitest/`。

## 7. 验收基线（用户要求）

- Release 构建 0 警告 0 错误。
- 只跑隔离库；功能回归：搜刮入队/取消、候选比较、审核预览/撤销、插件管理、备份恢复入口、更新提示。
- 分类图节点与连线数量不变。
- 800×550 / 1240×780 / 1600×1000 按钮不裁切，侧栏与二级页可达。
- 交付：提交号、改动文件、截图路径、原始测试 JSON、保留功能清单、未验证项清单。
