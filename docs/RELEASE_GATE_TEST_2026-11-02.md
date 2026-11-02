# 1.6.0 发布门槛复测（2026-11-02）

基线：`84f8f1e`。使用隔离数据目录；没有读取真实视频库、调用付费 AI、安装或发布。

| 检查 | 结果 |
| --- | --- |
| `--self-test-features` | 退出码 0，`passed=true`，`realLibraryRead=false` |
| `--self-test-updater`、`--self-test-startup-update` | 均退出码 0，`passed=true` |
| `--self-test-anime`、`--self-test-airborne` | 均退出码 0，`passed=true` |
| `--self-test-ai-processes` | 退出码 0，`passed=true` |
| `--self-test-missing-files`、`--self-test-plugins`、`--self-test-plugin-queue` | 均退出码 0，`passed=true` |
| `--self-test-cross-device` | 正确参数重跑后退出码 0，`AllPassed=true`，21 项检查 |
| 1.5.4 → 1.6.0 隔离更新 | 从 GitHub v1.5.4 下载便携程序，SHA-256 与发布清单一致；生产替换函数在临时副本上替换成功，新版从目标路径后台重启成功，旧版备份哈希保持 |

第一次跨设备测试把“输出 JSON”和“目标目录”参数顺序传反，导致自检入口异常退出且没有报告。修正测试命令后通过；该次错误不作为产品回归结果。原始报告在 [`evidence/release-gate-2026-11-02`](evidence/release-gate-2026-11-02)。

**仍未通过发布门槛**：125%/150% 系统缩放实测；二级搜刮页与费用预览弹窗的真实鼠标/键盘交互；1.6.0 安装包生成和安装升级验收。更新转换测试使用后台重启探针，不覆盖正常可见窗口启动。当前单文件构建使用提交 `a0caf01` 的程序代码，最终发布资产应在最终提交后重新构建并计算哈希。
