# UI 重构 43 项验收（2026-11-06）

## 结论与口径

基线为 `codex/ui-report-integration` 的 `8aa504d`（v1.6.10）。按 `D:\deepseek\yingge-review\THEME_REFACTOR_TASKS.md` 的 R01–R10、T01–T33 逐项核对。**43 项都已检查，但项目未通过全量验收**：试点门槛 T18 和最终门槛 T32 缺少第三方盲测，另有 Compact 密度、Classic 语义对比度、主题准入校验器等明确缺口。D2 已决定的七项实验风格保持「暂不派工」，不冒充已完成。

本次对现有界面做了两处低风险修复：导航按钮补全 UIA 名称；同一视频库被另一实例占用时，把 PostgreSQL 锁的英文系统错误换成明确的中文恢复指引。没有停止用户已安装的应用或修改真实视频库。

### 取证方法

- 完整 Release 重建：`dotnet build YinggeVideoLibrary.csproj -c Release -t:Rebuild -p:OutputPath=artifacts/ui-full-acceptance/build/ -p:EnableSourceLink=false --nologo`，最终改动后复跑 0 警告、0 错误。
- 真实窗口 UIA：`artifacts/ui-full-acceptance/uia-names.json`，11 个导航按钮及搜索、排序控件共 13/13 名称非空。
- 私有 PostgreSQL 回归：`artifacts/ui-full-acceptance/postgres-probe.json`，`passed=true`、`stopped=true`、`privateClusterLifecycleVerified=true`；其中包括重复打开同一隔离库被拒绝的断言。启动过的多余隔离探针和它的 PostgreSQL 进程已按专属目录停止，未触碰用户进程。
- 隔离库：`artifacts/ui-full-acceptance/fixture30/`，30 条索引、30 个生成的可播放 AVI、10 张生成海报；不含私人媒体。另用 600 条隔离库跑离屏主题诊断。
- 真实窗口：`scripts/capture-ui-full-acceptance.ps1` 启动独立进程，固定窗口 1240×780，以 Windows UI Automation 切页并回读 `PageTitle`，用 `PrintWindow(PW_RENDERFULLCONTENT)` 存入 `artifacts/ui-full-acceptance/printwindow30/`。15 张图均由真实窗口 API 保存；**其中 Classic 的 `roles` 图片仍是分类页，不能算人物页证据**。其余 14 张标题正确。单独重放 Classic 人物入口时，UIA Invoke 后 10 秒内没有出现人物窗口或错误弹窗；这仍不能区分自动化调用未生效与产品问题，需真实鼠标交互复核。脚本使用带 BOM 的 UTF-8，兼容 Windows PowerShell 5.1。
- 离屏主题诊断：`artifacts/ui-full-acceptance/theme/`，285 张图、`theme-layout.json`、`contrast-raw.json`（300 个原始比值）、`theme-switch.json`（20 次切换）；它不能代替上条真实窗口截图。
- 动画诊断：`artifacts/ui-full-acceptance/motion.json`，smooth / balanced / animated 及中途切换无残留，均通过。
- 对比度：五个主主题的 300 个语义组合中，295 个 ≥4.5:1；Classic 原色有五个低于该值（3.05、3.24、3.08、4.30、3.43）。完整逐行数值在 `contrast-raw.json`。Classic 的逐像素锁定目标与此处的对比度目标存在真实冲突，不能同时记为通过。

### R01–R10

| 项 | 状态 | 证据与尚缺内容 |
| --- | --- | --- |
| R01 空分类页不露空库卡 | 仅代码检查 | `MainWindow.xaml.cs` 分类空态分支已排除 `categories`；本轮没有空库真实窗口 UIA JSON 与截图双证。 |
| R02 节点单击只放大一次 | 仅代码检查 | `MainWindow.SphereGraph.cs` 点击路径只排一次居中；未采到真实点击后的 120% UIA 读数。 |
| R03 折叠侧栏在线页标题 | 仅代码检查 | `MainWindow.ChromeModes.cs` 给折叠在线页留 48px；本轮无在线页折叠截图。 |
| R04 重进分类页回到 fit | 离屏通过 | `ShowView` 进入分类时重置；主题诊断记录 100% 视图，未做真实窗口重进路径。 |
| R05 清理在线页跨列死代码 | 代码通过 | 原无效列设置已移除；无真实在线页回归截图。 |
| R06 拟物双向浮雕 | 部分 | `SoftHighlight` 已绑定设置卡，但静止 / 按下 / 禁用三态未逐一截图核验。 |
| R07 量化线条四档 | 部分 | `UiTheme.cs` 有 0.5/1/2/4 四档和检测框；未取得真实窗口该风格截图。 |
| R08 卡片圆角令牌 | 离屏通过 | `CardCornerRadius` 已在视频卡 XAML 绑定，主题诊断断言卡片宽度；9 风格逐张半径测量未做。 |
| R09 冷启动占位 | 仅代码检查 | `StartupVeil` 已在 XAML 首帧，缺 t=0/130ms 真实窗口双帧。 |
| R10 在线页首次加载提示 | 仅代码检查 | `OnlineLoading` 已有中文初始化文案；缺 850ms 画面与多次耗时读数。 |

### T01–T18

| 项 | 状态 | 证据与尚缺内容 |
| --- | --- | --- |
| T01 圆角令牌 | 同 R08：部分 | 有实际 XAML 绑定；尚无 9 风格逐张半径证据。 |
| T02 有数据态图 | 部分 | 五主主题×三屏已执行 PrintWindow；14 张有效，Classic 人物页缺失。原任务九风格×三屏未达到。 |
| T03 线条四档 | 同 R07：部分 | 四档代码存在，缺真实窗口材质验收。 |
| T04 拟物三态 | 同 R06：部分 | 双阴影绑定存在，缺按下/禁用态。 |
| T05 图卡完整可见 | 已实测 | 1240×780 的五主主题分类截图中整卡下沿在窗口内；离屏 `graphCardBottom` 分别 759.01、702.68、666.72、736.62、726.01。 |
| T06 常驻 inspector | 已实测 | `atlas-categories.png` 中画布与右侧来源/子分类/操作面板、节点列表同屏；节点选择后的来源聚合已有外部报告，本轮未重放该点击。 |
| T07 关闭按钮与 UIA 名称 | 本轮回归通过 | 导航按钮、搜索框、排序框都补显式名称；最终构建的真实窗口 `uia-names.json` 13/13 通过。关闭按钮沿用旧报告证据，本轮未重测点击。 |
| T08 动画残留 | 部分 | 应用三档动效及中途切换在 `motion.json` 全过；清单要求的系统级路径未核验。 |
| T09 七维契约 | 代码与诊断通过 | `PresentationContract.cs` 七个强类型维度，任意主主题差异数的注册检查存在；**静态契约值不等于真实界面已呈现这些差异**。 |
| T10 材质策略 | 部分 | 业务视图未再引用四个 `Is*` 标志；`UiThemeState` 仍通过布尔分支组合材质，尚未成为统一策略文件。 |
| T11 四层色板 | 部分 | 主 XAML 原始 hex 为 0；L1 Primitive / L2 Semantic / L3 Component / L4 Mode 的完整分层与组件只用 L3 尚未落地。 |
| T12 独立明暗 Mode | 离屏通过 | 五主主题×四 Mode 图及比值已生成；同主题模式切换时契约与卡片宽度不变。 |
| T13 六角色字体 | 部分 | `UiThemeState` 有 Display/Heading/Body/Data/Mono/Caption 六访问器；未满足各主题双字族、完整 1.25 字阶及等宽数字验证。 |
| T14 八色数据序列 | 未完成 | 没有跨分类、人物、统计共享的八色 bg/border/fg 色板及色盲核验。 |
| T15 Cinema 放映厅 | 已实测 | `cinema-all.png` 中横向海报带与底部胶片轴均可见；与普通网格不同。 |
| T16 Compact 控制台 | 未通过 | `compact-all.png` 的 1240×780 真实窗口仅能看到约 22–23 条，未达到每屏 ≥36 条。清单同时要求 24px 行高，36 行需要 864px，已大于 780px 的整个窗口高度；这两个判据在该窗口尺寸下无法同时满足。需重新定义目标窗口或采用双列列表，不能靠继续压缩字体冒充完成。 |
| T17 Atlas 试点 | 已实测 | `atlas-categories.png` 有画布与右侧 inspector，离屏图索引 540 节点且无断链；默认 fit，图卡全露。 |
| T18 试点硬门槛 | 未通过 | 有数据态截图基本具备，但没有独立第三方的去色盲测 ≥2/3 原始识别矩阵；六条硬判据也未全过。 |

### T19–T33

| 项 | 状态 | 证据与尚缺内容 |
| --- | --- | --- |
| T19 Tech 工程仪表盘 | 暂不派工 | D2 降级为配色/实验风格。 |
| T20 Contrast 线性阅读器 | 暂不派工 | D2 降级为配色/实验风格。 |
| T21 Glass 分层景深 | 部分 | `glass-all.png` 有深色光井、半透明控件与卡片，但仍与普通卡片网格同骨架；没有可复核的三层景深、视差与分层模糊半径实测。 |
| T22 Neumorphism 可按压面板 | 暂不派工 | D2 降级，现有实验效果不作为独立主题验收。 |
| T23 Editorial 栏式档案 | 暂不派工 | D2 降级。 |
| T24 Classic 逐像素锁定 | 未验证 | 本轮有当前版本截图，缺相同数据/窗口状态的锁定基线像素比较；不得由颜色代码推断逐像素一致。 |
| T25 盲测分级 | 部分 | 五主主题与「更多配色与实验风格」分组已接线；没有去色盲测原始结果。 |
| T26 Atlas 正式图谱 | 已实测 | `atlas-categories.png` 显示同屏画布、inspector 与节点列表；图卡下沿 736.62＜780。节点选择与 pan clamp 仍主要是离屏证据。 |
| T27 Mono 无色基准 | 暂不派工 | D2 转实验组。 |
| T28 Terminal 命令终端 | 暂不派工 | D2 转实验组。 |
| T29 Neon 霓虹夜 | 暂不派工 | D2 转实验组。 |
| T30 设置页选择器 | 离屏通过 | `atlas-1240x780-settings.png` 显示五种不同结构缩略、说明及明暗选项，另有「更多配色与实验风格」。 |
| T31 主题准入校验器 | 未完成 | 仅校验 ID 唯一与七维差异；未将缺截图、盲测不达标、对比度不达标接入注册拒绝。 |
| T32 全量验收 | **未通过** | 14/15 张有效真实窗口图；第三方灰度识别矩阵缺失；Classic 5 个对比度组合低于 4.5；T16、T31 不达标，故不能勾选。 |
| T33 弃用与分组策略 | 部分 | Core 与实验风格已分组，尚无「连续两版零使用」计量与自动降级。 |

## 对本轮新增报错截图的核对

截图中的 `postgres-cluster-owner.lock` 是独占锁；只靠锁文件存在不能认定故障。验收时进程列表确有另一份已安装的映格程序及其本地 PostgreSQL 正在运行，符合“第二实例尝试访问同一库”的情况。未停止该实例，也未打开真实库内容。`PostgresPrivateCluster.OpenAsync` 现将共享冲突改为“另一份映格正在使用或转换该视频库”的中文说明，仍保持拒绝双写。隔离数据库完整回归已通过，含重复打开同一库被拒绝和关闭后再打开成功。

## 下一轮必须补齐的证据与实现

1. Classic 独立人物窗口的真实点击及截图；明确 UIA Invoke 未出现窗口是自动化限制还是产品缺陷。修正 15 图清单。
2. Compact 在目标窗口内真实可见 ≥36 条；保留筛选、批量操作和键盘路径。
3. 选一位与实现无关的评审者，对匿名去色截图给出原始识别矩阵；不能以开发者自认代替。
4. Classic 的视觉锁定与文本可读性冲突须在产品层定口径，随后复测原始对比度。
5. 完成四层色板、六角色字体尺度、八色序列及主题准入校验器；为 Glass 提供可测的层级/模糊/视差参数。
6. 补 R01–R03 的真实窗口双证、在线加载时间、冷启动双帧及系统动画路径。

当前结论只对这份独立工作树有效；没有把以上不达标项写成已验收或已发布。
