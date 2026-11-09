# 离线验收补充（2026-11-09）

基线提交：`c2335ef`。本轮只运行命令行与隐藏的离屏诊断，不操作前台窗口、已安装程序或真实媒体文件。

## 列表与搜索

- Release 构建：0 警告、0 错误。
- 完整功能自检：`passed=true`，`realLibraryRead=false`。报告：`artifacts/1733-p0/perf-real/features-empty-state-correct.json`。
- 离屏 UI 矩阵：10 主题 × 3 尺寸 × 8 页面，240 个布局、1,804 项检查，失败 0。报告：`artifacts/1733-p0/perf-real/ui-empty-state-final/report.json`。
- 完整列表零命中：空结果面板及其父容器可见，旧虚拟卡片不会进入当前选择范围（0 条）。报告：`artifacts/1733-p0/perf-real/probe-empty-state-verified.json`。
- 首次显示结果仍有约 100–120 ms 的布局脉冲；卡片生成与测量是主要成本。将预建缓存从 0.5 降到 0 会使滚动 P95 从约 3.6 ms 恶化到 15.4–15.7 ms，试验改动已撤回。详见 `docs/SEARCH_OFFLINE_OPTIMIZATION_2026-11-09.md`。

## PostgreSQL 备份与恢复

使用仓库内捆绑运行时及临时合成库运行 `--self-test-postgres-backup`，退出码 0，`passed=true`、`realLibraryRead=false`。1 部和 3,000 部两档的导入、备份校验、资料变更、恢复、回退副本、标记改写、重启及运行时写入均通过；15 项故障注入全部通过，包括中断恢复、标记回退、部分图片切换回退和缺表拒绝。报告：`artifacts/1733-p0/perf-real/postgres-backup-final.json`。

这仍是合成库验收。真实用户库副本的完整迁移与恢复、PostgreSQL 跨主版本升级、实际前台交互及真实媒体海报 I/O 尚未由本轮验证，不能据此标记为完成。
