# yingvideo.top 域名检查（2026-11-04）

用户截图中的 `@`、`www`、`reports` 三条 A 记录均指向 `43.226.58.187`，主机名和记录值的写法没有明显错误。

但 Google Public DNS 和 Cloudflare DNS over HTTPS 均对 `yingvideo.top` 返回 `Status=3`（NXDOMAIN），`reports.yingvideo.top` 同样如此；Google 对根域的 NS 查询也返回 NXDOMAIN。当前电脑普通解析得到的 `198.18.8.x` 属本地代理/虚拟解析结果，不能当成域名已在公网生效的证据。

下一步先在域名注册商核对：域名是否已经注册并处于有效状态、注册商处配置的权威 DNS 是否与截图中的 DNS 托管平台一致。若刚完成注册或委派，等待公共解析器的负缓存到期后重新查。只有公共 DNS 返回 `43.226.58.187`，再配置并验收 HTTPS 证书及 `reports` 上传服务。

端口探测显示服务器 IP 的 80、443、43602 TCP 可连接，但直连 IP 并指定 `yingvideo.top` 主机名时，443 TLS 握手失败、80 返回空响应；开放端口不等于网站或加密日志上传服务已经可用。本次仅做只读检查，未改服务器、DNS 或客户端上传配置。
