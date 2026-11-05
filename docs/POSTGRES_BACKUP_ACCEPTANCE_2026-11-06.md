# PostgreSQL 备份 / 恢复 / 安装验收报告（2026-11-06）

分支 `codex/postgresql-backup-acceptance`，基线 `5d3384a`，独立工作树
`C:\Users\yunwu\WorkBuddy\2026-11-06-yingge-pg-backup`。未修改主集成工作树，未改动
`App.xaml.cs` / `MainWindow.xaml.cs`，未推送、未打标签、未安装正式版、未发布。

## 1. 结论

- 数据库模式的一致性备份、带回退副本的就地恢复、失败不残留激活标记：**代码完成并在隔离集群实测通过**。
- 合成库矩阵 1 / 3,000 / 30,000 部视频，含审核、人物、标签、队列、插件设置、AI 作业：**备份 → 修改 → 恢复 → 重启逐项一致，全部通过**。
- 失败注入 11 项全部通过（中断备份、空间预检、损坏包、旧版本包、旧库升级、新库拒绝、恢复中断、清理、激活失败、换名后回退、哨兵阻断）。
- 独立 AppId 安装 → 覆盖升级 → 卸载通过；安装后的 PostgreSQL 18.6 二进制在临时数据目录完成 initdb / start / stop。正式安装与真实媒体库未被读取或修改。
- **普通启动仍未接入 PostgreSQL**，设置页也没有数据库备份/恢复入口；本轮通过不等于可以发布数据库模式。

## 2. 改动清单

| 文件 | 类型 | 内容 |
| --- | --- | --- |
| `PostgresLibraryBackup.cs` | 新增 | 排他会话租约 + `pg_advisory_lock(178942601)`；事务内复制图片；空间预检；清单 + SHA-256；原子发布；失败清理 |
| `PostgresLibraryRestore.cs` | 新增 | 恢复哨兵 → 可回退副本 → 暂存 schema 导入校验 → 单事务换名 → 复核 / 重写标记；失败反向换名；中断清理 |
| `PostgresStorageModeActivation.cs` | 新增 | 仅在校验通过后原子写 `storage-postgresql.active`；切换与恢复后重写两条路径；兼容隔离恢复的纯文本标记 |
| `PostgresStorageModeGuard.cs` | 修改 | 三态 `LegacyFiles / PostgreSql / Blocked`；哨兵阻断两种模式；损坏标记不放行文件模式 |
| `PostgresLibraryExport.cs` | 修改 | 抽出 `WriteSnapshotAsync` / `PublishAsync`；图片在提交前复制；空间预估；失败清理统一 |
| `PostgresLibraryMigration.cs` | 修改 | `schema` 参数（暂存导入/校验）；`library_schema` 版本表；`InspectSchemaAsync` / `UpgradeSchemaAsync` 拒绝更高版本 |
| `PostgresLibraryBackupCoordinator.cs` | 并行任务带入 | 离线整库快照与新目录恢复（本分支保留并纳入探针，不是本轮新增） |
| `PostgresBackupAcceptance.cs` | 新增 | 矩阵、失败注入、实体一致性断言 |
| `BundledPostgresRuntime.cs` | 修改 | 接入验收段；崩溃日志钩子；新增断言计入 `passed` |
| `docs/POSTGRES_BACKUP_RESTORE_CONTRACT.md` | 新增 | 主线接入契约与不变量 |
| `docs/POSTGRES_BACKUP_RESTORE_UX_AUDIT_2026-11-06.md` | 新增 | 前端只读 UX 审核 |
| `docs/POSTGRES_BACKUP_ACCEPTANCE_2026-11-06.md` | 新增 | 本报告 |

## 3. 关键保证

1. 备份期间数据库写入器被同一把 advisory lock 阻塞；文件模式应用被排他 `.library-session.lock` 阻塞。
2. 行数据与图片字节在同一事务窗口内取得；此前版本是“先提交事务、再复制图片”，存在跨版本窗口。
3. 恢复先写哨兵，再生成并校验 `backups/pre-restore-<时间>`；换名是唯一发布点且在一个事务里完成。
4. 恢复成功保留 `yingge_prev_*` 作为第二层回退；失败反向换名并删除暂存 schema；回退失败保留哨兵，两种模式都打不开。
5. 激活标记是 JSON + 临时文件原子替换；失败不留标记；纯文本旧标记只授权、不充当可回退备份指针。
6. 数据库版本高于程序时抛 `NotSupportedException`，不尝试写入。

## 4. 可复跑命令

```powershell
# 1) Release 构建
cd C:\Users\yunwu\WorkBuddy\2026-11-06-yingge-pg-backup
dotnet build YinggeVideoLibrary.csproj -c Release -p:EnableSourceLink=false --nologo

# 2) 运行时（本工作树 artifacts 已被 .gitignore 忽略；initdb 要求绝对路径）
#    首次准备：复制或运行 scripts/prepare-postgres-runtime.ps1 -Download
artifacts\postgres-runtime\bin\initdb.exe --version

# 3) 基础矩阵（1 / 3,000 + 全部失败注入）
bin\Release\net9.0-windows\映格视频库.exe --self-test-bundled-postgres `
  "$PWD\artifacts\postgres-runtime" "$env:TEMP\yingge-pg-base.json"

# 4) 含 30,000 部的规模矩阵（原始 30k 索引项同时运行）
cmd /c artifacts\installer-probe\run-scale.cmd
# 结果写入 artifacts\probe-scale.json

# 5) 失败注入位于同一次探针内，无需单独命令
```

注意：`--self-test-bundled-postgres` 的结果路径必须是绝对路径且父目录可写；通过 MSYS/Git Bash 调用时不要用 `timeout` 包装可执行文件（会返回 127），也不要把 Windows 路径作为 `cmd /c` 的字符串参数（会被路径转换改写）。

## 5. 隔离证据

两次最终运行的 JSON：`%TEMP%\yingge-pg-base.json`、`artifacts\probe-scale.json`（摘录见
`docs/POSTGRES_BACKUP_ACCEPTANCE_RESULT.json`）。

| 项目 | 1 部 | 3,000 部 | 30,000 部 |
| --- | --- | --- | --- |
| 导入 ms | 508 | 828 | 4,369 |
| 备份 ms | 448 | 587 | 2,253 |
| 恢复 ms（含可回退副本 + 暂存导入 + 换名 + 复核） | 2,742 | 3,697 | 13,143 |
| 备份字节 | 6,468 | 2,954,153 | 29,543,161 |
| 全部断言 | 10/10 | 10/10 | 10/10 |

公共结果：`passed=true`、`stopped=true`、`realLibraryRead=false`、`loopbackOnly=true`、
`credentialRoundTrip=true`。原始 30,000 索引项：导入 1,910 ms、读取 313 ms、全量保存 2,504 ms、
单行增量 419 ms，`scaleVerified=true`、`scaleDeltaVerified=true`、`scaleFileRoundTripVerified=true`。

失败注入结果（`backupFailureInjection` 全为 true）：

| 注入 | 断言 |
| --- | --- |
| 备份复制阶段取消 | 无最终目录、无 `.partial-*` 残留 |
| 空间不足预检 | `IOException`，未创建目录（**预检为真，未制造真实 ENOSPC**） |
| 篡改 `metadata.json` | 恢复在写库前拒绝，标记未变、无哨兵 |
| 清单 `schemaVersion=0` | 离线校验失败，明确拒绝旧包 |
| 旧库（删除 `library_schema`） | `Legacy → Upgrade → Current` |
| 数据库版本 99 | `Newer` + `NotSupportedException` |
| 换名前取消 | 公开 schema 与变异状态不变，标记仍为 PostgreSQL |
| 残留暂存 schema + 哨兵 | 清理后暂存删除、哨兵移除、恢复可打开 |
| 激活失败（空备份） | 不产生标记，仍为文件模式 |
| 换名后注入失败 | 反向换名，数据库逐项等于变异后状态，无哨兵 |
| 哨兵存在 | `Blocked`：文件模式与数据库模式都拒绝 |

## 6. 安装 / 升级 / 卸载（独立标识）

- 包：Inno Setup 7.1.0，`AppId=PRC1978.YinggeVideoLibrary.PGIsolatedAcceptance`，目录固定为
  `%TEMP%\yingge-install-acceptance\app`，v1=1.0.0，v2=1.0.1（v2 额外投放 `v2-marker.txt`）。
- v1 安装：退出码 0；`映格视频库.exe`、`postgres\bin\postgres.exe`、`share\postgres.bki` 存在；
  `postgres --version` = 18.6；安装日志包含 “Installation process succeeded”。
- 启动 / 停止：在 `%TEMP%\yingge-install-acceptance\pgdata` 执行 `initdb`（退出 0）、
  `pg_ctl start`（退出 0，server started）、`pg_ctl stop`（退出 0，shut down）。
- 覆盖升级：v2 安装退出 0；`v2-marker.txt` 存在；`postgres --version` 仍为 18.6；
  再次 initdb / start / stop 全部退出 0；日志 “Installation process succeeded”。
- 卸载：`unins000.exe /VERYSILENT` 退出 0，`app` 目录被删除。
- 正式安装 `%LOCALAPPDATA%\Programs\YinggeVideoLibrary` 在前后均为 6 个条目、时间戳 `11-05 17:14`，未被触碰。
- 未从安装目录启动桌面应用本身，避免读取真实库；启动/停止验证的是随包 PostgreSQL 二进制 + 临时数据目录。

## 7. 未验证 / 残留风险

1. **普通启动未接入数据库**：本分支没有修改启动与设置页；数据库模式仍只能由隔离探针驱动。
2. **真实 ENOSPC 未复现**：只验证了空间预检与写失败清理，操作系统级“磁盘写满”未制造。
3. **30,000 部为合成元数据**：不含海报解码、真实磁盘 I/O、真实扫描耗时。
4. `yingge_prev_*` 与 `backups/pre-restore-*` 不自动清理，长期使用会累积；需要保留策略。
5. 备份 API 无进度回调，UI 只能做不确定进度。
6. 安装升级的注册表版本号未直接读取（`reg.exe` 被安全策略禁用）；以 v2 专属文件、安装日志与文件替换为证据。
7. 探针对上一轮遗留进程敏感：两次早期规模运行在收尾阶段异常退出（无结果 JSON），清理遗留
   `映格视频库.exe` 后重跑通过。复跑前应先确认没有遗留探针进程与临时集群。
8. 恢复会丢弃队列二进制日志代次（`scrape-queue.journal.*.bin`），统一由 `scrape-queue.json` + 队列表表达；这是导出格式的既定语义，不是字节级全等恢复。
9. `PostgresLibraryBackupCoordinator.cs` 与并行分支 `codex/postgresql-backup-coordinator` 重叠；主线集成时以该分支为准，删除本分支副本后只保留探针调用点。

## 8. 前端文案口径

- 不得在发行说明或设置页写“数据库备份/恢复已完成”。
- 设置页现有「下次启动一致性备份 / 恢复最近备份」在数据库模式下会写旧 JSON 暂存文件，必须在主线接入时加模式判断（详见 UX 审核文档 G1）。
