# PostgreSQL 备份与恢复独立验证（2026-11-06）

> 后续扩展与完整矩阵结论见 `docs/POSTGRES_BACKUP_ACCEPTANCE_2026-11-06.md`；本文保留为当日首轮加固记录。

分支 `codex/postgresql-backup-acceptance` 从 `5d3384a` 分出。本轮只加固离线备份清单校验及取消恢复断言，不改变普通启动模式，也不写 `storage-postgresql.active`。

## 改动

- `PostgresLibraryExport.VerifyOfflineAsync` 对未登记嵌套目录返回失败，避免清单只覆盖顶层文件却遗漏子目录；无效结构按 `false` 处理，不把异常误当成功。
- 私有集群探针在已验证快照内插入 `title-candidates/unlisted/`，确认拒绝后移除；再取消图片恢复，确认目标目录和部分目录均未留下。

## 隔离验证

- `dotnet build YinggeVideoLibrary.csproj -c Release /p:EnableSourceLink=false --nologo`：0 警告、0 错误。
- `--self-test-bundled-postgres <独立运行时> <结果 JSON>`：退出码 0，`passed=true`、`unlistedNestedBackupDirectoryRejected=true`、`cancelledAssetRestoreLeavesNoTarget=true`。证据 `%TEMP%/yingge-pg-backup-f47fb57f9a8c469d889ec516989f6247.json`。
- 同命令附 `--scale`：30,000 部合成库，退出码 0，`passed=true`、`scaleDeltaVerified=true`，新断言均为 true。证据 `%TEMP%/yingge-pg-backup-scale-e9d7ea7c010545d0928667d0c5f119ec.json`。

探针使用 `%TEMP%` 下的私有 PostgreSQL 集群与合成资料；没有读取真实视频库或启动正式安装版。

## 仍未完成

普通启动尚未使用 PostgreSQL；设置页的数据库备份和恢复入口、生产切换、完整写入入口收口均未实现。现有 `PostgresLibraryExport` 与 `PostgresLibraryRecovery` 只能作为离线快照及新空数据库恢复组件，不能据此宣称日常数据库备份已经上线。安装升级与 UI 交互未在本轮复测。发行版不得因本轮通过而启用 PostgreSQL 模式。
