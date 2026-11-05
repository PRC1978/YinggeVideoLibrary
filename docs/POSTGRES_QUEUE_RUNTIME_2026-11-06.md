# PostgreSQL 搜刮队列与来源冷却适配

本分支从 `5d3384a` 独立实施，不改普通启动和界面入口。新增数据库模式的来源冷却记录读写，以及按标题/番号来源轮流选取任务的调度适配；原有队列增删、失败、重试、延时、取消沿用 `PostgresScrapeQueueStore`。

来源冷却保存在 `library_document` 的 `scrape-source-backoff.json`，与队列修改共享 advisory transaction gate。预留请求槽在联网前提交；失败冷却、成功清除和下一次允许请求时间可重启回读。无效来源文档使读取失败，避免限速状态损坏后继续请求。

调度读取数据库任务和来源冷却，不读旧队列 journal。标题来源冷却时会按每轮最多 1024 条已到期标题查找本地已验证候选；该候选仍须经过原有身份检查。此路径目前每次轮询都会完整读取数据库队列，3 万条场景尚未测量；正式切换前应使用 SQL 索引做分页读取并测量轮询延迟。

隔离私有 PostgreSQL 18.6 自检：`sourceBackoffLifecycleVerified=true`、`queueFairSchedulingVerified=true`、`queueLifecycleVerified=true`、`passed=true`、退出码 0。Release 构建 0 警告、0 错误。测试未读取真实库。

**仍未接入普通启动**：主线须将 `MainWindow.TitleScrapeQueue.cs` 和 `MainWindow.ScrapeQueue.cs` 的所有队列操作、来源限速操作路由到此适配，且在数据库模式下禁止写文件队列；并在 3 万条任务及重启/取消/来源故障下验证。未满足前不得发布数据库模式。
