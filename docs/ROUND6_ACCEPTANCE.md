# 第六轮：插件后台队列与队列级隔离测试

分支 `codex/plugin-queue`，起点 `3f37ef6`。
全部测试 `--data-dir` 隔离；未安装、未推送、未发布；未改动其他分支。

## 一、插件后台队列（已实现、已实测）

### 实现

| 项 | 做法 |
| --- | --- |
| 明确开关 | 新增 `config.pluginQueueEnabled`（默认 **关**），搜刮页「批量搜刮条件」上方增加复选框「允许已启用的搜刮插件参与后台队列（标题匹配）」，改动即保存 |
| 只让健康插件参与 | 复用 `ScrapeSourcePlugins`：仅 `Enabled && Status == Ready`；多个插件时按名称取第一个 |
| 队列调度接入 | `EnabledQueueTitleSources()` 追加合成来源 `plugin:<id>`；队列的 `PeekNextFair` 因此允许标题任务调度，来源不可用时仍按 `hasBlocked` 处理 |
| 每来源限速 | 沿用 `ScrapeSourcePolicy` 与 `scrape-source-backoff.json`；插件走同一套 `NextAttemptAt` / `ScrapeSourceUnavailableException` 冷却 |
| 候选缓存 | 插件候选仍写 `plugin-candidates.json`，进审核；不写 `metadata.json` |
| 暂停 / 取消 | 复用现有 `scrapeQueuePaused`、`CancellationToken`、`ClearPending`；插件调用收到取消令牌会终止子进程 |
| 重启恢复 | 队列条目本来就在 `scrape-queue.json`；重启后 `PollScrapeQueueAsync` 会先 `EnsurePluginCatalog()` 再派发 |
| 失败隔离 | 插件超时/崩溃/无结果只记录错误；有内置标题来源时继续走内置，没有则抛 `ScrapeSourceUnavailableException`（保留任务、不消耗重试次数、**不写「无匹配」**） |

### 实测（离屏真实窗口跑真实队列循环，`--diagnose-plugin-queue`）

成功路径（`ev/queue-success2.json`）：

```json
{"pluginQueueEnabled": true, "activePlugin": "com.yingge.example.scrape",
 "video": "demo-2", "pendingAfterRun": 0, "candidateCount": 3,
 "reviewStatus": "pending", "reviewDecisionSource": "plugin",
 "unmatchedRecords": 0, "elapsedMs": 4037, "passed": true}
```

插件无结果 + 未启用任何内置标题来源（`ev/queue-fail4.json`）：

```json
{"pendingAfterRun": 1, "lastError": "插件 示例搜刮来源 暂时没有可用结果，且没有启用其他标题来源；任务已保留。",
 "candidateCount": 0, "reviewStatus": "", "unmatchedRecords": 0, "elapsedMs": 30826, "passed": true}
```

即：任务保留、没有被标记为失败或「无匹配」、没有生成审核记录。

队列级自检 `--self-test-plugin-queue` **10/10**（`ev/queue.json`）：来源键只在开启时追加、重复入队被忽略、重启后条目仍在、无来源时任务阻塞、加入插件来源后可调度、重新调度保留任务、来源级失败保留重试预算、取消清空待处理、取消保留失败记录。

### 未实测

- 多插件同时启用时只取第一个，未做多插件轮转。
- 暂停/取消在**插件执行中**的表现没有端到端计时（只有代码路径与 store 级测试）。
- 队列里的插件候选进入多候选人工审核后切换候选，仍未点击验证。

## 二、真实交互验收（未完成）

本机自动化仍不稳定：`uia-invoke` 多次超时，真实鼠标点击的窗口坐标换算不可靠（点击后截图与点击前逐字节相同，说明没有落在控件上）。因此：

- 候选 1/3 → 2/3 切换、通过/否决点击：**未实测**（读取侧上一轮已实测「候选 2/3」显示）。
- 插件候选写入失败提示、清理日志写入失败弹窗：**未实测**。

没有用代码检查代替实测，这些项目在验收表里保持未验证。

## 三、发布前检查（未完成）

含真实海报的数据、应用内备份恢复、125%/150% DPI、Tab/Enter/Esc、真实库只读副本升级演练、版本号与更新说明：**均未做**（版本仍 1.5.4）。

## 四、过程中发现并修复的两个配置缺陷

1. `CloneLibraryConfig` / 配置合并漏掉了新字段，插件队列开关在保存后会被抹成 false。已补 `PluginQueueEnabled` 到克隆与 `MergeScalar`，实测保存后仍为 true。
2. 测试夹具的迁移标记会因应用自身保存配置而失效，第二次启动可能触发旧缓存迁移。本轮实测中隔离库的索引仍指向测试目录（未被真实库覆盖），但这是夹具层面的坑，后续脚本应在每次启动前重写标记。

## 五、证据

- `fix6/ev/queue-success2.json`、`queue-fail4.json`、`queue.json`（自检）、`plugins.json`、`missing.json`
- 自检：missing 30/30、plugins 44/44、plugin-queue 10/10

## 六、剩余风险

1. 插件队列只覆盖标题匹配；番号路径仍走内置番号来源。
2. 插件失败后无内置来源时任务会以 2 分钟间隔反复重试，直到用户暂停或取消；没有上限提示。
3. 开关默认关闭，升级不会自动产生外部调用；但打开后第一个插件会承担全部标题任务。
4. 真实交互与发布准备完全未完成，不能进入发布流程。
