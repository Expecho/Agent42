#!/usr/bin/env bash
# One-time Azure + Entra setup for deploying Agent 42 to your own subscription (ADR 0128).
#
# WHO RUNS IT: you, the operator, by hand. It creates role assignments and Entra objects, so it is never run by CI and
# never by an AI agent (hard rule 5 in CLAUDE.md). Read it first.
#
# PERMISSIONS: Owner (or User Access Administrator + Contributor) on the subscription, plus the right to create app
# registrations in the tenant. The deploy service principal this script creates gets Contributor and Role Based Access
# Control Administrator on the resource group only, and NO Microsoft Graph permissions. That is why the Entra part is a
# script and not Bicep: the Graph Bicep extension would need tenant-admin consent for the deploy identity.
#
# TWO PHASES (the Easy Auth redirect URI needs the app's host, which only exists after the first deploy):
#   bootstrap.sh init     --resource-group <rg> --location <location> [--app-name agent42]
#   ... set the printed GitHub secrets and variables, push to main, wait for the Deploy run ...
#   bootstrap.sh redirect --app-id <AUTH_CLIENT_ID> --host <fqdn or custom domain>
#
# `init` is idempotent where az allows. Client secrets are always freshly created, so re-running it prints new ones
# (the previous ones stay valid until they expire).
set -euo pipefail

# Git Bash on Windows rewrites an argument that starts with "/" into a Windows path, which ARM rejects as
# MissingSubscription. Role scopes below are passed without the leading slash as well; this covers the rest.
export MSYS_NO_PATHCONV=1

usage() {
  sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-1}"
}

die() { echo "error: $*" >&2; exit 1; }

require() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but not installed"; }

cmd_init() {
  local rg="" location="" app="agent42"
  while [ $# -gt 0 ]; do
    case "$1" in
      --resource-group) rg="$2"; shift 2 ;;
      --location) location="$2"; shift 2 ;;
      --app-name) app="$2"; shift 2 ;;
      *) die "unknown argument: $1" ;;
    esac
  done
  [ -n "$rg" ] || die "--resource-group is required"
  [ -n "$location" ] || die "--location is required"

  require az
  local subscription tenant
  subscription=$(az account show --query id -o tsv)
  tenant=$(az account show --query tenantId -o tsv)
  echo "Subscription $subscription, tenant $tenant" >&2

  echo "Creating resource group $rg in $location ..." >&2
  az group create --name "$rg" --location "$location" --output none

  # The RG-scoped deploy identity cannot register providers, and a fresh subscription fails the first deploy with a
  # confusing error otherwise.
  echo "Registering resource providers ..." >&2
  local provider
  for provider in Microsoft.App Microsoft.ContainerRegistry Microsoft.OperationalInsights \
                  Microsoft.DocumentDB Microsoft.Storage Microsoft.Authorization; do
    az provider register --namespace "$provider" --wait --output none
  done

  # --- Easy Auth app registration -------------------------------------------------------------------------------
  # Single tenant, web platform, ID-token issuance on (without it the first sign-in fails), assignment required.
  local auth_app_id
  auth_app_id=$(az ad app list --display-name "$app-easyauth" --query "[0].appId" -o tsv)
  if [ -z "$auth_app_id" ]; then
    echo "Creating the Easy Auth app registration $app-easyauth ..." >&2
    auth_app_id=$(az ad app create --display-name "$app-easyauth" --sign-in-audience AzureADMyOrg \
      --enable-id-token-issuance true --query appId -o tsv)
  else
    echo "Reusing the Easy Auth app registration $app-easyauth ($auth_app_id)" >&2
    az ad app update --id "$auth_app_id" --enable-id-token-issuance true
  fi
  if [ -z "$(az ad sp list --filter "appId eq '$auth_app_id'" --query "[0].id" -o tsv)" ]; then
    az ad sp create --id "$auth_app_id" --output none
  fi
  az ad sp update --id "$auth_app_id" --set appRoleAssignmentRequired=true
  local aad_client_secret
  aad_client_secret=$(az ad app credential reset --id "$auth_app_id" --append \
    --display-name "easyauth-$(date +%Y%m%d)" --query password -o tsv)

  # --- Deploy service principal ---------------------------------------------------------------------------------
  # A client secret, not OIDC: azure/login in deploy.yml takes the full credentials JSON (see gotchas).
  local deploy_app_id deploy_secret
  deploy_app_id=$(az ad app list --display-name "$app-deploy" --query "[0].appId" -o tsv)
  if [ -z "$deploy_app_id" ]; then
    echo "Creating the deploy service principal $app-deploy ..." >&2
    deploy_app_id=$(az ad app create --display-name "$app-deploy" --query appId -o tsv)
  fi
  if [ -z "$(az ad sp list --filter "appId eq '$deploy_app_id'" --query "[0].id" -o tsv)" ]; then
    az ad sp create --id "$deploy_app_id" --output none
  fi
  deploy_secret=$(az ad app credential reset --id "$deploy_app_id" --append \
    --display-name "deploy-$(date +%Y%m%d)" --query password -o tsv)

  # Contributor deploys the resources. Role Based Access Control Administrator lets Bicep create the app identity's
  # role assignments (storage-role-assignment.bicep); a plain Contributor cannot. The scope has no leading slash on
  # purpose (MSYS path conversion).
  local scope="subscriptions/$subscription/resourceGroups/$rg" role
  for role in "Contributor" "Role Based Access Control Administrator"; do
    echo "Granting '$role' on $rg to the deploy service principal ..." >&2
    az role assignment create --assignee "$deploy_app_id" --role "$role" --scope "$scope" --output none
  done

  local searxng_secret
  searxng_secret=$(head -c 32 /dev/urandom | base64 | tr -d '/+=\n' | head -c 40)

  cat <<EOF

# ---------------------------------------------------------------------------------------------------------------
# Done. Run these in your fork's clone (needs the GitHub CLI, 'gh auth login'):
#
# GitHub secrets
gh secret set AZURE_CLIENT_ID --body '$deploy_app_id'
gh secret set AZURE_CLIENT_SECRET --body '$deploy_secret'
gh secret set AZURE_SUBSCRIPTION_ID --body '$subscription'
gh secret set AZURE_TENANT_ID --body '$tenant'
gh secret set AAD_CLIENT_SECRET --body '$aad_client_secret'
gh secret set SEARXNG_SECRET --body '$searxng_secret'

# GitHub variables
gh variable set AZURE_RESOURCE_GROUP --body '$rg'
gh variable set AUTH_CLIENT_ID --body '$auth_app_id'
# ---------------------------------------------------------------------------------------------------------------
# Next: push to main and wait for the Deploy run, then
#   infra/bootstrap.sh redirect --app-id $auth_app_id --host <the app's fqdn or your custom domain>
#
# NOTE: this script does NOT assign any users. Sign-in requires assignment, so assign yourself (and anyone else) to
# the enterprise application '$app-easyauth' (Entra admin center > Enterprise applications > Users and groups).
# The optional secrets and variables (Google Maps, Hindsight, VAPID, custom domain, owners ...) are in
# docs/features/azure-deployment.md.
# ---------------------------------------------------------------------------------------------------------------
EOF
}

cmd_redirect() {
  local app_id="" host=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --app-id) app_id="$2"; shift 2 ;;
      --host) host="$2"; shift 2 ;;
      *) die "unknown argument: $1" ;;
    esac
  done
  [ -n "$app_id" ] || die "--app-id is required"
  [ -n "$host" ] || die "--host is required"
  require az

  local uri="https://$host/.auth/login/aad/callback" existing
  existing=$(az ad app show --id "$app_id" --query "web.redirectUris" -o tsv)
  if printf '%s\n' "$existing" | grep -Fxq "$uri"; then
    echo "Redirect URI already registered: $uri"
    return 0
  fi
  # Existing URIs are kept: the update replaces the whole list, so pass them back with the new one.
  local uris=()
  while IFS= read -r line; do
    [ -z "$line" ] || uris+=("$line")
  done <<<"$existing"
  uris+=("$uri")
  az ad app update --id "$app_id" --web-redirect-uris "${uris[@]}"
  echo "Added redirect URI: $uri"
}

[ $# -ge 1 ] || usage 1
sub="$1"
shift
case "$sub" in
  init) cmd_init "$@" ;;
  redirect) cmd_redirect "$@" ;;
  -h | --help | help) usage 0 ;;
  *) usage 1 ;;
esac
