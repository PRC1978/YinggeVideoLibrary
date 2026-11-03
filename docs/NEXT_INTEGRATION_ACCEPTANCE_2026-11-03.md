# 下一轮搜刮、封面、插件与性能集成验收

基线：已发布的 1.6.0 后续开发分支 `codex/next-integration`。四条开发线使用独立工作树；本集成工作树未读取真实视频库、私人媒体，也未使用用户 API Key。Kimi 的前端重做另从 `525f73f` 建工作树，不属于本轮集成。

## 已集成

- 搜刮封面：候选选择、审核比较、放大、按需下载与手动选用；封面只从本库管理目录读取，清除自定义封面不会删除共享搜刮缓存。隔离 WPF 诊断的候选、比较、放大窗口截图正常；选用后另起进程读取持久设置成功。证据：`%TEMP%\yingge-cover-integrated-20261103\`。
- 准确率评测：人工核对样本模板、按来源统计和 95% Wilson 区间；不足或合成样本不建议调阈值。六条合成样本 0 次误批准，只验证评测计算，不代表真实准确率。
- 插件：固定选项设置与 v1 清单检查；本机 AI 插件使用既有 AI 作业/预算/限速与审核门禁；工具命令只读并限制输出长度。插件端到端自检 63/63，通过真实示例子进程覆盖取消、超时、重试、禁用、预算和错误响应。第三方打包兼容测试只使用独立复制的示例插件，不是第三方作者交付物。
- 大库：隔离夹具可生成最多 1000 张 JPEG 封面；600 条全封面与 30,000 条部分封面搜索诊断通过，结果 ID 和卡片绑定一致。详细数据及限制见 `POSTER_PERFORMANCE_ACCEPTANCE_2026-11-03.md`。

## 集成检查

- Release 构建：0 警告、0 错误；两个示例插件 Release 构建：0 警告、0 错误。
- `--self-test-features`：`passed=true`、`realLibraryRead=false`。
- `--self-test-plugins`：63/63；`--self-test-plugin-queue`、`--self-test-missing-files`、`--self-test-ai-processes`：均 `passed=true`。
- `--diagnose-ui-cover` 与 `--diagnose-ui-cover-reload`：均 `passed=true`，第二次进程确认封面选择持久化。
- 六条合成 AI 评测用例：退出码 0，`falseApprovals=0`，`calibrationStatus=synthetic-cannot-calibrate`。

## 证据边界

真实来源匹配准确率、真实模型费用和第三方插件兼容性尚需人工标注样本及外部交付物。封面下载没有在本轮访问真实站点；隔离界面验收使用 WPF 事件触发，不等于真实鼠标与键盘验收。合成 JPEG 不代表用户海报尺寸和存储设备速度。云端 AI 插件和可写工具命令未开放，不能称为已实现。本轮尚未安装或发布新版本。
