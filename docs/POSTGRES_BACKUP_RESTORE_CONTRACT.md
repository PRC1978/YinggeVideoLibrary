# 数据库模式备份 / 恢复接口契约（供主线集成）

分支：`codex/postgresql-backup-acceptance`，基线 `5d3384a`（`codex/desktop-cloud-integration`）。
本分支不修改 `App.xaml.cs`、`MainWindow.xaml.cs`，也不改数据库运行时入口；下面列出主线需要接入的调用点与不变量。

## 1. 当前边界（必须如实对外表述）

- 普通启动仍然只读写文件（JSON）。`storage-postgresql.active` 不会由正常启动流程写出。
- 本分支新增的备份 / 恢复 / 激活 API 目前只被隔离开关探针调用，**没有**接入设置页。
- 设置页现有的「下次启动一致性备份 / 恢复最近备份 / 选择性导入备份」仍是文件模式实现，数据库模式下不可直接复用（见 UX 审核文档）。
- 云端账号服务与本机媒体库无关，本分支不部署、不调用云端。

## 2. 新增 API

### 2.1 `PostgresLibraryBackup`

```csharp
Task<Info> CreateAsync(string connectionString, string dataDirectory, string destination,
    CancellationToken cancellationToken = default);

Task<Info> CreateLockedAsync(string connectionString, string dataDirectory, string destination,
    CancellationToken cancellationToken = default);   // 调用方已持有排他会话租约
```

- `dataDirectory`：视频库数据目录，必须已存在；图片取自 `posters/`、`thumbs/`。
- `destination`：必须不存在。成功时目录一次性出现；失败时不留下最终目录，`.partial-*` 也会被清理。
- 一致性：备份事务持有 `pg_advisory_lock(178942601)`，与所有现有数据库写入器（它们在同一键上取 `pg_advisory_xact_lock`）互斥；同时持有 `.library-session.lock` 排他租约，阻止文件模式应用运行。**图片复制发生在事务提交之前**，因此行与图片字节属于同一版本。
- 空间：写入前按 `EstimateSnapshotBytesAsync`（含 25% 余量与 8 MB 固定开销）与卷可用空间比较，不足直接 `IOException`，不创建任何目录。
- 内容：`library-index.json`、`metadata.json`、允许清单内的资料文档（含 `title-candidates/`、`ai-jobs/`、`role-profiles.json`、`plugins.json`、队列快照/日志）、`scrape-queue.json`、可选 `posters/` 与 `thumbs/`，外加 `database-export-manifest.json`（逐文件长度 + SHA-256）。
- 发布前会重新读取并校验清单；校验失败按失败处理。

`Info` 返回清单 SHA-256、视频/资料/文档计数、字节数与创建时间；激活标记只记录清单哈希，不复制清单内容。

### 2.2 `PostgresLibraryRestore`

```csharp
Task<Result> RestoreInPlaceAsync(string connectionString, string dataDirectory,
    string backupDirectory, CancellationToken cancellationToken = default,
    Action? interruptBeforeSwap = null, Action? interruptAfterSwap = null);

Task<bool> CleanupInterruptedAsync(string connectionString, string dataDirectory,
    CancellationToken cancellationToken = default);
```

顺序与保证：

| 步骤 | 动作 | 中断后的状态 |
| --- | --- | --- |
| 1 | 离线校验清单与 SHA-256 | 未触碰数据库，标记不变 |
| 2 | 写恢复哨兵 + 取排他会话租约 | 两种模式都被阻止（`Blocked`） |
| 3 | 生成 `backups/pre-restore-<时间>` 可回退副本并校验 | 公开 schema 未动 |
| 4 | 导入到 `yingge_restore_*` 暂存 schema 并逐项比对 | 公开 schema 未动，暂存 schema 由清理入口删除 |
| 5 | 单事务内 `public → yingge_prev_*`、暂存 → `public` | 事务原子；提交前中断等于没换 |
| 6 | 复核公开 schema，重写激活标记，删除哨兵 | 若第 6 步失败则反向换名回退 |

- 失败时：已换名则反向换名；暂存 schema 被删除；只有回退成功才删除哨兵，回退失败则保留哨兵，使库在两种模式下都打不开。
- 成功时保留 `yingge_prev_*` 作为第二层回退副本（第一层是第 3 步的目录备份）。清理策略见 §6。
- `interruptBeforeSwap` / `interruptAfterSwap` 只用于失败注入，生产调用传 `null`。
- 恢复不改写 `AppPaths`、不启动/停止集群，集群生命周期仍由调用方管理。

### 2.3 `PostgresStorageModeActivation`

```csharp
Task<Marker> ActivateAsync(connectionString, dataDirectory, backupDirectory, ct);      // 切换（cutover）
Task<Marker> RewriteAfterRestoreAsync(connectionString, dataDirectory, backupDirectory, ct); // 已处于数据库模式
```

- `ActivateAsync` 前置条件：`config.json`、`library-index.json`、`metadata.json` 仍存在（切换前旧文件必须保留）；备份离线校验通过；数据库内容与备份逐项一致。任一失败都不写标记；写标记本身用临时文件 + `File.Move` 原子替换。
- `RewriteAfterRestoreAsync` 不要求旧 JSON 文件（只对已经是数据库模式的库使用），但仍要求备份与数据库一致。
- 标记内容为 JSON：`schemaVersion`、`libraryId`、`backupDirectory`、`backupManifestSha256`、`videoCount`、`metadataCount`、`postgresVersion`、`activatedUtc`。

### 2.4 `PostgresStorageModeGuard`

```csharp
enum StorageMode { LegacyFiles, PostgreSql, Blocked }
StorageMode Current(string dataDirectory);
bool LegacyFilesMayOpen(string dataDirectory);
bool PostgresMayOpen(string dataDirectory);
void BeginRestore(string dataDirectory);   // 写哨兵
void EndRestore(string dataDirectory);
```

判定规则：

1. 存在 `.postgres-restore-in-progress` → `Blocked`（两种模式都不许开）。
2. 标记是当前 JSON 格式且可解析 → `PostgreSql`。
3. 标记是隔离恢复写出的纯文本 `postgresql` / `postgresql-v1` → `PostgreSql`（授权，但无备份指针）。
4. 标记存在但无法解析（截断、损坏）→ `Blocked`，绝不放行文件模式。

### 2.5 `PostgresLibraryMigration` 增量

- `ImportAsync(..., string schema = "public")`：允许导入到暂存 schema。
- `VerifyContentAsync(..., string schema = "public")`、`VerifyAsync(..., string schema = "public")`。
- `InspectSchemaAsync` → `Empty / Legacy / Current / Newer`；`UpgradeSchemaAsync` 只升级 `Legacy`，遇到 `Newer` 抛 `NotSupportedException`。
- 新增 `library_schema(version)` 表（当前版本 1），导入时写入。

## 3. 主线接入顺序（不在本分支实施）

1. 启动：先 `PostgresStorageModeGuard.Current(dataDir)`；`Blocked` 时显示阻断页（说明恢复哨兵/损坏标记，给出「打开数据目录」「查看日志」「重试恢复」入口），不得回退到文件模式。
2. 设置页：数据库模式下把备份/恢复按钮路由到新 API，并保留文件模式旧实现；两种模式不得互相写入。
3. 备份：`CreateAsync` 在后台线程执行，UI 订阅进度（当前 API 只提供完成结果，进度回调待主线加）。
4. 恢复：先展示预览（备份时间、视频/资料/文档/队列数量、将覆盖的库、可回退副本位置），确认后执行 `RestoreInPlaceAsync`，成功后提示「原库已保留为 `yingge_prev_*` 与 `backups/pre-restore-*`」。
5. 切换：只有全部写入器迁移完成后，才允许调用 `ActivateAsync`；调用点必须持有排他会话租约。
6. 启动恢复：若发现哨兵，调用 `CleanupInterruptedAsync`；返回 `false` 时保持阻断并提示人工处理。

## 4. 不变量

- I1：没有通过校验的备份，不写 `storage-postgresql.active`。
- I2：标记写入是原子的；本次调用失败不会留下「已激活」状态。
- I3：恢复期间哨兵存在时，文件模式与数据库模式都不可打开。
- I4：暂存 schema 永远不承载线上读取；换名是唯一发布点。
- I5：恢复前一定先产生可回退副本（目录备份），且副本自身通过清单校验。
- I6：失败回退后数据库内容与恢复前逐项一致；回退失败则保持阻断，不假装成功。
- I7：数据库版本高于本程序时拒绝打开，不尝试降级写入。
- I8：备份包含的队列、审核、人物、插件、AI 作业与配置均为原始字节，恢复按清单整体替换。

## 5. 与并行任务的文件边界

- 本分支拥有：`PostgresLibraryBackup.cs`、`PostgresLibraryRestore.cs`、`PostgresStorageModeActivation.cs`、`PostgresLibraryExport.cs`、`PostgresBackupAcceptance.cs`、`BundledPostgresRuntime.cs` 的验收段。
- `PostgresLibraryBackupCoordinator.cs` 与探针中的 coordinated 断言来自并行分支 `codex/postgresql-backup-coordinator`（工作树 `2026-11-06-yingge-pg-backup-coordinator`）。本分支为自包含验证暂时保留；主线集成时**以该分支为准**，如已合并请删除本分支副本，只保留调用点。
- 写入器收口（`Postgres*Store`、`LibraryBackupService`）由 `codex/postgresql-writer-audit` 负责；本分支只调用其现有 API，不改其语义。
- `App.xaml.cs` / `MainWindow.xaml.cs` 由主线在集成时修改。

## 6. 残留风险（需主线决策）

1. `yingge_prev_*` 与 `backups/pre-restore-*` 目前不自动清理；需要保留策略（按数量/天数/磁盘水位），删除前必须确认新库已稳定运行。
2. 备份 API 无进度回调，长任务在 UI 上只能显示不确定进度。
3. 未做真实 ENOSPC 注入：只验证了空间预检与写失败清理，操作系统级磁盘写满未复现。
4. 30,000 部仅验证索引/资料/文档/队列与图片，不含真实海报解码与真实磁盘 I/O。
5. 安装包升级路径本轮未复测（详见验收报告）。
