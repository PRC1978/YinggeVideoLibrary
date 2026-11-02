# 第八轮：1.6.0 发布前缺口验收

分支 `codex/release-1.6.0-verify`，起点 `c3cddf6`（`codex/queue-rate-limit`）。
所有运行都使用 `--data-dir` 指向 `fix8/iso/*` 隔离目录；没有读取或改写真实视频库，没有安装、推送、打标签或发布。

判定口径：**实测通过** = 有界面操作与原始结果；**仅代码核对** = 只读过代码；**未验证** = 环境不具备或没做成。

## 一、结论

| 发布前缺口 | 结果 |
| --- | --- |
| 插件来源的手动完整资料搜刮（真实点击） | **实测通过**：候选入审核、来源与可信度正确；无候选/崩溃都不写「无匹配」 |
| 候选写入失败、审核入队失败的界面提示 | **实测通过**：两种失败分别给出准确文案，数据没有被误报 |
| 清理日志写入失败提示（复核） | **实测通过**：提示「索引没有任何变化」，索引确实未变 |
| 应用内「数据与备份」备份 + 恢复 | **实测通过**：备份 5 个受管文件；恢复后 config / 索引 / 资料 / 审核历史与备份逐字节一致 |
| Tab / Enter / Esc | **实测通过**：焦点链、回车提交搜索、回车触发按钮、Esc 清空搜索均读回验证 |
| 125% / 150% 缩放 | **未验证**：本机两块显示器均为 100%，改缩放需要重新登录并影响其它程序 |
| Release 构建、四项自检、版本与发布包 | **通过**：features `passed=true`、missing 30/30、plugins 44/44、plugin-queue 17/17；FileVersion 1.6.0.0 |

**1.6.0 目前仍不满足发布清单**，唯一未勾选项是高 DPI（125%/150%）实测。其余原「未验证」项已补齐。

## 二、插件来源的手动完整资料搜刮（实测）

隔离库：12 部索引视频、40 个分类叶子、示例插件已启用（`fix8/iso/base` 的克隆）。
操作全部通过 UI Automation 模式完成，没有注入鼠标；窗口移到副屏，未抢占前台。

### 2.1 成功路径

1. 启动 → 左侧「搜刮」→「默认完整资料搜刮来源」下拉。
2. 选择「插件：示例搜刮来源」（读回 `selected=插件：示例搜刮来源`）。
3. 点「完整资料搜刮」→「选择要搜刮的视频」（12 项）→ 选中第 1 项 →「读取完整资料」。
4. 标题窗口保留建议标题 →「确定」。

界面结果（`ScrapeSourceResultText`，本轮新增的可见行）：

> 该视频现有 3 条候选，已加入人工审核队列（508 ms）。原始资料没有被修改。

审核区汇总：

> 搜刮记录 1 条 · 待人工 1 条 · 未找到 0 条 · 低可信度 1 条 · 规则否决 0 条 · AI 否决 0 条 · 人工否决 0 条

审核行读回：

- 可信度 `55% / 待人工`
- 标题「夜行城市 Night City 2024 第00000号 示例作品 1」
- 来源行「… · 插件：示例搜刮来源 · EX-001」
- 候选下拉「候选 1/3 · 插件：示例搜刮来源 · EX-001」

`config.json` 的审核记录：

| 字段 | 值 |
| --- | --- |
| `status` | `pending` |
| `decisionSource` | `plugin` |
| `candidateSource` | `插件：示例搜刮来源` |
| `confidence` | `55` |
| `model` | `com.yingge.example.scrape v1.0.0` |
| `reason` | 含插件 id、依据与「插件候选一律进入人工审核」 |

`plugin-candidates.json` 保存 3 条候选；`metadata.json` 未变；视频资料没有被写入。

### 2.2 无候选

插件返回 0 条候选。界面：

> 插件在 537 ms 内没有找到候选，视频状态未改变。

结果：审核记录 0、`unmatched` 0、标签未增加「素人」、资料条目仍 12、候选文件未创建。

### 2.3 插件崩溃

插件进程以退出码 7 结束。界面弹窗与结果行：

> 插件调用失败：插件未返回结果就退出（退出码 7）：simulated crash（视频状态未改变）

结果同 2.2：没有审核记录、没有「无匹配」、没有素人标签，插件日志记录 `plugin_exited`。

**结论**：插件失败不会被记成「无匹配」，与队列路径的语义一致。

## 三、写入失败提示（实测）

| 场景 | 制造方式 | 界面原文 | 数据结果 |
| --- | --- | --- | --- |
| 候选文件无法写入 | `plugin-candidates.json` 只读 | **候选未能保存：候选文件无法写入** | 候选 0 条、审核 0 条、无「无匹配」 |
| 候选已保存、审核入队失败 | 提交审核时把 `config.json` 设为只读 | **已保存 3 条候选，但审核记录写入失败；可稍后重试。** | 候选 3 条（1 个视频）、审核 0 条、资料未变 |
| 清理日志无法写入（复核） | `missing-cleanup.json` 只读 | **清理日志无法写入（数据目录或插件状态目录可能只读），索引没有任何变化。** | 索引 6 条未变、缺失 1 条仍在、被删文件记录仍在 |

两种候选失败都没有出现「界面成功、磁盘没变」的旧问题；文案与磁盘状态一致。

## 四、应用内备份与恢复（实测）

隔离库：20 部视频 + 真实占位文件、30 个分类叶子、1 条插件审核记录、`review-history.jsonl`。

1. 设置 → 展开「数据与备份」→「下次启动一致性备份」。
   界面：「一致性备份已排定。关闭所有映格窗口并重新打开后，会在载入资料前创建并校验备份。」
2. 重启：启动前完成备份。生成 `backups/20261102-095844-3151ca42`，清单 5 个文件：
   `config.json`、`library-index.json`、`metadata.json`、`review-history.jsonl`、`scrape-queue.json`，校验通过。
3. 制造漂移（应用关闭时）：索引 20→19、分类 20→19、标签 20→19、审核 1→0、资料 20→19。
4. 设置 →「恢复最近备份」→ 预览：
   - 视频索引 20 → 20
   - 受管文件：当前 4 个，备份 5 个；将覆盖 4 个、加入 1 个
   -「只影响受管资料，视频原文件不受影响」
5. 确认「是(Y)」→ 重启：恢复在载入资料前完成，没有出现「恢复已停止」弹窗。

恢复后对比：

| 文件 | 与备份 SHA-256 |
| --- | --- |
| `config.json` | **一致**（`4F71E7C4…`） |
| `library-index.json` | **一致**（`41C1B885…`） |
| `metadata.json` | **一致**（`5DFE7039…`） |
| `review-history.jsonl` | **一致**（`F08D7EB6…`） |

结构计数恢复为：索引 20、分类 20、标签 38 项（20 个视频）、审核 1（pending）、资料 20。
备份目录同时保留 `.rollback-20261102-100019-b630370d` 回滚包；`backup-last-result.json` 记录「一致性备份已完成并校验」。

## 五、Tab / Enter / Esc（实测）

按键通过窗口消息投递，焦点通过 UI Automation 设置并读回；没有全局键盘钩子，没有移动鼠标。

| 操作 | 读回 |
| --- | --- |
| 焦点搜索框后按 Tab | `SearchBox` → `SortBox` → `＋ 添加文件夹` → `↻`（连续三次，均读回控件 id/名称） |
| 搜索框输入 `Night City` 后按 Enter | `config.json` 的 `searchHistory` 写入 `["Night City"]`（只有 Enter 分支会写），结果 2 部 |
| 搜索框按 Esc | 搜索框值变为空，计数回到「12 部视频」 |
| 焦点「完整资料搜刮」后按 Enter | 打开「选择要搜刮的视频」，列出 12 项 |

## 六、高 DPI（未验证）

`GetDpiForWindow` = 96（100%）；两块显示器（2560×1440、1080×1920）的 `PerMonitorSettings` 均为推荐值 0。
修改缩放需要注销/重新登录，会影响用户其它程序，本轮没有做。**不能用代码检查代替，保持未验证。**

## 七、本轮修复的缺陷

| # | 现象 | 根因 | 修复 |
| --- | --- | --- | --- |
| 1 | 启动后直接进「搜刮」，来源下拉没有「插件：…」 | `ShowView` 里有两个 `scrape` 分支，后一个（加载插件目录）永远不执行 | 合并分支，进入搜刮页时 `EnsurePluginCatalog()` + `RefreshScrapeSourcePlugins()`（`MainWindow.xaml.cs`） |
| 2 | 插件手动搜刮的结果只写进「状态」页的隐藏文本，搜刮页看不到 | `TaskStatusText` 位于状态页 | 在来源下拉下新增 `ScrapeSourceResultText`，成功/失败/异常都写入（`MainWindow.xaml`、`MainWindow.xaml.cs`） |
| 3 | **候选能保存，审核永远入不了队**：提示「审核预览已过期」 | 配置加载会把标签去重/去空白，但审核事务比较的是磁盘原始标签；只要 `config.json` 有重复标签就必然不一致 | 新增 `TagRules`，磁盘快照按同一规则归一化后再比较（`TagRules.cs`、`ReviewTransactionStore.Recovery.cs`） |
| 4 | 上述错误的提示不说明是审核、资料还是标签不一致 | 固定文案 | 按实际差异输出（如「磁盘中的标签已改变」），仍不写入任何内容（`ReviewTransactionStore.Recovery.cs`） |
| 5 | 设置页折叠卡片没有自动化标识，键盘/读屏/自动化无法定位 | `Expander` 头部是组合文本，Name 为空 | 每个分区设置 `SettingsSection_<key>`（`MainWindow.SettingsLayout.cs`） |

回归：为 #3 增加自检 `reviewDuplicateTagsDoNotBlock`（磁盘保留重复/带空格标签，内存为归一化结果，事务必须能提交并恢复）。

## 八、构建、自检与发布包

| 项 | 结果 |
| --- | --- |
| Release 构建 | 成功，0 警告 0 错误 |
| `--self-test-features` | `passed=true`；633 个顶层布尔项中 629 为 true，4 个非断言项为 false（未重定向数据目录、未复制真实库、未读取真实库、临时目录清理成功） |
| `--self-test-missing-files` | **30/30** |
| `--self-test-plugins` | **44/44** |
| `--self-test-plugin-queue` | **17/17** |
| 发布包 | 14 个文件、5.6 MB；不含 `samples/`、诊断工具；含 `映格视频库.pdb` |
| 版本 | `FileVersion 1.6.0.0`，`ProductVersion 1.6.0+<提交>`（提交后重建） |
| 更新说明 | `docs/RELEASE_NOTES_1.6.0.md` 已补本轮修复与验收状态 |

## 九、证据索引

- 插件搜刮：`fix8/ev/scrape-{source,success,empty,crash,candidate-fail,enqueue-fail}.json`
- 插件搜刮截图：`fix8/shots/scrape-plugin-{success,empty,crash}.png`、`scrape-source-{combo,selected}.png`、`scrape-{candidate,enqueue}-fail.png`
- 清理：`fix8/ev/cleanup-failure.json`、`fix8/shots/cleanup-{status,preview,warning}.png`
- 备份/恢复：`fix8/ev/backup-{mutate,backup-schedule,backup-apply,restore-schedule,restore-apply}.json`、`fix8/shots/{backup-settings,backup-scheduled,restore-preview,restore-scheduled}.png`
- 键盘/DPI：`fix8/ev/keyboard.json`、`fix8/shots/keyboard-{tab,escape,enter}.png`
- 自检：`fix8/selftest/{features,missing,plugins,queue}.json`
- 发布包：`fix8/publish/`

### 复现命令

```bash
# 隔离库（不要指向真实库）
映格视频库.exe --ui-fixture <fix8/iso/base> 12 40
python tools/prepare-iso.py <fix8/iso/base> <示例插件输出目录>

python tools/release_scrape_ui.py success <克隆目录>
python tools/release_scrape_ui.py empty|crash|candidate-fail|enqueue-fail <克隆目录>
python tools/release_cleanup_ui.py <含真实文件的隔离库>
python tools/release_backup_ui.py backup-schedule|backup-apply|mutate|restore-schedule|restore-apply <隔离库>
python tools/release_keyboard.py <克隆目录>

映格视频库.exe --self-test-features <out.json>
映格视频库.exe --self-test-missing-files <dir> <out.json>
映格视频库.exe --self-test-plugins <dir> <out.json> <sampleDir>
映格视频库.exe --self-test-plugin-queue <dir> <out.json>
```

## 十、仍未验证 / 发布阻断

1. **125% / 150% 缩放（发布阻断）**：环境只有 100%，需要改系统缩放并重新登录。
2. 真实照片/录音的识别准确率：本轮继续未验证（只用合成隔离库）。
3. 备份恢复只在 20 部视频的隔离库验证；真实库 32 909 条的完整恢复仍需在副本上做一次（上一轮只做了升级读取演练）。

## 十一、独立复核补充

复核 `99e96ea` 时发现：配置加载通过 `ParseTags` 拆分单个标签字符串中的逗号，但审核磁盘快照新增的 `TagRules.Normalize` 最初只处理标签列表，未拆分列表元素中的逗号。旧配置若含 `"旅行, 科幻"` 这样的单项标签，仍可能误报预览过期。现已让两条路径使用相同分隔规则，并把回归样本改成重复标签加嵌入逗号，显式断言与配置加载结果一致。补丁后 Release 构建 0 警告、0 错误；从构建后的 EXE 运行 `--self-test-features` 得 `passed=true`、`reviewDuplicateTagsDoNotBlock=true`、`realLibraryRead=false`。此补充不改变高 DPI 和大库备份恢复的未验证状态。
