# Run in Docker with Azure storage and Entra sign-in

> Run Agent 42 in plain Docker, outside Azure Container Apps, but still on Azure Cosmos DB and Blob Storage, with each
> person signing in through Microsoft Entra ID. The app does the sign-in itself (`Auth:Mode=entra`), so every user keeps
> their own data, keyed by their Entra object id.

## Which setup is this?

| | Sign-in | Storage | Use it when |
|---|---|---|---|
| [Azure deployment](azure.md) | Easy Auth in front of the app | Cosmos + Blob | You run on Azure Container Apps (the default and what production uses) |
| [Self-hosting](../features/self-hosting.md) | none (everyone is `local-dev`) | Postgres + a volume | One person, one machine, no Azure account |
| **This page** | the app itself, Entra ID | Cosmos + Blob | You want Docker but real accounts and your existing Azure data |

Easy Auth stays the default on Azure. Nothing changes there unless you set `Auth__Mode=entra`.

## What you need

- An Azure subscription with a Cosmos DB account and a Storage account (the ones the [Azure deployment](azure.md)
  creates, with the containers `agent-state`, `reminders`, `doc-chunks` and `chat-messages`).
- An **Entra app registration** that people sign in through.
- A **service principal** (a second app registration with a secret) that the container uses to reach Cosmos and Blob.
- Docker, and the repository's `compose.azure.yaml`.

## Quick start

1. Sign in with the Azure CLI (`az login`) as someone who can create app registrations and assign roles on your Cosmos and
   Storage accounts.
2. Run the setup script. It is for you to run by hand and reads like a checklist, so skim it first
   ([`infra/docker-entra-setup.sh`](https://github.com/Expecho/Agent42/blob/main/infra/docker-entra-setup.sh)):

   ```bash
   infra/docker-entra-setup.sh init --resource-group <rg> --cosmos-account <cosmos-account> --storage-account <storage-account>
   ```

   It creates the sign-in app registration (invite-only, with you assigned), a service principal for the container, grants
   the Cosmos and Blob roles, and prints a block of `.env` values.
3. Copy `.env.example` to `.env`, paste the printed block in, and set `SEARXNG_SECRET` to a long random string.
4. Start it with the repository's [`compose.azure.yaml`](https://github.com/Expecho/Agent42/blob/main/compose.azure.yaml):

   ```bash
   docker compose -f compose.azure.yaml up -d
   ```

5. Open <http://localhost:8080>, sign in, then add a model provider under Preferences.

Role assignments can take a few minutes to take effect, so if the first start shows storage errors, wait and restart.
Other commands of the script: `assign` lets another person sign in, `redirect` adds a public address, and `roles` only grants
the two roles to a service principal you already have. Run it with `help` for details. The sections below explain each
piece, and the commands to run them by hand.

## Set it up by hand

### 1. The sign-in app registration

Create a single-tenant app registration, platform **Web**, with these redirect URIs (use your public address instead of
`http://localhost:8080` if you have one):

| Purpose | URI |
|---|---|
| Sign-in | `http://localhost:8080/signin-oidc` |
| Sign-out | `http://localhost:8080/signout-callback-oidc` |

Add a client secret. The app asks for an ID token only: it never reads an access or refresh token, so it needs no API
permissions beyond sign-in.

Make it **invite-only**. On the enterprise application turn on "assignment required", then assign the people who may
sign in. Check it:

```bash
az ad sp show --id <client-id> --query appRoleAssignmentRequired
```

It must print `true`. Without it, anyone in your tenant can sign in. A person who is not assigned sees the Microsoft
error `AADSTS50105`.

### 2. The service principal and its roles

Create the service principal and give it the two data roles (`infra/docker-entra-setup.sh roles` does the second part).
**You run these commands**, they are not run for you:

```bash
# Cosmos DB data plane (grain state, reminders, document vectors, chat search)
az cosmosdb sql role assignment create --account-name <cosmos-account> --resource-group <rg> \
  --scope "/" --principal-id <service-principal-object-id> \
  --role-definition-name "Cosmos DB Built-in Data Contributor"

# Blob Storage (attachments, skills, fleet files and the Data Protection key ring at dataprotection/keys.xml)
az role assignment create --assignee <service-principal-object-id> \
  --role "Storage Blob Data Contributor" --scope <storage-account-resource-id>
```

### 3. Configure and start

```bash
cp .env.example .env     # fill the "compose.azure.yaml only" block, plus SEARXNG_SECRET
docker compose -f compose.azure.yaml up -d
```

Open <http://localhost:8080>. Then add a model provider under Preferences, as the composer is gated until you do.

## Settings

| Key | Meaning |
|---|---|
| `Auth__Mode` | `entra` turns the app's own sign-in on. Absent or `easyauth` is the default. Anything else fails at startup |
| `Auth__Entra__TenantId`, `Auth__Entra__ClientId`, `Auth__Entra__ClientSecret` | The sign-in app registration. A missing one stops the app from starting, rather than letting everyone in as `local-dev` |
| `Auth__PublicBaseUrl` | Optional. The address people use, when a proxy rewrites `Host`. Used for the sign-in and sign-out redirect URIs |
| `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` | The service principal the container uses for Cosmos and Blob |
| `ConnectionStrings__cosmos`, `Storage__BlobEndpoint` | The account addresses |
| `Access__Owners__0` | **Your own** Entra object id, which opens the dashboard and `/logs`. Never `local-dev` |

## HTTPS and the session cookie

The sign-in session is a cookie named `AppServiceAuthSession`, valid for 14 days from sign-in. It is marked `Secure` whenever
the request is HTTPS. `http://localhost` works because browsers treat it as a secure context. On any other host, put a
TLS-terminating proxy (Caddy or Traefik) in front, make it send `X-Forwarded-Proto`, and set `Auth__PublicBaseUrl` to the
public address. Without TLS the cookie is not protected on the wire.

The compose file binds the app to `127.0.0.1` for the same reason.

## Good to know

- **Sign-out** clears the cookie and also ends your Entra session, so the next sign-in shows the account picker.
- **Public links keep working.** Share links, the agent run API, the MCP endpoint and inbound email need no sign-in, as on
  Azure. Everything else needs a signed-in user.
- **The Android app** signs in the same way and needs no change.
- **The Copilot shell is off.** Any signed-in user could otherwise run commands inside the container, where they could read
  the service principal secret and forge the sign-in headers. The owner list is a convenience, not a security boundary. Turn
  `Copilot__BuiltInToolsEnabled` on only for people you fully trust.
- **One replica**, as on Azure.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Everyone is signed out after every restart | The Data Protection key ring is not persisted. Set `Storage__BlobEndpoint` (not a Blob connection string) and give the service principal Blob access. The Configuration tab shows `Auth:DataProtectionKeyRing` as an error |
| `AADSTS50011` / redirect URI mismatch | The URI in the app registration must equal `<address>/signin-oidc` exactly, including scheme and port |
| `AADSTS50105` | The user is not assigned to the enterprise application |
| A sign-in loop on a non-localhost address | The cookie is `Secure` but the request looks like HTTP. Send `X-Forwarded-Proto` from the proxy |
| The app will not start: `Auth:Entra:... is not set` | A required `Auth__Entra__*` value is empty. This is deliberate |

## See also

- [`.agentic/decisions/0145-native-entra-sign-in.md`](https://github.com/Expecho/Agent42/blob/main/.agentic/decisions/0145-native-entra-sign-in.md)
- [PWA install and sign-in](../features/pwa-and-sign-in.md)
- [Configuration health](../features/configuration-health.md)
