#!/usr/bin/env bash
# Read-only preconditions for issuing a certificate for reports.yingevideo.top.
# It changes nothing: no package install, no listener, no certificate request.
set -uo pipefail
cd /tmp || exit 9

DOMAIN="${DOMAIN:-reports.yingevideo.top}"
EXPECTED_IP="${EXPECTED_IP:-43.226.58.187}"

echo "== DNS =="
if command -v dig >/dev/null; then
  A="$(dig +short A "$DOMAIN" | tr '\n' ' ')"
else
  A="$(getent ahostsv4 "$DOMAIN" | awk '{print $1}' | sort -u | tr '\n' ' ')"
fi
echo "A record: ${A:-<none>}  (expected ${EXPECTED_IP})"
case " $A " in *" $EXPECTED_IP "*) echo "DNS: PASS";; *) echo "DNS: FAIL";; esac

echo "== local listeners =="
ss -ltn | awk 'NR==1 || /:80 |:443 |:5080 /' | sed 's/^/  /'
BOUND80="$(ss -ltn | awk '$4 ~ /:80$/ {print $4}')"
BOUND443="$(ss -ltn | awk '$4 ~ /:443$/ {print $4}')"
echo "port80: ${BOUND80:-free}"
echo "port443: ${BOUND443:-free}"

echo "== tools =="
for tool in nginx certbot; do
  if command -v "$tool" >/dev/null; then
    echo "$tool: $(command -v "$tool")"
  else
    echo "$tool: MISSING"
  fi
done

echo "== existing certificates =="
if command -v certbot >/dev/null; then
  certbot certificates 2>/dev/null | sed -n '1,20p'
else
  echo "certbot not installed; nothing to list"
fi

echo "== NAT rules (read-only) =="
iptables -t nat -S 2>/dev/null | grep -E 'dport (80|443)' || echo "no local DNAT for 80/443"

echo "== interpretation =="
cat <<'EOF'
  DNS PASS + port 80 free on this host  => HTTP-01 可以尝试（前提：公网 80 确实转发到本机）
  DNS PASS + port 80 被其它站点占用     => 用 nginx 共存配置，让 /.well-known/acme-challenge 走本机
  公网 80 未转发到本机                  => HTTP-01 不可用，改用 DNS-01（DNSPod API 或手工 TXT）
EOF
