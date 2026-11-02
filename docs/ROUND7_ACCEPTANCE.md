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

## 二、界面验收（真机，UI Automation 驱动）

### 工具侧查清的四件事

1. `shot` 把整窗渲染进客户区大小的位图，**每张截图纵向偏 31 px**（标题栏高度）。已改为按窗口矩形渲染再裁客户区。
2. 点击辅助把客户区坐标当屏幕坐标校验，只有窗口在 (0,0) 时才通过。已修正。
3. `AutomationElement.BoundingRectangle` 对页面 `ScrollViewer` 内的控件返回**滚动内容坐标**（下拉框报 y=5907，实际 y≈727），不能用来定位。
4. 模态窗口打开时，**整棵桌面树的枚举会阻塞**；弹窗必须用 `EnumWindows` 拿 HWND 再 `FromElement`。中文 MessageBox 的按钮名是「是(Y)/否(N)」。

### 交互方式

前台曾长期被全屏游戏占用，抢鼠标会直接影响用户。因此本轮**没有注入任何鼠标或键盘事件**，全部用 UI Automation 的模式调用（`Invoke` / `SelectionItem` / `ExpandCollapse`），窗口只做 `SetWindowPos` 移到副屏，不抢前台。

### 结果

| 项 | 结果 | 证据 |
| --- | --- | --- |
| 候选 1/3 → 2/3 | **通过**：`uia-select-item` 展开下拉并选中第 2 项，控件 `SelectionPattern` 变为 `候选 2/3 · 示例来源 · EX-002`，`config.json` 的 `CandidateCode` 同步为 `EX-002`，状态仍 `pending` | `ev/ui-switch.json`、`shots/rm-switch-after.png` |
| 重启后保持 2/3 | **通过**：新进程读回 `selected=候选 2/3`（上一轮已实测） | `ev/ui-switch.json` |
| 通过 | **通过**：行内「通过」→「逐字段审核候选」窗口（读到 20 个字段的"原值/候选/写入后"）→「预览实际写入」→ 预览框"将通过 1 条候选。实际改变 2 个字段…"→「是(Y)」→ `status=approved`、`ReviewedAt` 写入、`metadata.json` 出现 `Code=EX-001 / Title=示例作品 1 / Source=示例来源` | `ev/ui-approve.json`、`shots/rm-approve-fields.png`、`rm-approve-preview.png`、`rm-approve-done.png` |
| 否决 | **通过**：「否决」→ 预览框"将否决 1 条候选。不会修改任何已保存的视频资料或标签。"→「是(Y)」→ `status=rejected`，另一条仍 `pending` | `ev/ui-reject.json`、`shots/rm-reject-preview.png`、`rm-reject-done.png` |
| 清理日志写入失败 | **通过**：状态页「预览并清理不在本地记录…」→ 预览"将清除 1 条索引记录，涉及 1 个位置…只删除索引记录，不会删除任何视频文件"→ 确定 → 警告"清理日志无法写入（数据目录或插件状态目录可能只读），索引没有任何变化。"→ 索引仍 4 条、1 条缺失 | `ev/ui-cleanup.json`、`shots/rm-cleanup-preview.png`、`rm-cleanup-warning.png` |
| 插件候选写入失败提示 | **未验证**：该提示只会在"已选中视频"时出现，而插件页要经设置页进入；这台机器上设置页的全树 UIA 查找超过 150 s 仍在阻塞，无法在不抢前台的前提下走到 | — |

### 真机测试额外发现并修复的缺陷

1. **状态页的"本地文件检查"卡片不刷新**：卡片文字与清理按钮只在卡片渲染（`RenderCards`）时更新，直接进「状态」页会显示"所有索引记录都能在磁盘上找到对应文件"且按钮禁用，即使确有缺失文件。已改为进入状态页时刷新，并在真机复测（按钮变为可用，预览与警告按预期出现）。
2. **行内按钮的自动化标识与批量按钮重名**：`ReviewApproveButton` / `ReviewRejectButton` 在 XAML 里已用于「通过选中 / 否决选中」，行内按钮再设同名标识会让自动化拿到禁用的批量按钮。已改为 `ReviewRowApprove` / `ReviewRowReject` / `ReviewRowInfo`。
3. **一次中断的审核提交会阻塞后续提交**：上一次提交被终止后留下 `review-transaction-pending.json`，再次提交会弹"审核数据写入后与预览不一致…审核元数据已被其他操作修改"。重启后重新提交成功。当前行为是提示用户重启，属于已知语义，未改。

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

1. **仍未验证**：插件候选写入失败提示（插件页需先选中视频，设置页的全树 UIA 查找在本机阻塞）、应用内「数据与备份」恢复、125%/150% DPI、Tab/Enter/Esc。
2. 审核通过/否决要经过「逐字段候选 → 预览 → MessageBox」两级弹窗，交互偏重；本轮已用 UIA 走通，但用户每次通过都要点两次确认，值得单独评估是否合并。
3. 中断的审核提交会留下 `review-transaction-pending.json`，下一次提交被拒绝并提示重启；提示明确，但恢复需要重启这一步可以考虑做成自动重试。
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
