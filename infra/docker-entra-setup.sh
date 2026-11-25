#!/usr/bin/env bash
# One-time Azure + Entra setup for running Agent 42 in Docker with Entra sign-in and Azure storage (ADR 0145,
# docs/setup/docker-entra.md, compose.azure.yaml).
#
# WHO RUNS IT: you, by hand. It creates app registrations and role assignments, so it is never run by CI and never by an AI
# agent (hard rule 5 in CLAUDE.md). Read it first.
#
# PERMISSIONS: the right to create app registrations in the tenant, and Owner (or User Access Administrator) on the Cosmos
# DB account and the Storage account (the Cosmos data-plane role also needs rights on the account).
#
# COMMANDS:
#   docker-entra-setup.sh init  --resource-group <rg> --cosmos-account <name> --storage-account <name>
#                               [--base-url http://localhost:8080] [--app-name agent42-docker]
#       Creates the sign-in app registration (redirect URIs, "assignment required", you assigned), a service principal for
#       the container, grants it the Cosmos and Blob roles, and prints the .env block for compose.azure.yaml.
#   docker-entra-setup.sh roles --resource-group <rg> --cosmos-account <name> --storage-account <name>
#                               --principal-object-id <service principal object id>
#       Only the two role assignments, for a service principal you already have.
#   docker-entra-setup.sh redirect --app-id <AUTH_CLIENT_ID> --base-url <https://your.host>
#       Adds another address (sign-in and sign-out redirect URIs) to the sign-in app registration.
#   docker-entra-setup.sh assign --app-id <AUTH_CLIENT_ID> --user <user principal name or object id>
#       Lets one more person sign in (the app is invite-only).
#
# Client secrets are always freshly created, so re-running `init` prints new ones (older ones stay valid until they expire).
# The printed block contains secrets: put it in .env (git-ignored) and do not paste it anywhere else.
set -euo pipefail

# Git Bash on Windows rewrites an argument that starts with "/" into a Windows path, which ARM rejects.
export MSYS_NO_PATHCONV=1

COSMOS_DATA_CONTRIBUTOR="00000000-0000-0000-0000-000000000002" # built-in "Cosmos DB Built-in Data Contributor"
DEFAULT_APP_ROLE="00000000-0000-0000-0000-000000000000"        # the default access role on an app with no app roles

usage() {
  sed -n '2,29p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-1}"
}

die() { echo "error: $*" >&2; exit 1; }

require() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but not installed"; }

need() { [ -n "$2" ] || die "missing $1"; }

ensure_sp() { # app id -> prints the service principal object id, creating it when absent
  local id
  id=$(az ad sp show --id "$1" --query id -o tsv 2>/dev/null || true)
  [ -n "$id" ] || id=$(az ad sp create --id "$1" --query id -o tsv)
  echo "$id"
}

grant_roles() { # rg cosmos storage principal-object-id
  local rg="$1" cosmos="$2" storage="$3" principal="$4" scope
  echo "Cosmos DB data plane ($cosmos)..." >&2
  az cosmosdb sql role assignment create --account-name "$cosmos" --resource-group "$rg" --scope "/" \
    --principal-id "$principal" --role-definition-id "$COSMOS_DATA_CONTRIBUTOR" -o none \
    || echo "  (already assigned, or failed: check the message above)" >&2
  echo "Blob Storage ($storage)..." >&2
  scope=$(az storage account show --name "$storage" --resource-group "$rg" --query id -o tsv)
  az role assignment create --assignee-object-id "$principal" --assignee-principal-type ServicePrincipal \
    --role "Storage Blob Data Contributor" --scope "$scope" -o none \
    || echo "  (already assigned, or failed: check the message above)" >&2
}

cmd_init() {
  local rg="" cosmos="" storage="" base="http://localhost:8080" app="agent42-docker"
  while [ $# -gt 0 ]; do
    case "$1" in
      --resource-group) rg="$2"; shift 2 ;;
      --cosmos-account) cosmos="$2"; shift 2 ;;
      --storage-account) storage="$2"; shift 2 ;;
      --base-url) base="${2%/}"; shift 2 ;;
      --app-name) app="$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  need --resource-group "$rg"; need --cosmos-account "$cosmos"; need --storage-account "$storage"
  require az
  az account show -o none 2>/dev/null || die "run 'az login' first"

  local tenant user_id
  tenant=$(az account show --query tenantId -o tsv)
  user_id=$(az ad signed-in-user show --query id -o tsv)

  echo "Sign-in app registration '$app'..." >&2
  local auth_id auth_sp auth_secret
  auth_id=$(az ad app list --display-name "$app" --query "[0].appId" -o tsv)
  if [ -z "$auth_id" ]; then
    auth_id=$(az ad app create --display-name "$app" --sign-in-audience AzureADMyOrg \
      --web-redirect-uris "$base/signin-oidc" "$base/signout-callback-oidc" --query appId -o tsv)
  fi
  auth_secret=$(az ad app credential reset --id "$auth_id" --append --display-name docker --years 1 --query password -o tsv)
  auth_sp=$(ensure_sp "$auth_id")
  # Invite-only (ADR 0121): only assigned people can sign in. Check: az ad sp show --id <id> --query appRoleAssignmentRequired
  az ad sp update --id "$auth_sp" --set appRoleAssignmentRequired=true
  az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$auth_sp/appRoleAssignedTo" \
    --body "{\"principalId\":\"$user_id\",\"resourceId\":\"$auth_sp\",\"appRoleId\":\"$DEFAULT_APP_ROLE\"}" -o none 2>/dev/null \
    || echo "  (you are probably already assigned)" >&2

  echo "Container service principal '$app-storage'..." >&2
  local sp_app sp_obj sp_secret
  sp_app=$(az ad app list --display-name "$app-storage" --query "[0].appId" -o tsv)
  [ -n "$sp_app" ] || sp_app=$(az ad app create --display-name "$app-storage" --sign-in-audience AzureADMyOrg --query appId -o tsv)
  sp_secret=$(az ad app credential reset --id "$sp_app" --append --display-name docker --years 1 --query password -o tsv)
  sp_obj=$(ensure_sp "$sp_app")

  grant_roles "$rg" "$cosmos" "$storage" "$sp_obj"

  local cosmos_endpoint blob_endpoint
  cosmos_endpoint=$(az cosmosdb show --name "$cosmos" --resource-group "$rg" --query documentEndpoint -o tsv)
  blob_endpoint=$(az storage account show --name "$storage" --resource-group "$rg" --query primaryEndpoints.blob -o tsv)

  cat <<EOF

# ---- paste into .env (contains secrets; role assignments can take a few minutes to take effect) ----
AZURE_TENANT_ID=$tenant
AZURE_CLIENT_ID=$sp_app
AZURE_CLIENT_SECRET=$sp_secret
AUTH_CLIENT_ID=$auth_id
AUTH_CLIENT_SECRET=$auth_secret
COSMOS_ACCOUNT_ENDPOINT=$cosmos_endpoint
BLOB_ENDPOINT=${blob_endpoint%/}
OWNER_OBJECT_ID=$user_id
EOF
  [ "$base" = "http://localhost:8080" ] || echo "AUTH_PUBLIC_BASE_URL=$base"
  echo "" >&2
  echo "Next: set SEARXNG_SECRET in .env, then: docker compose -f compose.azure.yaml up -d" >&2
}

cmd_roles() {
  local rg="" cosmos="" storage="" principal=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --resource-group) rg="$2"; shift 2 ;;
      --cosmos-account) cosmos="$2"; shift 2 ;;
      --storage-account) storage="$2"; shift 2 ;;
      --principal-object-id) principal="$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  need --resource-group "$rg"; need --cosmos-account "$cosmos"; need --storage-account "$storage"
  need --principal-object-id "$principal"
  require az
  grant_roles "$rg" "$cosmos" "$storage" "$principal"
}

cmd_redirect() {
  local app_id="" base=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --app-id) app_id="$2"; shift 2 ;;
      --base-url) base="${2%/}"; shift 2 ;;
      *) usage ;;
    esac
  done
  need --app-id "$app_id"; need --base-url "$base"
  require az
  local current
  current=$(az ad app show --id "$app_id" --query "web.redirectUris" -o tsv | tr -d '\r')
  # shellcheck disable=SC2086
  az ad app update --id "$app_id" --web-redirect-uris $current "$base/signin-oidc" "$base/signout-callback-oidc"
  echo "Registered $base/signin-oidc and $base/signout-callback-oidc. Set AUTH_PUBLIC_BASE_URL=$base if a proxy rewrites Host." >&2
}

cmd_assign() {
  local app_id="" user=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --app-id) app_id="$2"; shift 2 ;;
      --user) user="$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  need --app-id "$app_id"; need --user "$user"
  require az
  local sp user_id
  sp=$(ensure_sp "$app_id")
  user_id=$(az ad user show --id "$user" --query id -o tsv)
  az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$sp/appRoleAssignedTo" \
    --body "{\"principalId\":\"$user_id\",\"resourceId\":\"$sp\",\"appRoleId\":\"$DEFAULT_APP_ROLE\"}" -o none
  echo "Assigned $user. Their object id is $user_id (put it in Access__Owners only if they should see the diagnostics)." >&2
}

case "${1:-}" in
  init) shift; cmd_init "$@" ;;
  roles) shift; cmd_roles "$@" ;;
  redirect) shift; cmd_redirect "$@" ;;
  assign) shift; cmd_assign "$@" ;;
  -h|--help|help) usage 0 ;;
  *) usage ;;
esac
