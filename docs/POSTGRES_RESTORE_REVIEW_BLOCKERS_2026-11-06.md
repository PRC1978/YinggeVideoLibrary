# PostgreSQL 恢复线只读复核：发布阻断

## 集成后处理（2026-11-06）

三项恢复缺口已在主集成分支修复：`48f47aa` 将图片随 schema 一起切换并在异常时回退、禁止暂存 schema 查询落到 `public`、恢复失败时还原原始标记；`c1d98d2` 增加标记改写后故障注入；`7c00d13` 修复图片目录切换到一半时的回退；`8da4b80` 验证缺表暂存库必被拒绝。独立隔离验收 `--self-test-postgres-backup` 的 1 部和 3,000 部恢复、图片字节回退、标记回退、部分图片切换回退及缺表拒绝均通过，`realLibraryRead=false`。30,000 部合成库沿用 WorkBuddy 原始验收结果，本轮未复跑。

`c619786` 又将中断恢复的清理限定到本次暂存 schema：成功恢复留下的旧回退 schema 不再影响“切换前中断”的判断；不确定时保留阻断标记。独立验收的 `InterruptedRestoreCleaned=true`、`backupFailureInjectionVerified=true`。

**仍未达到发布条件：**正常应用启动仍拒绝 PostgreSQL 标记；主窗口、扫描、审核、队列等日常读写尚未统一切换至数据库。不得据此将数据库模式标成已上线。

本轮查看的是 `C:\Users\yunwu\WorkBuddy\2026-11-06-yingge-pg-backup` 中另一会话尚未提交的恢复代码。尚未运行该代码的端到端测试；以下是代码审查发现，须由该工作树作者修复并以故障注入验证后再集成。

1. `PostgresLibraryRestore.RestoreInPlaceAsync` 切换了数据库 schema，但没有把所选备份中的 `posters/` 和 `thumbs/` 切换为对应版本。恢复后的资料可能引用错误修订的图片。必须连同图片资产一起恢复，失败时恢复原图片，并测试图片复制中断及回滚。
2. `PostgresLibraryMigration.VerifyContentAsync(schema)` 设置 `search_path` 为候选 schema 再加 `public`，但使用未限定的表名。候选表缺失时查询可能落到 `public` 的旧表，误判候选库有效。必须对候选 schema 的每个必需表做存在性检查并使用 schema 限定查询；测试缺表候选库。
3. `RestoreInPlaceAsync` 在最终核验和结束恢复前写入 `storage-postgresql.active`。后续失败时数据库会回滚，但标记可能保留新快照信息。须在回滚中恢复原标记字节，或重排步骤使标记只在全部可失败操作完成后写入；测试写标记后故障。

以上任一项未修复，不能启用正常启动的 PostgreSQL 模式或发布数据库版。
