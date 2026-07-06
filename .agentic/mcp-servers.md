# MCP server catalog

Known MCP servers matched against tooling detected (or confirmed) during onboarding.
Onboarding **never writes `.mcp.json`, tokens, or any server config into the repo** —
it presents the matching rows from this table and the user runs the install command
themselves. Prefer `--scope user`: config lands in `~/.claude.json`, private to the
user and outside the repo. Secrets live in env vars or OAuth flows (`/mcp` to
authenticate), never in checked-in files. If the team later decides to share a
server via a project `.mcp.json`, that is a deliberate team decision (record it),
and still never includes credentials inline.

| Detected signal | Server | Install (user runs this) | Auth |
|-----------------|--------|--------------------------|------|
| `git remote` on github.com, `.github/workflows/` | GitHub MCP | `claude mcp add --transport http --scope user github https://api.githubcopilot.com/mcp/ --header "Authorization: Bearer $GITHUB_PAT"` | PAT in env var |
| `git remote` on a GitLab instance, `.gitlab-ci.yml` | GitLab MCP (official) | `claude mcp add --transport http --scope user gitlab https://<instance>/mcp` then `/mcp` to authenticate | OAuth |
| `git remote` on dev.azure.com, `azure-pipelines.yml`, `AB#123` refs | Azure DevOps MCP (Microsoft) | `claude mcp add --scope user azure-devops -- npx -y @azure-devops/mcp <org-name>` | PAT env var |
| `PROJ-123` ticket keys in branches/commits | Atlassian MCP (remote; needed by the `jira-implement` skill) | `claude mcp add --transport http --scope user atlassian <endpoint>` then `/mcp` — take the current endpoint from Atlassian's docs (the old SSE endpoint was retired mid-2026) | OAuth / API token |
| Azure resources in Bicep/Terraform, App Insights connection strings, AKS manifests/Helm charts | Azure MCP (covers Azure Monitor, Log Analytics, AKS) | `claude mcp add --scope user azure -- npx -y @microsoft/mcp-azure` | Azure CLI credentials (`az login`) |
| Grafana dashboards/provisioning in repo | Grafana MCP | `claude mcp add --scope user grafana -- uvx mcp-grafana` | `GRAFANA_URL` + `GRAFANA_SERVICE_ACCOUNT_TOKEN` env vars |
| Sentry DSN in config | Sentry MCP | `claude mcp add --transport http --scope user sentry https://mcp.sentry.dev/mcp` then `/mcp` | OAuth |

Commands verified against vendor docs: 2026-07-06. Before presenting a row, sanity-check
anything that smells stale (a `--help` on the CLI form, or the vendor doc for remote
endpoints) — vendors move MCP endpoints. For tooling not in this table, search the
official registry at https://registry.modelcontextprotocol.io/ instead of guessing a
package name; add rows here once verified.
