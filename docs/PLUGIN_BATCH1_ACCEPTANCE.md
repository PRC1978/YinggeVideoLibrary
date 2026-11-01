# 插件平台第一批（搜刮来源）验收记录

分支：`codex/plugin-platform-first-batch`（从 `codex/search-card-performance` 另建工作树，未改动原分支）
接口文档：`docs/PLUGINS.md`

## 已实现范围

| 交付 | 位置 | 说明 |
| --- | --- | --- |
| 清单与版本检查 | `PluginManifest.cs` | `id/version/apiVersion/capabilities/permissions/entry` 逐项校验；损坏、重复 ID、接口版本不兼容、能力未开放、入口越界分别给出原因 |
| 插件目录与状态 | `PluginStateStore.cs` | `<数据目录>\plugins\<id>\plugin.json`；启用状态、权限同意签名、最近错误写在独立的 `plugins.json`，与 `config.json` 隔离 |
| 插件管理页 | `MainWindow.Plugins.cs` + `MainWindow.xaml` | 发现数量、可用/已启用/需处理统计、插件卡片（名称、版本、ID、作者、能力、域名、数据范围、状态徽章、最近错误）、启用/停用、清除错误、重新扫描、打开插件目录 |
| 权限确认 | `MainWindow.Plugins.cs` | 首次启用弹窗列出声明的域名与数据范围；权限集合签名变化后自动停用并要求重新确认 |
| 独立进程协议 | `PluginProtocol.cs` / `PluginProcessHost.cs` | 每次调用一个子进程，stdin/stdout 各一行 JSON，按 `id` 配对；超时、取消、崩溃、超大响应、无响应均有明确错误码 |
| 候选桥接 | `PluginScrapeBridge.cs` | 候选→`MovieMetadata`（`DetailIdentityVerified` 恒为 false）→`ScrapeIdentityPolicy`→`AiReviewRecord`（`status=pending`，`decisionSource=plugin`）→现有审核事务 |
| 示例插件 | `samples/ExampleScrapePlugin/` | 独立 .NET 控制台工程，实现 `describe`/`search`，内置超时、崩溃、超大响应、限流、路径泄露、空结果六种故障路径 |
| 自检 | `PluginDiagnostics.cs` + `--self-test-plugins` | 32 项离线断言，拒绝把工作目录指向真实库或程序目录 |

设置页新增「插件」分区（`MainWindow.SettingsLayout.cs`），点击进入插件管理页；启动页下拉新增「插件」选项。为避免把侧栏挤出默认窗口高度，**没有**在侧栏新增第 10 个导航项。

## 验证

构建：`dotnet build -c Release` — **0 警告 0 错误**；示例插件同样 0/0。

自检：`--self-test-plugins <隔离目录> <结果.json> <示例插件输出目录>` — **32/32 通过，退出码 0**，耗时约 5.9 秒。

关键观测值（`observed`）：

| 指标 | 值 |
| --- | --- |
| `catalogScanMs`（9 个清单含 8 个异常样本） | 130.5 ms |
| `searchElapsedMs`（示例插件冷启动 + 查询） | 348 ms |
| `timeoutElapsedMs`（manifest 改成 2 秒） | 2081 ms |
| `orphanProcessesAfterTimeout` | 0 |
| `callerTicksWhileWaiting` | 30 |

覆盖的场景：

- 清单：空目录、坏 JSON、缺 ID、重复 ID（两个目录同时判不可用）、`apiVersion=2`、`ui.theme` 能力、`../escape.exe` 入口、入口文件缺失、正常插件。
- 状态：启用持久化、停用持久化、权限签名随域名变化、签名不匹配即视为未同意。
- 进程：`describe` 握手、正常搜索 3 条候选、`sourceUrl` 域名校验、`pathleak` 候选被丢弃、`ratelimit` 带 `retryAfterSeconds`、400 KB 单行被拒、`exit 7` 报退出码、超时 2 秒终止且无残留进程。
- 隐私：请求 JSON 不含路径/视频 ID/`path` 字段；日志脱敏去掉用户名路径与长 token。
- 审核门禁：候选记录 `status=pending`、可信度被压到 80 以下、`DetailIdentityVerified=false`、番号完全匹配时仍然 pending、记录里带插件 ID 与版本。

界面：隔离数据目录（120 部合成视频）+ 示例插件，截图见工作区
`2026-10-31-23-02-00\plugin-accept\shots\03-plugins-page.png`（插件页，已启用 1 个）与
`10-settings-entry.png`（设置页）。

## 故障测试

| 场景 | 预期 | 实测 |
| --- | --- | --- |
| 清单损坏 / 重复 ID / 版本不兼容 | 卡片标红给出原因，主程序不受影响 | 通过（自检断言 + 页面渲染） |
| 插件挂起 | 到 `timeoutSeconds` 终止整棵进程树，主界面可继续操作 | 2081 ms 终止，无残留进程 |
| 插件崩溃 | 显示退出码与 stderr 摘要，写入最近错误 | `plugin_exited`，含退出码 7 |
| 返回超大数据 | 丢弃并提示，不解析 | `response_too_large` |
| 返回本地路径 | 丢弃该候选并计数 | 1 条被丢弃 |
| 返回声明外域名链接 | 判为无效候选 | 通过 |
| 未同意权限就启用 | 弹窗列出权限，取消则不变更 | 代码路径已实现；**未做自动化点击验证** |

## 未完成 / 未验证

1. **未接入搜刮页来源下拉与后台队列**：目前只能从插件页对手动选中的视频发起查询；批量队列仍只用内置来源。
2. **未做插件常驻会话**：每次调用冷启动（约 348 ms），批量场景开销明显。
3. **未实现** AI 服务、工具命令、界面主题能力；清单里声明会被标为"本版本尚未开放"。
4. **未做签名/来源校验**，独立进程不等于安全沙箱。
5. **UI 自动化点击未完成**：本机 UIA 在该进程上多次超时，权限确认弹窗的点击流程只做了代码审查；插件页与设置入口有截图佐证渲染正常。
6. **未在多显示器 / 高 DPI / 深色主题下验证**插件页布局。
6.1 验收期间隔离实例多次被**未定位的外部原因**终止（事件日志无 .NET 未处理异常，退出是干净的）；双方脚本都只按 PID 终止自己的实例，未按进程名杀进程。同批实测中用户已安装实例也在此窗口内从窗口列表消失，来源未能定位。凡涉及长时间界面操作的复现，建议单次命令内完成启动与操作。
7. 未运行仓库既有的 `--self-test-features`、`--self-test-anime` 等全量自检（与本次改动无关，且耗时较长）。

## 性能影响

- 启动路径不含任何插件代码：`PluginManifestReader.Scan` 只在打开插件页时调用（`MainWindow.xaml.cs:1531`）。
- 插件页首次打开额外扫描 130 ms（9 个清单，含 8 个异常样本），之后走缓存。
- 单次插件调用 348 ms（冷启动）+ 宿主解析，低于 20 秒默认预算。
- 未安装插件时页面只显示空态文案，不产生文件 IO。

## 复现步骤

```bash
# 1. 构建
dotnet build -c Release
cd samples/ExampleScrapePlugin && dotnet build -c Release && cd ../..

# 2. 离线自检（隔离目录，不读真实库）
bin/Release/net9.0-windows/映格视频库.exe --self-test-plugins ^
  C:\temp\yingge-plugin-test C:\temp\yingge-plugin-test\result.json ^
  samples\ExampleScrapePlugin\bin\Release\net9.0

# 3. 界面验证
bin/Release/net9.0-windows/映格视频库.exe --ui-fixture C:\temp\yingge-ui 120 40
#    写入匹配的 cache-migration-v1.done 后，把示例插件复制到 C:\temp\yingge-ui\plugins\com.yingge.example.scrape
bin/Release/net9.0-windows/映格视频库.exe --data-dir C:\temp\yingge-ui
#    设置 → 插件 → 打开插件管理 → 启用 → 全选视频 → 搜刮当前视频
```
