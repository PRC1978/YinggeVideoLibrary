# 云端公网端口阻塞（2026-11-05）

- `reports.yingevideo.top` 的 A 记录经公共 DNS 查询指向 `43.226.58.187`。
- Ubuntu 服务器已安装 nginx 与 certbot；临时 nginx 仅监听 80，并在本机 `127.0.0.1` 正确返回随机 ACME 测试文件。
- 从工作站绕过 HTTP 代理直接请求 `http://reports.yingevideo.top/.well-known/acme-challenge/yingge-probe` 得到空响应，同一随机内容未到达 nginx。由此无法确认公网 80 转发，暂不能执行 HTTP-01 签证书。
- 测试后删除探针，停止并禁用 nginx；服务器没有云端账号服务、生产数据库、证书、正式账号或公网 5080/5432。PostgreSQL 14 仍仅监听 `127.0.0.1:5432`。
- 发布包已在本机生成，暂存在服务器 `/tmp/yingge-cloud-release.tar.gz` 与 `/tmp/yingge-cloud-install`；不含口令。

下一步需要在云服务商的安全组/端口映射中确认公网 TCP 80、443 转发到这台 Ubuntu 的 80、443。SSH 公网 43602 映射到服务器 22，80/443 可能也需要单独映射。若无法开放公网 80，可改 DNS-01 签证书，但公网 443 仍必须可达。
