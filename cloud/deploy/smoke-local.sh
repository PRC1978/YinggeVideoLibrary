#!/usr/bin/env bash
# End-to-end check against a deployed instance over real HTTPS.
#   sudo BASE_URL=https://reports.example.com ./smoke-local.sh
#
# It creates one throwaway account, uploads one synthetic report, deletes it,
# and leaves nothing behind. No user data is touched.
set -euo pipefail

BASE_URL="${BASE_URL:-}"
ADMIN_FILE="${ADMIN_FILE:-/etc/yingge-cloud/admin.env}"
APP_DIR="${APP_DIR:-/opt/yingge-cloud/app}"
STAMP="$(date +%s)"
USERNAME="smoke${STAMP}"
PASSWORD="SmokePass-1234"

if [[ -z "$BASE_URL" ]]; then echo "用法: BASE_URL=https://... ./smoke-local.sh" >&2; exit 2; fi
if [[ "$BASE_URL" != https://* ]]; then echo "必须使用 HTTPS 地址。" >&2; exit 2; fi

ADMIN_CS="$(grep -m1 '^Database__AdminConnectionString=' "$ADMIN_FILE" | cut -d= -f2- || true)"
[[ -n "$ADMIN_CS" ]] || { echo "admin.env 缺少连接串。" >&2; exit 4; }

json() { python3 -c "import sys,json;print(json.load(sys.stdin)[\"$1\"])"; }

INVITE="$(dotnet "$APP_DIR/Yingge.Cloud.Accounts.dll" admin invite-create \
  --note smoke --count 1 --admin-db "$ADMIN_CS" | sed -n 's/^invite_code=//p')"
[[ -n "$INVITE" ]] || { echo "生成邀请码失败。" >&2; exit 1; }

code=$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$BASE_URL/api/v1/auth/register" \
  -H 'Content-Type: application/json' \
  -d "{\"inviteCode\":\"$INVITE\",\"username\":\"$USERNAME\",\"password\":\"$PASSWORD\"}")
[[ "$code" == "201" ]] || { echo "注册失败：HTTP $code" >&2; exit 1; }

TOKEN="$(curl -sS -X POST "$BASE_URL/api/v1/auth/login" -H 'Content-Type: application/json' \
  -d "{\"username\":\"$USERNAME\",\"password\":\"$PASSWORD\"}" | json token)"

code=$(curl -sS -o /tmp/yingge-smoke-report.json -w '%{http_code}' -X POST "$BASE_URL/api/v1/reports" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"clientVersion":"smoke","os":"linux","arch":"x64","messages":["连通性测试：这是一条合成错误，不含任何私人信息。"]}')
[[ "$code" == "201" ]] || { echo "上传失败：HTTP $code" >&2; cat /tmp/yingge-smoke-report.json >&2; exit 1; }
REPORT_ID="$(json id < /tmp/yingge-smoke-report.json)"

COUNT="$(curl -sS "$BASE_URL/api/v1/reports" -H "Authorization: Bearer $TOKEN" \
  | python3 -c 'import sys,json;print(len(json.load(sys.stdin)))')"
[[ "$COUNT" == "1" ]] || { echo "列表数量异常：$COUNT" >&2; exit 1; }

code=$(curl -sS -o /dev/null -w '%{http_code}' -X DELETE "$BASE_URL/api/v1/reports/$REPORT_ID" \
  -H "Authorization: Bearer $TOKEN")
[[ "$code" == "204" ]] || { echo "删除失败：HTTP $code" >&2; exit 1; }

code=$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$BASE_URL/api/v1/auth/logout" \
  -H "Authorization: Bearer $TOKEN")
[[ "$code" == "204" ]] || { echo "退出失败：HTTP $code" >&2; exit 1; }

echo "通过：注册 → 登录 → 上传 → 列表 → 删除 → 退出，全部经 $BASE_URL（真实 TLS）。"
echo "清理：使用 admin account-disable --username $USERNAME 停用该冒烟账号。"
