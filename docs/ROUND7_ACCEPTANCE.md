# 第七轮：插件队列限速、界面验收与发布准备

分支 `codex/queue-rate-limit`，起点 `0fda24c`（`codex/plugin-queue`）。
所有运行使用 `--data-dir` 指向隔离目录；真实库只做了**只读副本**演练，副本运行结束后真实文件哈希未变。
未安装、未推送、未发布。

编号说明：已实现 = 代码完成；已实测 = 有原始数据/截图；仅代码核对 = 只读过代码；未验证 = 没做成。

## 一、插件队列的限速与冷却（已实现、已实测）

### 问题

`plugin:<id>` 虽然进入了队列来源集合，但 `ProcessTitleScrapeQueueItemAsync` 直接调用插件进程，**绕过了** `RunQueueSourceAsync`：没有 `ReserveSourceRequest`、没有来源间隔、没有 `RecordSourceFailure` 的持久冷却。插件崩溃时每个队列项都会重新拉起进程。

### 改法

| 项 | 做法 |
| --- | --- |
| 同一套限速 | 插件调用改为 `RunQueueSourceAsync("plugin:<id>", …)`，与内置来源共用预约、间隔、冷却、失败计数 |
| 失败即来源状态 | 新增 `InvokePluginQueueAsync`：进程失败转成 `ScrapeSourceUnavailableException`，由包装器写入 `scrape-source-backoff.json`；插件自带 `retryAfterSeconds`（如 rate_limited）作为冷却下限 |
| 插件之间独立 | 每个插件一个来源键，冷却互不影响；队列按 DisplayName 顺序尝试，处于冷却的插件**跳过**而不是等待 |
| 不阻塞内置来源 | 插件全失败后照常进入内置标题来源；只有「没有内置来源」时才保留任务 |
| 不误写「无匹配」 | 插件路径只抛 `ScrapeSourceUnavailableException` / `InvalidDataException`，从不调用 `StoreUnmatchedScrape` |
| 不能无限重试 | 「只有插件来源且无结果」时累计 `Attempts`：1→2→3 次按 10/20/40 s 退避，第 4 次记为失败（可手动重试） |
| 键与调用一致性 | `QueuePlugins()` 一次取出，既生成 `titleSources` 又传给执行函数；执行前再校验 `PluginIdFromSourceKey(key) == plugin.Id`，不一致就不调用 |

配套：`PluginLog` 时间戳精确到毫秒，用于核对请求间隔；示例插件支持 `plugin-mode.txt` 强制失败模式（便于同一库内放一好一坏两个插件）。

### 实测（`--diagnose-plugin-queue`，真实窗口 + 真实队列循环 + 真实插件进程）

| 场景 | 结果 | 判定 |
| --- | --- | --- |
| 连续 3 个标题任务（插件来源间隔设为 6 s） | 3 次调用，间隔 **5810 / 5955 ms**；3 条审核均 pending，队列清空 | `multi` 通过 |
| 同上，间隔设为默认 2.5 s（对照） | 间隔 **3535 / 3249 ms** | 证明间隔来自按来源策略，不是常量 |
| 插件崩溃（`consecutiveFailureLimit=1`） | 只调用 1 次；冷却 3586 s；任务保留、`attempts=1`、残留进程 0、无「无匹配」 | `crash` 通过 |
| 紧接上一步重启 | **0 次新调用**；冷却截止时间与上一进程完全一致（12:40:21）；任务仍 pending | `restart` 通过 |
| 插件超时（挂起 90 s，宿主 15 s 超时） | 1 次调用，错误码 `timeout`，冷却 3568 s，残留进程 0 | `timeout` 通过 |
| 一坏一好两个插件 | 坏插件被冷却（`plugin:com.example.bad`），好插件照常返回 3 条候选并进审核 | `independence` 通过 |
| 执行中取消（`slow` 模式，3.5 s 后取消） | 1 次调用被中断，**残留进程 0**，任务保留，未写入冷却（用户取消不是来源故障） | `cancel` 通过 |
| 重复入队 | 同一视频入队两次 → 队列 1 条；运行后候选 3 条、审核记录 1 条、无重复 | `duplicate` 通过 |
| 插件崩溃 + 启用 TVmaze | 插件冷却；TVmaze 照常执行并给出「没有找到标题候选」，该「未匹配」记录的 `decisionSource=rules` | `builtin` 通过 |

原始结果：`fix7/ev/queue-{multi,crash,restart,independence,cancel,duplicate,timeout,builtin}.json`，每次均含 `requestLog`（时间戳+插件）、`queueAfterRun`（attempts/failed/nextAttemptAt/lastError）、`sourceCooldowns`、`residualPluginProcesses`。

队列级自检 `--self-test-plugin-queue` **17/17**（新增来源键往返、冷却持久化、冷却不阻塞内置来源/另一插件、健康插件保留自己的时间表）。

## 二、界面验收（部分完成）

先说工具侧查清的三件事，它们解释了前几轮为什么点不中：

1. `shot` 把整窗渲染进客户区大小的位图，**每张截图纵向偏 31 px**（标题栏高度），从图上量的坐标必然错位。已改为按窗口矩形渲染再裁客户区。
2. 点击辅助把客户区坐标当屏幕坐标去校验，只有窗口在 (0,0) 时才通过；已修正为「客户区坐标 → 转屏幕 → 校验归属」。
3. `AutomationElement.BoundingRectangle` 对页面 `ScrollViewer` 内的控件返回的是**滚动内容坐标**（实测下拉框报告 y=5907，实际在 y≈727），根本不能用来定位点击。

随后发现一条环境事实：**前台是《帝国时代 IV》全屏窗口**（pid 40604，rect 312,160 1936×1119），抢占鼠标会直接影响用户操作。因此后续**全部改用 UI Automation 的 API 调用，不再注入任何鼠标/键盘事件**。

| 项 | 状态 | 证据 |
| --- | --- | --- |
| 候选 1/3 → 2/3 切换 | **已实测（产品代码路径）**：`--diagnose-review-select` 调用审核行 `SelectionChanged` 所调用的同一个 `ApplyPluginCandidateSelection`，EX-001 → EX-002，`status=pending` 不变 | `ev/select.json` |
| 重启后仍选中 2/3 | **已实测（UI 读回）**：新进程打开同一库，`uia-rect` 读控件 `SelectionPattern` 返回 `selected=候选 2/3 · 示例来源 · EX-002` | `shots/review-after-switch.png`、`ev/ui-switch.json` |
| 通过 / 否决按钮 | **未验证**：按钮先弹「逐字段审核候选」窗口（1050×720），再弹 Yes/No 预览；应用不在前台时 UIA 全树枚举阻塞（`uia-list` 超时），无法在不抢前台的前提下走完 | — |
| 候选文件写入失败提示 | **未验证**：同样需要点击插件页按钮 | — |
| 清理日志写入失败弹窗 | **未验证**：需要点击状态页按钮并确认两个弹窗 | — |

**没有用代码检查或构造截图代替这些未验证项。**

## 三、真实海报下的性能（已实测）

隔离库：600 部视频 + **600 张 600×900 真实 JPEG 海报**（`thumbs/<id>.jpg`，解码+缩放都真实发生），120 个分类叶子。三档批大小交替跑两轮，同机同库。

| 每批张数 | 搜索布局 P50 | 布局最大 | 最长单次界面停顿 | 分类页 60 卡到位 | 滚动 P95 | 滚动 P99 |
| --- | --- | --- | --- | --- | --- | --- |
| **6（定档）** | **374 ms** | 519 ms | **44.5 ms** | **1382 ms** | 11.3 ms | 31.8 ms |
| 8 | 365 ms | 439 ms | 59.0 ms | 1195 ms | 11.0 ms | 27.6 ms |
| 12 | 311 ms | 352 ms | 71.5 ms | 1034 ms | 11.9 ms | 20.2 ms |

结论：**保持每批 6 张**。按「优先降低可感知停顿」的要求，6 张把最长单次停顿压在 44.5 ms（与改动前的 ~43 ms 持平），同时布局仍比基线 620 ms 快 40%，分类页比 2732 ms 快 49%。8/12 张整页更快，但单次停顿抬到 59/71.5 ms，超过 50 ms 的可感知门槛。

海报解码没有把数字推高；同库下 `v6` 的滚动 P95 仍为 11.3 ms。卡片数、分页、分类节点与 174 条关系线均未减少（诊断仍报告同样的节点/边数）。

原始数据：`fix7/ev/perf/{v6,v8,v12}-{search,cat,scroll}-{1,2}.json`、`ev/perf/summary.json`。

## 四、发布准备（已实现、部分已实测）

| 项 | 结果 |
| --- | --- |
| 版本号 | `1.5.4` → **`1.6.0`**（`Version` 与 `FileVersion`），更新说明见 `docs/RELEASE_NOTES_1.6.0.md` |
| 发布包 | `dotnet publish -c Release` → 5.6 MB，10 个文件；**不含** `samples/`、诊断工具；仍含 `映格视频库.pdb`（可在打包时删除） |
| 自检 | missing **30/30**、plugins **44/44**、plugin-queue **17/17**，均为最终构建 |
| 真实库副本升级演练 | 复制 `config.json`(17.6 MB) / `library-index.json`(7.4 MB) / `metadata.json` / `review-history.jsonl` 到隔离目录运行 1.6.0：13 个文件夹、**32 909** 条索引、15 730 个分类、4 340 个标签、4 312 条审核、125 条资料记录全部一致，缺失标记 0；运行后真实目录四个文件的 SHA-256 **完全未变** |
| 应用内「数据与备份」恢复 | **未验证**（需要界面操作） |
| 125% / 150% DPI | **未验证**：修改系统缩放需要重新登录，会影响用户其它程序；本轮未做 |
| Tab / Enter / Esc | **未验证**（同上，需前台焦点） |

## 五、未完成与风险

1. 通过/否决、候选写入失败提示、清理日志失败弹窗、应用内备份恢复、DPI、键盘：**未验证**。其中前三个的障碍是「按钮会打开模态窗口 + 应用不在前台时 UIA 阻塞」，不是代码缺陷假设。
2. 审核通过/否决的多窗口流程（逐字段候选 → 预览 → 提交）本身较重，建议单独做一轮可自动化的验收（例如在测试模式下跳过逐字段窗口）。
3. 插件队列只覆盖标题匹配；番号任务仍走内置番号来源。
4. 多插件同时启用时按名称顺序尝试，没有做负载分摊。
5. 单插件承担全部标题任务时，一旦进入 1 小时冷却，队列会停在该来源上直到冷却结束（这是内置来源的既有语义）。
6. `映格视频库.pdb` 仍在发布目录，打包脚本若要精简需显式排除。

## 六、证据索引

- 队列：`fix7/ev/queue-*.json`、`ev/final-queue.json`、`ev/queue-store.json`
- 界面：`fix7/ev/select.json`、`ev/ui-switch.json`、`fix7/shots/review-after-switch.png`、`shots/flow-1-review-row.png`
- 性能：`fix7/ev/perf/*`、`fix7/ev/perf/summary.json`
- 升级演练：`fix7/ev/upgrade-before.json`、`ev/upgrade-scan.json`
- 自检：`fix7/ev/final-{missing,plugins,queue}.json`
- 发布包：`fix7/publish/`
