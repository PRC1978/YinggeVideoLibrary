# PostgreSQL 日常写入入口审计（2026-11-06）

本文件按“哪个数据是权威状态”列出切换点。搜刮缓存、海报和诊断输出可继续保存在文件中；这些文件不能决定审核、人物、分类或队列的最终状态。

| 权威状态 | 当前文件入口 | 现有数据库能力 | 正式切换仍需接线 |
| --- | --- | --- | --- |
| 视频路径、文件夹、缺失标记 | `MainWindow.SaveIndexAsync` → `library-index.json`；扫描、移动、缺失清理 | `PostgresLibraryIndexStore` 全量/增量写入及修订拒绝 | 扫描/批量移动/清理都改走数据库，界面刷新从数据库回读 |
| 作品资料和来源 | `MetadataStore`、审核和搜刮入口 → `metadata.json` | `PostgresLibraryMetadataStore`；`PostgresLibraryAtomicStateStore` | 审核/撤销、搜刮写入与来源归属在同一事务 |
| 分类、标签、收藏、审核队列、设置 | `MainWindow.SaveConfig` → `config.json` | `PostgresLibraryConfigStore` 三路合并；原子事务可同时写配置和资料 | 所有设置和标签路径改用数据库；启动加载、冲突提示与凭据仍分开 |
| 审核历史、撤销和中断恢复 | `ReviewTransactionStore`、`DuplicateMetadataTransactionStore`、`MissingCleanupStore` | `PostgresReviewTransactionStore` 通过原子事务写 config/metadata/history | 批量审批、撤销、缺失清理、重复资料合并、恢复逐条接线；日志不得领先于数据 |
| 搜刮队列和来源退避 | `ScrapeQueueStore` → checkpoint/journal；`scrape-source-backoff.json` | `PostgresScrapeQueueStore` 已有部分生命周期操作 | 调度、限速、来源失败、重试、取消和重启都改用同一表，不能再写文件 journal |
| 人物和关联作品 | `RoleProfileStore` → `role-profiles.json` | 新增 `PostgresRoleProfileStore`：校验、修订 CAS、过期编辑拒绝 | 将人物窗口的加载/保存改走该存储；关联作品与相关审核写入需事务化 |
| 插件启停和候选 | `PluginStateStore`、`PluginCandidateStore` | `PostgresUserStateStore` 新增插件状态读写 CAS；候选仍只有原字节文档存储 | 插件管理、搜刮入审和候选切换改走数据库；候选与审核入队原子提交 |
| 评分、图关系移动、审核视图、站点导入 | `PersonalRatingStore`、`CategoryGraphMove`、`ReviewSavedViewStore`、`SiteListImportStore` | `PostgresUserStateStore` 新增评分、图移动历史和审核视图的校验与 CAS；站点导入仍只有原字节文档存储 | 每个修改入口接入数据库；图移动历史与 config 分类变更需同一事务，不能单独提交 |
| AI 预算与作业 | `AiRequestQueue`、`AiJobStore` | 迁移包含预算文件和 AI 作业快照 | 预留/结算、重试、取消与作业状态须持续写数据库；密钥留在 Windows 凭据管理器 |
| 一致性备份与恢复 | `LibraryBackupService*` 的文件清单和离线恢复 | `PostgresLibraryExport` 可生成核验的回退文件包 | 数据库模式的完整备份、恢复、回滚与存储标记操作；由独立并行线负责 |

## 切换顺序与拒绝条件

1. 独立安装和启动私有 PostgreSQL，仅监听 loopback；迁移前获得离线库锁、生成完整备份。
2. 导入、核对视频/资料/所有白名单状态；每个权威写入口都改走数据库。旧 JSON 保留为冻结回退副本，不再由正常界面写入。
3. 备份/恢复、审核/撤销、队列/人物/插件端到端均通过后，持锁写入 `storage-postgresql.active`。标记出现时旧文件版必须拒绝启动；数据库版不得从旧文件悄悄回退。
4. 任何数据库连接失败、写入冲突或恢复失败均停在明确错误状态，不能让新旧副本同时继续写。

当前提交只补齐人物档案、评分、审核视图、图移动历史和插件状态的**隔离数据库存储能力**，尚未把普通界面接入它们。单文档 CAS 不能替代跨文档事务；图移动与配置、候选与审核等必须使用同一事务。正常启动仍使用旧文件库，不能把本分支作为已完成的数据库发布版。

## 隔离证据

`--self-test-bundled-postgres` 在私有 PostgreSQL 18.6 集群退出码 0，`passed=true`、`typedPersonStoreVerified=true`、`typedUserStateCasVerified=true`、`realLibraryRead=false`。测试含人物、评分、审核视图、图移动历史和插件状态的数据库读写，及旧修订拒绝；没有读取真实媒体或正式视频库。
