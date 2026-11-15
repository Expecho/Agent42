# Deploying to your own Azure subscription

> New to this? Start with the step-by-step guide, [Deploy to Azure](../setup/azure.md). This page is the reference for
> every secret and variable.

The Azure setup is tenant-portable: a fork runs one bootstrap script, sets the GitHub secrets and
variables it prints, and pushes. Without an Azure account, use [self-hosting](self-hosting.md) instead.

## The fork flow

1. **Bootstrap** (you run it, never CI or an agent: it creates role assignments and Entra objects). It needs Owner (or
   User Access Administrator + Contributor) on the subscription and the right to create app registrations:

   ```bash
   az login
   infra/bootstrap.sh init --resource-group rg-myagent --location westeurope
   ```

   It creates the resource group, registers the resource providers, creates the Easy Auth app registration (single
   tenant, ID tokens on, assignment required) and a deploy service principal (Contributor + Role Based Access Control
   Administrator on that resource group only, no Graph permissions), and prints `gh secret set` / `gh variable set`
   commands. Run them in your fork.
2. **Push to `main`.** `.github/workflows/deploy.yml` runs `preflight`, then deploys `infra/shared.bicep` (creates
   the registry, a Log Analytics workspace and a Container Apps environment when no existing names are given), builds
   and pushes the images, and deploys `infra/main.bicep`.
3. **Register the sign-in redirect** once the first deploy has an address (the host is unknown before that):

   ```bash
   infra/bootstrap.sh redirect --app-id <AUTH_CLIENT_ID> --host <fqdn from the deploy, or your custom domain>
   ```
4. **Assign users** to the enterprise application `agent42-easyauth` (Entra admin center, Enterprise applications,
   Users and groups). Sign-in requires assignment and the script assigns nobody.

## Secrets

| Secret | Required | Missing means |
|---|---|---|
| `AZURE_CLIENT_ID` | yes | Login fails. Printed by `bootstrap.sh`. |
| `AZURE_CLIENT_SECRET` | yes | Login fails. |
| `AZURE_SUBSCRIPTION_ID` | yes | Login fails. |
| `AZURE_TENANT_ID` | yes | Login fails. |
| `AAD_CLIENT_SECRET` | yes | Easy Auth cannot sign anyone in. Rotate with `az ad app credential reset --id <AUTH_CLIENT_ID>`. |
| `SEARXNG_SECRET` | yes | **App down**: the sidecar crash-loops and the revision never turns healthy (`@minLength` fails the deploy first). |
| `GOOGLE_MAPS_API_KEY` | no | Location returns coordinates without an address. |
| `GH_OAUTH_CLIENT_SECRET` | no | "Sign in with GitHub" is hidden. |
| `HINDSIGHT_API_KEY` | no | No long-term memory, and no calls are made. Setting it turns on always-on egress. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | no | No App Insights export; OTLP only. |
| `VAPID_PRIVATE_KEY` | no | Web Push is hidden. |
| `MAILGUN_SIGNING_KEY` | no | `/api/email-in` answers 503. |

## Variables

An **absent GitHub variable expands to an empty string**, and for the optional Bicep params empty means "off".
That is why the `preflight` job exists: it fails the run, naming the variable, before anything is created or pushed.

| Variable | Required | Meaning, and what a missing one does |
|---|---|---|
| `AZURE_RESOURCE_GROUP` | yes | Resource group the app deploys into. Preflight fails when empty. |
| `AUTH_CLIENT_ID` | yes | Client id of the Easy Auth app registration. Preflight fails when empty. |
| `ACR_NAME` | with the two below | Existing container registry to reuse. |
| `CAE_NAME` | with the two around it | Existing Container Apps environment to reuse. |
| `CAE_RESOURCE_GROUP` | with the two above | Resource group of that environment. |
| `CUSTOM_DOMAIN_NAME` | no | Custom domain bound to the app. Set it together with the certificate name. |
| `CUSTOM_DOMAIN_CERTIFICATE_NAME` | with the domain | Managed certificate backing the domain. |
| `COSMOS_LOCATION` | no | Region of the Cosmos account (default: the resource group's location). Immutable once created. |
| `OTLP_ENDPOINT` | no | OpenTelemetry collector. Empty disables OTLP export. |
| `AZURE_OPENAI_ENDPOINT` | no | Dormant Foundry-provider inheritance source. |
| `VAPID_PUBLIC_KEY` | no | Overrides the committed `Push:VapidPublicKey`. |
| `VAPID_SUBJECT` | no | `Push:VapidSubject` (a `mailto:` or `https:` contact URL). Ships empty. |
| `GH_OAUTH_CLIENT_ID` | no | Overrides the committed `GitHub:OAuth:ClientId`. Not `GITHUB_*`: GitHub reserves that prefix. |
| `EMAIL_INBOUND_DOMAIN` | no | `Email:InboundDomain`. Ships empty, so inbound email is off until set. |
| `ACCESS_OWNERS` | no | Comma-separated Entra object ids that become the owner tier. Ships empty, so nobody is an owner until set. |
| `IDENTITY_ALIASES` | no | JSON object, additional Entra object id to the object id that owns the data. Ships empty. An entry is an authorization grant. |

**Shared infra is all or nothing.** `ACR_NAME`, `CAE_NAME` and `CAE_RESOURCE_GROUP` are either all set (reuse, nothing
is created) or all empty (create). Preflight rejects a mix, so create mode is never a side effect of one missing
variable.

**What preflight checks.** The required variables; the shared-infra trio; the domain pair; that the custom domain
bound to the existing app is the one the variable names (a deploy that omits it unbinds it); that the app is not
being moved to another environment; that the Cosmos account's region matches `COSMOS_LOCATION`; and that
`ACCESS_OWNERS` does not contain `local-dev`.

## Custom domain

Binding stays a one-time CLI step, because Bicep cannot create the managed certificate:

```bash
az containerapp hostname add  -n ca-agent42-backend -g <rg> --hostname <domain>
az containerapp hostname bind -n ca-agent42-backend -g <rg> --hostname <domain> --environment <cae> --validation-method CNAME
az containerapp env certificate list -n <cae> -g <cae resource group>   # note the certificate name
```

Then set `CUSTOM_DOMAIN_NAME` and `CUSTOM_DOMAIN_CERTIFICATE_NAME` together. If the certificate is ever recreated,
update the name or the next deploy unbinds the domain (preflight catches the domain, not a stale certificate name).

## App-config overrides

The committed `src/Agent42.Backend/appsettings.json` carries the VAPID public key and the GitHub OAuth client id. The
VAPID subject, inbound email domain, `Access:Owners` and `Identity:Aliases` ship **empty**, because they identify a
person or a deployment. Set the variables above and they arrive as environment variables, only when non-empty.

- **VAPID:** the public key must be the pair of `VAPID_PRIVATE_KEY`, or push breaks. Generate a pair with any Web Push
  tool (for example `npx web-push generate-vapid-keys`), set the private half as the secret and the public half plus a
  `mailto:` subject as variables.
- **Owners:** `ACCESS_OWNERS` is split on commas and emitted as `Access__Owners__0`, `__1` and so on. With the list
  empty nobody is an owner, so `/dashboard`, `/logs`, `/api/diagnostics` and `/api/config-health` answer 404 to
  everyone. Never list `local-dev`. The owner tier is advisory, never isolation.
- **Aliases:** `IDENTITY_ALIASES` is emitted as `Identity__Aliases__<alias id>`. Both sides must be owners.

## Keeping a deployment's values

Everything tenant-specific lives in the repository variables above, not in code, so a deployment's values are visible
only in the repository's Actions settings. Set them **before merging** a change to the IaC, or preflight fails (the
intended fail-closed). An absent `ACCESS_OWNERS`, `VAPID_SUBJECT`, `EMAIL_INBOUND_DOMAIN` or `IDENTITY_ALIASES` on an
existing deployment switches that feature off (owners, push contact, inbound email, aliases), so carry them over when
you move a deployment to this layout. The Entra tenant is the deploy's own (`tenant().tenantId`).

## Not covered

A fresh-resource-group or fresh-tenant test deploy was not run, so portability is proven structurally (tests and
`bicep build` in CI), not end to end. Owner-specific content outside the IaC is unchanged: the legal pages, the Android
app's default URL and the Chrome extension's fallback server (all point at the maintainer's public instance).
