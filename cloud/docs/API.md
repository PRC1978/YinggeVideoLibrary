# API 契约 · 云端账号与错误日志

所有路径以 `/api/v1` 开头，只接受 HTTPS。账号和报告请求与响应为 JSON（`application/json`）；头像为 PNG。错误为
`application/problem+json`。未知字段一律拒绝（400），字段名区分大小写以外的写法不做兼容。

## 认证

- 登录成功后返回 43 字符不透明令牌，客户端以 `Authorization: Bearer <token>` 携带。
- 服务端只保存令牌的 SHA-256；会话默认 8 小时，可在 `Security:SessionHours` 调整。
- 退出、管理员停用或到期后，后续请求立即返回 401/403。
- 可选请求头 `X-Client-Version`（≤32 字符）会写入会话审计。

## 端点

| 方法 | 路径 | 认证 | 说明 |
| --- | --- | --- | --- |
| POST | `/auth/register` | 邀请码 | 一次性邀请码注册 |
| POST | `/auth/login` | — | 账号密码登录 |
| POST | `/auth/logout` | Bearer | 撤销当前会话 |
| GET | `/auth/session` | Bearer | 会话状态与今日配额 |
| POST | `/reports` | Bearer | 上传错误日志 |
| GET | `/reports` | Bearer | 列出自己最近 50 份 |
| DELETE | `/reports/{id}` | Bearer | 删除自己的一份日志 |
| GET | `/account/avatar` | Bearer | 读取自己的头像；未设置返回 404 |
| PUT | `/account/avatar` | Bearer | 上传自己的方形 PNG，最多 240 KB |
| DELETE | `/account/avatar` | Bearer | 删除自己的云端头像 |
| GET | `/health` | — | 数据库连通性 |

### POST /auth/register

```json
{"inviteCode":"ABCD-EFGH-IJKL-MNOP-QRST","username":"alice","password":"至少12位含字母数字"}
```

- 201 → `{"accountId":"<uuid>","username":"alice"}`
- 400 `invalid_request`：账号名/密码不符合规则（3–32 位小写字母数字 `._-`；密码 12–128 位且含字母与数字）
- 403 `invite_invalid`：邀请码不存在、已用、已撤销或过期（同一文案，避免探测）
- 409 `username_taken`
- 429 `rate_limited`

邀请码与账号创建在同一事务内完成：用户名冲突不会作废邀请码。

### POST /auth/login

```json
{"username":"alice","password":"..."}
```

- 200 → `{"token":"<43 字符>","expiresAt":"2026-11-05T04:00:00+00:00","username":"alice","accountId":"<uuid>"}`
- 401 `invalid_credentials`（账号不存在与密码错误不可区分，耗时也已对齐）
- 403 `account_disabled`
- 429 `account_locked`（连续 5 次失败锁定 15 分钟，带 `Retry-After`）
- 429 `rate_limited`（按来源 IP 每分钟 10 次）

### GET /auth/session

```json
{"authenticated":true,"username":"alice","expiresAt":"...","uploadsLast24Hours":2,"dailyUploadLimit":10}
```

### POST /reports

```json
{
  "clientVersion": "1.6.6",
  "os": "windows",
  "arch": "x64",
  "occurredAtUtc": "2026-11-04T12:00:00Z",
  "messages": ["生成封面失败：无法连接 TMDB。"]
}
```

- 201 → `{"id":"<uuid>","receivedAt":"...","expiresAt":"...","messageCount":1,"byteSize":188}`
- 401 `unauthorized` / 403 `account_disabled`
- 413 `payload_too_large`：正文超过 128 KB（在解析前按字节拒绝）
- 400 `invalid_json`：JSON 非法或含未定义字段
- 422 `report_invalid`：条数 >100、单条 >2000 字符、控制字符、`os`/`arch` 不在白名单、时间超出 ±30 天
- 422 `report_contains_private_data`：仍含本地路径、UNC 路径、媒体文件名、分享链接、签名 URL 或凭据；响应带 `index` 指明第几条
- 409 `duplicate_report`：同一账号提交过内容相同的日志（忽略时间戳）
- 429 `upload_quota_exceeded`：24 小时 10 份或 5 分钟 3 份

允许字段严格固定为 `clientVersion/os/arch/occurredAtUtc/messages`。服务端在存储前再次扫描私密数据，
被拒绝的请求不写 `error_reports`、不落盘，只记一条不含正文的审计。

### GET /reports

```json
[{"id":"<uuid>","receivedAt":"...","expiresAt":"...","messageCount":2,"byteSize":420}]
```

只返回调用者自己的、未删除的记录。

### DELETE /reports/{id}

- 204：标记删除并删除磁盘文件
- 404 `report_not_found`：不存在或不属于调用者（不区分，避免枚举）

### GET /health

- 200 → `{"status":"ok","database":"ok"}`
- 503 → `{"status":"degraded","database":"unavailable"}`

## 错误体

```json
{"type":"https://reports.yingevideo.top/problems/duplicate_report","title":"重复提交","status":409,
 "code":"duplicate_report","detail":"这份日志已经上传过，无需重复提交。"}
```

`code` 稳定，客户端据此分支；`detail` 为中文提示，可能随文案调整。带 `Retry-After` 的响应均有秒数。

## 限额汇总

| 项目 | 默认值 | 配置键 |
| --- | --- | --- |
| 请求体上限 | 256 KB（全局），日志 128 KB | Kestrel / `ReportLimits` |
| 单份日志 | 100 条，单条 2000 字符 | 代码常量 |
| 上传配额 | 24h 10 份，5min 3 份 | `Reports:DailyLimit` / `Reports:BurstLimit` |
| 登录 | 10 次/分钟/IP，5 次失败锁定 15 分钟 | `Security:LoginPerMinute` / `MaxFailedLogins` |
| 注册 | 5 次/小时/IP | `Security:RegisterPerHour` |
| 全局 | 300 次/分钟/IP | `Security:GlobalPerMinute` |
| 保留期 | 30 天 | `Reports:RetentionDays` |
