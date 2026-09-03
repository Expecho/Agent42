#!/usr/bin/env bash
#
# Backfill the chat-search index (ADR 0061).
#
# POST /api/search/reindex reindexes at most ChatSearchLimits.ReindexBatchSize (25) conversations
# per call and reports `more` when it stopped on that cap rather than because it ran out. Nothing
# backfills history automatically — not on deploy, not on activation — and the endpoint has no UI,
# so this script is the "user-invocable repair" the best-effort write rule leans on.
#
# It walks the caller's OWN conversations only: the endpoint takes the user id from the Easy Auth
# principal, never from a parameter, so this cannot reindex anyone else's chats.
#
# Reindexing is idempotent — ordinals come from one GetHistoryAsync() projection, so a re-run
# overwrites the same document ids rather than appending. Interrupting and re-running is safe, and
# so is starting over from --skip 0.
#
# Usage:
#   AGENT42_AUTH_COOKIE='<AppServiceAuthSession value>' ./scripts/reindex-chat-search.sh
#   ./scripts/reindex-chat-search.sh --cookie '<value>' --base-url http://localhost:61326
#   ./scripts/reindex-chat-search.sh --dry-run          # preflight only, writes nothing
#
# Getting the cookie: sign in to the app, DevTools -> Application -> Cookies -> copy the VALUE of
# AppServiceAuthSession. It is a session credential — it is never printed or logged by this script,
# and prefer the environment variable over --cookie so it stays out of your shell history.
#
# Runs in Git Bash on Windows as well as Linux/macOS. Needs only curl.

set -euo pipefail

BASE_URL="${AGENT42_BASE_URL:-https://chat.agent42.nl}"
COOKIE="${AGENT42_AUTH_COOKIE:-}"
SKIP=0
# A batch is 25 grain ACTIVATIONS, and OnActivateAsync connects to that user's MCP servers
# sequentially with a 10 s timeout each (ADR 0024) — so a user with several dead servers makes this
# a multi-minute request, not a slow one. Hence a per-request budget in minutes, not seconds.
MAX_TIME=900
MAX_RETRIES=3
DRY_RUN=0

usage() {
  sed -n '3,28p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

while [ $# -gt 0 ]; do
  case "$1" in
    --base-url)  BASE_URL="${2:?--base-url needs a value}"; shift 2 ;;
    --cookie)    COOKIE="${2:?--cookie needs a value}"; shift 2 ;;
    --skip)      SKIP="${2:?--skip needs a value}"; shift 2 ;;
    --max-time)  MAX_TIME="${2:?--max-time needs a value}"; shift 2 ;;
    --retries)   MAX_RETRIES="${2:?--retries needs a value}"; shift 2 ;;
    --dry-run)   DRY_RUN=1; shift ;;
    -h|--help)   usage 0 ;;
    *)           echo "Unknown argument: $1" >&2; usage 1 ;;
  esac
done

BASE_URL="${BASE_URL%/}"

case "$SKIP" in ''|*[!0-9]*) echo "--skip must be a non-negative integer" >&2; exit 2 ;; esac

if [ -z "$COOKIE" ]; then
  echo "No auth cookie. Set AGENT42_AUTH_COOKIE or pass --cookie." >&2
  echo "DevTools -> Application -> Cookies -> AppServiceAuthSession (copy the value)." >&2
  exit 2
fi

# A bare host with no scheme silently becomes a relative curl URL and fails confusingly.
case "$BASE_URL" in
  http://*|https://*) ;;
  *) echo "--base-url must start with http:// or https:// (got '$BASE_URL')" >&2; exit 2 ;;
esac

# --- request helper -----------------------------------------------------------------------------
#
# Sets HTTP_CODE and HTTP_BODY. Deliberately does NOT follow redirects: Easy Auth answers an
# unauthenticated request with a 302 to login.microsoftonline.com, and following it would return a
# 200 full of HTML — the single most confusing way this can fail. Seeing the 302 is the diagnosis.
HTTP_CODE=""
HTTP_BODY=""
request() {
  local method="$1" url="$2" response
  response=$(curl -sS -X "$method" "$url" \
    --max-time "$MAX_TIME" \
    -H 'Accept: application/json' \
    -H "Cookie: AppServiceAuthSession=${COOKIE}" \
    -w '\n%{http_code}') || return 1

  HTTP_CODE="${response##*$'\n'}"
  HTTP_BODY="${response%$'\n'*}"
}

# Reads one integer or boolean field out of the small flat JSON response. Tolerates whitespace so a
# future JsonSerializerOptions change to indented output does not silently break the loop.
json_field() {
  printf '%s' "$2" | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*[^,}]+" | head -1 |
    sed -E "s/\"$1\"[[:space:]]*:[[:space:]]*//" | tr -d '" '
}

explain_failure() {
  case "$HTTP_CODE" in
    302|401|403)
      echo "  -> Not authenticated. The AppServiceAuthSession cookie is missing or expired;" >&2
      echo "     sign in again and copy a fresh one." >&2 ;;
    409)
      echo "  -> Chat search is not configured on this deployment" >&2
      echo "     (Cosmos__ChatMessagesContainerName is unset, so the in-memory twin is registered)." >&2 ;;
    404)
      echo "  -> No such route. Is --base-url right, and is a build with ADR 0061 deployed?" >&2 ;;
  esac
}

# --- preflight ----------------------------------------------------------------------------------
#
# A READ, so it writes nothing, but it proves the cookie and that the index is wired before the
# first batch commits anything. Worth doing every run: an expired cookie is the likeliest failure
# and its 302 is indistinguishable from success once you stop reading status codes — which is
# exactly how the composite-index bug hid.
echo "Target:    $BASE_URL"
echo "Preflight: GET /api/search"

if ! request GET "$BASE_URL/api/search?q=preflight"; then
  echo "Preflight request failed (network, DNS or timeout)." >&2
  exit 1
fi

if [ "$HTTP_CODE" != "200" ]; then
  echo "Preflight failed with HTTP $HTTP_CODE" >&2
  explain_failure
  exit 1
fi

echo "           OK (HTTP 200, authenticated, search index reachable)"

if [ "$DRY_RUN" -eq 1 ]; then
  echo "Dry run — stopping before any write. Re-run without --dry-run to backfill."
  exit 0
fi

# --- backfill loop ------------------------------------------------------------------------------
total_conversations=0
total_turns=0
batch_number=0

echo "Backfill:  POST /api/search/reindex (batches of up to 25, starting at skip=$SKIP)"

while :; do
  batch_number=$((batch_number + 1))
  attempt=1
  ok=0

  # Retry in place, never skipping ahead: the operation is idempotent, so repeating a batch costs
  # duplicate upserts, while advancing past a failed one leaves a silent hole in the corpus —
  # precisely the invisible gap the backfill exists to close.
  while [ "$attempt" -le "$MAX_RETRIES" ]; do
    if request POST "$BASE_URL/api/search/reindex?skip=$SKIP" && [ "$HTTP_CODE" = "200" ]; then
      ok=1
      break
    fi

    if [ "$HTTP_CODE" = "302" ] || [ "$HTTP_CODE" = "401" ] || [ "$HTTP_CODE" = "403" ] ||
       [ "$HTTP_CODE" = "409" ] || [ "$HTTP_CODE" = "404" ]; then
      break  # not transient — retrying cannot help
    fi

    echo "  batch $batch_number (skip=$SKIP): HTTP ${HTTP_CODE:-error}, retry $attempt/$MAX_RETRIES" >&2
    sleep $((attempt * 5))
    attempt=$((attempt + 1))
  done

  if [ "$ok" -ne 1 ]; then
    echo "Batch $batch_number failed at skip=$SKIP (HTTP ${HTTP_CODE:-error})." >&2
    explain_failure
    echo "Resume once fixed with: --skip $SKIP" >&2
    exit 1
  fi

  conversations=$(json_field conversations "$HTTP_BODY")
  turns=$(json_field turns "$HTTP_BODY")
  more=$(json_field more "$HTTP_BODY")

  case "$conversations" in ''|*[!0-9]*)
    echo "Could not parse the reindex response at skip=$SKIP." >&2
    echo "Body: $HTTP_BODY" >&2
    exit 1 ;;
  esac

  total_conversations=$((total_conversations + conversations))
  total_turns=$((total_turns + turns))

  printf '  batch %-3d skip=%-5d conversations=%-3d turns=%-6d more=%s\n' \
    "$batch_number" "$SKIP" "$conversations" "$turns" "$more"

  # Advance by what the server actually processed, not by the batch cap: the final batch is short,
  # and assuming 25 would step past conversations that were never indexed.
  SKIP=$((SKIP + conversations))

  [ "$more" = "true" ] || break

  # `more` is computed from the batch length, so a zero-length batch with more=true cannot happen —
  # but it would loop forever if it ever did, and a backfill is not the place to find out.
  if [ "$conversations" -eq 0 ]; then
    echo "Server reported more work but returned no conversations; stopping at skip=$SKIP." >&2
    exit 1
  fi
done

echo
echo "Done. Reindexed $total_conversations conversation(s), $total_turns turn(s) in $batch_number batch(es)."
echo "Cosmos reindexes in the background, so results may lag a little behind this."
