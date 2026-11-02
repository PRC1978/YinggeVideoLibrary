# 1.6.0 大库备份恢复合成验收

验收命令：`--diagnose-large-backup <系统临时目录下全新空目录> <结果 JSON>`。该入口拒绝非临时目录、非空目录和文件夹链接；只生成合成索引，不读取用户视频库或创建视频内容。原始结果见 `LARGE_BACKUP_SYNTHETIC_RESULT.json`。

生成 32,909 条合成视频索引和 15,730 个分类叶子，索引中的媒体路径特意不存在，以模拟扫描根暂时不可用。使用产品现有 `StageOfflineBackup` → `ApplyPendingOfflineBackupAtStartup` → `Verify` → `CreateRestorePreview` → `StageRestore` → `ApplyPendingAtStartup` 路径。备份后给三个受管 JSON 追加合法空白以制造漂移，恢复后对照备份内 `config.json`、`library-index.json`、`metadata.json` 的 SHA-256。

结果：备份成功并校验通过；恢复成功；三文件与备份逐字节一致；恢复后仍有 32,909 条索引。备份阶段约 2.6 秒，恢复阶段约 35.5 秒（本机单次运行，不作为性能保证）。完整功能自检 `passed=true`、`realLibraryRead=false`；跨设备自检 21 项全部通过。

首次运行暴露一个产品问题：同一视频库的恢复预览要求每条索引路径的媒体文件必须在线，离线盘或丢失文件会使恢复被拒绝。已调整为同库、同机器且索引路径、大小、修改时间一致时允许媒体暂时缺席；在线媒体仍检查实际大小、修改时间与有记录时的内容指纹。跨设备映射门槛保持原样。

**证据边界**：这证明合成 32,909 条规模的受管 JSON 备份与恢复流程，不能等同于用户真实库副本的完整恢复。真实库副本恢复及 125%／150% DPI 仍未验收，1.6.0 发布清单不因此放行。
