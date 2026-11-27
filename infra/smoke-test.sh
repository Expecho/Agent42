#!/usr/bin/env bash
# Post-deploy smoke (ADR 0151). Pure curl, no credentials: the prod app sits behind Easy Auth and a CI identity would need
# an Entra app + role assignment, so this probes only what is reachable unauthenticated.
#
#   infra/smoke-test.sh <base-url> [--no-auth-wall]
#
# --no-auth-wall skips the probes that need Easy Auth in front (the negative probe and /api/email-in), so the script
# can be dry-run against the DevHost (no auth, no email endpoint).
# Retries up to SMOKE_TIMEOUT_SECONDS (default 300) for the revision rollover (a single replica, two briefly overlapping).
set -euo pipefail

base_url="${1:-}"
auth_wall=1
[ "${2:-}" = "--no-auth-wall" ] && auth_wall=0
if [ -z "$base_url" ]; then
  echo "usage: $0 <base-url> [--no-auth-wall]" >&2
  exit 2
fi
base_url="${base_url%/}"
timeout="${SMOKE_TIMEOUT_SECONDS:-300}"

# Public paths: every entry MUST be excluded from Easy Auth in infra/modules/container-app.bicep (excludedPaths).
# E2eWorkflowTests.SmokePublicPaths_AreExcludedFromEasyAuth reads this array, so keep it one path per line.
PUBLIC_PATHS=(
  "/legal/privacy.html"
  "/shared/themes.css"
)

status() { # method path -> http status (000 on a connection failure)
  curl -s -o /dev/null -w '%{http_code}' --max-time 20 -X "$1" "$base_url$2" || true
}

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }

check_public() {
  local code
  for path in "${PUBLIC_PATHS[@]}"; do
    code=$(status GET "$path")
    [ "$code" = "200" ] && echo "ok   GET $path -> 200" || fail "GET $path -> $code (want 200)"
  done
}

check_auth_wall() {
  local code
  # An unauthenticated API call must be refused or redirected to the login: 200 means Easy Auth is not in front.
  code=$(status GET /api/conversations)
  case "$code" in
    401|302|403) echo "ok   GET /api/conversations -> $code (auth wall holds)" ;;
    *) fail "GET /api/conversations -> $code (want 401/302/403; 200 means Easy Auth is not in front)" ;;
  esac
  # /api/email-in is MapPost and Easy-Auth-excluded: an empty POST reaches the app, which answers 400/401/403/503.
  # 404 means the exclusion or the route is gone; 200 would mean it accepted an unsigned body.
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 -X POST --data '' "$base_url/api/email-in" || true)
  case "$code" in
    400|401|403|503) echo "ok   POST /api/email-in -> $code (excluded, self-enforcing)" ;;
    *) fail "POST /api/email-in -> $code (want 400/401/403/503; never 404 or 200)" ;;
  esac
}

deadline=$(( $(date +%s) + timeout ))
while :; do
  failures=0
  check_public
  [ "$auth_wall" = "1" ] && check_auth_wall
  [ "$failures" = "0" ] && { echo "smoke passed: $base_url"; exit 0; }
  if [ "$(date +%s)" -ge "$deadline" ]; then
    echo "smoke FAILED after ${timeout}s: $base_url"
    exit 1
  fi
  echo "not ready ($failures failing), retrying in 10s..."
  sleep 10
done
