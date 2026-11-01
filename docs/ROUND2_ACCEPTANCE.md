# 第二轮验收：缺失文件标记 / 清理、UI 修复、插件修正

分支：`codex/missing-files-ui-plugins`（从 `codex/plugin-platform-first-batch` @ `4e5cdd9` 另建工作树）
基线对照工作树：`2026-11-01-yingge-baseline`（detached @ `4e5cdd9`，未改动）

所有运行均使用 `--data-dir <隔离目录>`；未读取真实视频库，未安装、未推送、未发布。

## 一、缺失文件标记与一键清理

| 要求 | 实现 | 证据 | 状态 |
| --- | --- | --- | --- |
| 扫描后给文件不存在的视频显示「不在本地」 | 扫描把「未找到」的旧索引项保留并标记 `Missing`；卡片明细行追加 `· 不在本地` | `shots/after/1240-missing-all.png`（clip-2 卡片） | 已实现 |
| 提供筛选 | 标题行右侧「不在本地 N」按钮，点击只显示该类；再点取消 | 同上；`MainWindow.MissingFiles.cs` | 已实现 |
| 区分丢失与扫描根不可访问 | 根目录读不到 → `RootUnavailable`，卡片显示「位置暂不可用」，**不计入丢失** | 自检 `unavailableRootIsNotMissing`、`unavailableRootKeepsRows`、`unavailableIsNotCleanupCandidate` | 已实现 |
| 预览数量与路径范围后确认 | 弹窗列出条数、按位置分组、3 条示例路径、明确「不删除任何文件」 | `shots/after/cleanup-dialog.png` | 已实现（实点） |
| 只清索引，不删文件 | 只改内存列表与 `library-index.json`，无任何文件删除调用 | 自检 `cleanupNeverDeletesVideoFiles`；清理后 6→4 条，磁盘文件仍在 | 已实现 |
| 不可访问目录不进清理范围 | 清理目标固定为 `IsMissing`，`RootUnavailable` 不参与 | 自检 `unavailableIsNotCleanupCandidate` | 已实现 |
| 清理有历史、可撤销 | `missing-cleanup.json` 记录路径/大小/时间；状态页「撤销上次清理」 | `shots/after/cleanup-after.png`、`undo-after.png`；索引 4→6 | 已实现（实点） |
| 重启与备份恢复后状态正确 | `IndexEntry.Missing` 随索引持久化，重启即显示；扫描重算 | 端到端 phase1–4 | 已实现 |
| 清理中断可恢复 | 先写 pending 再改索引，启动时回滚 | 端到端 phase5（6 条恢复、pending 清空、rolledBack=true） | 已实现 |

端到端原始结果：`fix2/it/ev/integration.json`

```json
{"phase1":{"entries":6,"missing":0},
 "phase2":{"entries":6,"missing":2,"missingPaths":["clip-1.mp4","clip-2.mp4"]},
 "phase3":{"entries":6,"missing":2,"indexUntouched":true},
 "phase4":{"entries":6,"missing":2},
 "phase5":{"entries":6,"missing":2,"pendingCleared":true,"rolledBack":true}}
```

自检：`--self-test-missing-files` **21/21 通过**（`fix2/ev/missing-files.json`）。

## 二、UI 与性能

| 报告项 | 处理 | 证据 | 状态 |
| --- | --- | --- | --- |
| S1-1 800×550 搜刮页按钮被裁 | 队列按钮行由右靠 `DockPanel` 改为可换行 `WrapPanel`，说明文字移到按钮下方 | `shots/after/800x550-scrape-all.png`（三个按钮完整可见） | 已修复（实测） |
| S1-2 窄窗口搜索框被裁 | `Toolbar` 改 `WrapPanel`，搜索框完整显示图标与占位文字 | `shots/after/800x550-final-all.png`、`800x550-all.png` | 已修复（实测） |
| 侧栏矮窗口滚动条 | 自定义 6px 无箭头滚动条 | `800x550-final-all.png` 侧栏 | 已改（实测可见样式变化） |
| 切页保留旧滚动位置 | `ShowView` 仅在页面真正变化时 `ScrollToTop()` | 代码 + 启动页截图不再停在页中 | 已修复（代码 + 实测） |
| 三处输入框无占位 | 全局搜索、设置搜索、文件夹搜索加占位提示 | `800x550-final-all.png`（搜索视频） | 已修复（实测） |
| 标题重复 | 设置/搜刮页清空小节标题；搜刮卡片标题改「来源与凭据」 | `800x550-scrape-all.png`（只有「搜刮」一个标题） | 已修复（实测） |
| 页面宽度不一致 | 搜刮页/插件页去掉固定 680 宽，改为 `MaxWidth` 拉伸 | `800x550-scrape-all.png` 不再裁切 | 已修复（实测） |
| S2-1 搜索整表重排 | 未改善，见下表 | `ev/ab/*.json` | **未解决** |
| S2-2 分类页 60 卡到位 | 未改动；交替复测与基线一致 | `ev/ab/cat-*.json` | 未处理（无回归） |
| S2-3 滚动 P99 | 未改动；交替复测一致 | `ev/ab/scroll-*.json` | 未处理（无回归） |

### 同口径性能（交替复测，同一隔离库 600 部）

搜索（`--diagnose-ui-search`，3 轮中位数，单位 ms，格式 改前/改后）：

| 查询 | 命中 | 布局 P50 | 布局最大 | 异步单次 UI 停顿 |
| --- | --- | --- | --- | --- |
| 夜行城市 | 100 | 608.8 / 778.2 | 626.4 / 1487.2 | 45.4 / 38.9 |
| Morning Light | 100 | 561.2 / 756.6 | 627.7 / 766.7 | 46.2 / 43.9 |
| ABC-10042 | 209 | 585.1 / 767.0 | 637.1 / 795.7 | 41.6 / 43.6 |
| 标签:旅行 状态:未看 | 20 | 126.0 / 268.7 | 209.0 / 298.9 | 35.3 / 41.7 |
| 分类:电影 状态:未看 | 50 | 442.2 / 642.3 | 522.4 / 663.2 | 43.1 / 34.7 |
| nigt city 2024 | 200 | 587.7 / 735.1 | 638.7 / 810.8 | 36.6 / 36.4 |
| qzxv无匹配词 | 0 | 0.2 / 0.2 | 0.5 / 0.5 | 12.8 / 0.0 |

- 零命中路径完全一致（0.2 ms），说明筛选与计数未引入固定开销。
- 命中路径改后普遍高 5–20%，但同版本的单次最大值为 1487 ms、约为中位数的 2 倍，机器自身波动大于该差值；分类页交替复测出现同样的离群（改后第 1 轮 2768 ms，第 2 轮 1581 ms，基线 1613–1714 ms）。**结论：本次改动没有改善搜索布局耗时，也没有稳定证据表明它显著恶化；S2-1 仍未解决。**
- 分类页：首次构建 229/234（改前）vs 441/225（改后），60 卡到位 1714/1613 vs 2768/1581；标签切换 102/100 vs 170/102。
- 滚动：UI 片段 P95 9.8–10.1 vs 9.8–10.5 ms，P99 11.5–12.9 vs 11.0–11.9 ms，最慢 32.2–33.6 vs 31.8–40.6 ms。

功能保留：分页、全选、分类图 296 个节点与 174 条关系线在离屏诊断中仍然通过（`graph verified` 记录）。

## 三、插件修正

| 要求 | 实现 | 证据 | 状态 |
| --- | --- | --- | --- |
| 多候选不互相覆盖 | 新增 `plugin-candidates.json`，按视频保存全部候选（去重、上限 8）；审核记录只指向其中一条 | 自检 `candidateStoreKeepsAll`、`candidateStoreDedupes`、`candidateStoreSurvivesReload` | 已实现 |
| 审核界面可保留并选择全部候选 | 审核行出现「候选 i/N」下拉框，切换改写待审核记录 | 代码 + 数据构造；**未截到下拉框画面**（长页面滚动未成功） | 代码可见，未截图 |
| 入队数量与实际保存一致、重启一致 | 返回 `PluginCandidateStore` 的条数而非循环次数；提示改为「该视频现有 N 条候选」 | 同上自检 | 已实现 |
| 网络权限边界说明 | 启用弹窗与插件页说明改为「只用于声明与链接校验，不是网络隔离」；文档新增边界小节 | `docs/PLUGINS.md`「网络权限的真实边界」 | 已改 |
| 真正的字节/字符上限 | 新增逐字符读取器，达到 256 KB 立即停止并终止进程 | 自检 `boundedReaderStopsAtCap`：8 M 字符输入只读取 262 145 个 | 已实现 |
| 持续超大输出 | 示例插件新增 `stream` 模式（8 MB 无换行） | 自检 `streamingResponseIsCutOff` 294 ms、无残留进程 | 已实现 |
| 启停先保存再改界面 | 克隆状态 → 写盘成功后才替换内存与界面；失败弹窗且状态不变 | 自检 `stateSaveFailsWhenReadOnly`（只读文件写入失败、内容不变） | 已实现 |
| 保留既有测试 | 崩溃/超时/权限变化/身份核对/人工审核门禁断言全部保留 | `fix2/ev/plugins-after.json` **38/38 通过** | 已实现 |
| 不要写成已接入搜刮下拉/队列 | 文档与报告均标注未接入 | `docs/PLUGINS.md` 已知限制第 3 条 | 已遵守 |

## 四、截图与原始数据位置

- 截图：`2026-10-31-23-02-00\fix2\shots\after\`（`800x550-final-all.png`、`800x550-scrape-all.png`、`1240-missing-all.png`、`1240x780-status-StatusNav.png`、`cleanup-dialog.png`、`cleanup-after.png`、`undo-after.png`、`1600x1000-final-all.png`）
- 基线截图：`fix2\shots\before\800x550-all.png`
- 性能原始 JSON：`fix2\ev\ab\`（search/cat/scroll 各 3/2 轮，before 与 after 分列）
- 自检：`fix2\ev\missing-files.json`、`fix2\ev\plugins-after.json`、`fix2\it\ev\integration.json`

## 五、已知限制与未完成

1. **S2-1 搜索重排未改善**：300–700 ms 的整表布局仍在，本次未动卡片容器与分页策略。
2. **审核行的候选下拉框没有截图**：页面过长、滚动截图未成功；只有代码与数据层验证。
3. **侧栏在 550 高仍需滚动**才能到达「搜刮/在线」，只是滚动条样式改细、可通过滚轮滚动；未做折叠或分组。
4. **未验证**：高 DPI、深色主题、多显示器、键盘可达性、备份恢复后的缺失状态（只验证了索引持久化与重启）。
5. 清理只处理本地索引记录；`config` 里指向已删视频的分类/标签/收藏条目不清除（避免误删元数据），属于有意保留。
6. 插件仍未接入普通搜刮来源下拉与后台队列；每次调用仍冷启动子进程。
7. 网络权限仍不是隔离；若要真正限制联网需要额外的系统级机制。

## 六、复现步骤

```bash
dotnet build -c Release

# 缺失文件自检
bin/Release/net9.0-windows/映格视频库.exe --self-test-missing-files ^
  C:\temp\yingge-missing C:\temp\yingge-missing\result.json

# 插件自检（需先构建示例插件）
cd samples/ExampleScrapePlugin && dotnet build -c Release && cd ../..
bin/Release/net9.0-windows/映格视频库.exe --self-test-plugins ^
  C:\temp\yingge-plugins C:\temp\yingge-plugins\result.json ^
  samples\ExampleScrapePlugin\bin\Release\net9.0

# 界面：见 docs/PLUGIN_BATCH1_ACCEPTANCE.md 的隔离库准备；状态页可预览并清理、撤销
```
