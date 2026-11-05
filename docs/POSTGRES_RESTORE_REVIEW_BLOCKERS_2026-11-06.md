# PostgreSQL 恢复线只读复核：发布阻断

本轮查看的是 `C:\Users\yunwu\WorkBuddy\2026-11-06-yingge-pg-backup` 中另一会话尚未提交的恢复代码。尚未运行该代码的端到端测试；以下是代码审查发现，须由该工作树作者修复并以故障注入验证后再集成。

1. `PostgresLibraryRestore.RestoreInPlaceAsync` 切换了数据库 schema，但没有把所选备份中的 `posters/` 和 `thumbs/` 切换为对应版本。恢复后的资料可能引用错误修订的图片。必须连同图片资产一起恢复，失败时恢复原图片，并测试图片复制中断及回滚。
2. `PostgresLibraryMigration.VerifyContentAsync(schema)` 设置 `search_path` 为候选 schema 再加 `public`，但使用未限定的表名。候选表缺失时查询可能落到 `public` 的旧表，误判候选库有效。必须对候选 schema 的每个必需表做存在性检查并使用 schema 限定查询；测试缺表候选库。
3. `RestoreInPlaceAsync` 在最终核验和结束恢复前写入 `storage-postgresql.active`。后续失败时数据库会回滚，但标记可能保留新快照信息。须在回滚中恢复原标记字节，或重排步骤使标记只在全部可失败操作完成后写入；测试写标记后故障。

以上任一项未修复，不能启用正常启动的 PostgreSQL 模式或发布数据库版。
