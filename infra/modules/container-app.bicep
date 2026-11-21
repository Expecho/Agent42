@description('Location for the Container App.')
param location string

@description('Name of the Container App.')
param containerAppName string

@description('Name of the existing Container Apps managed environment.')
param environmentName string

@description('Resource group containing the existing Container Apps managed environment.')
param environmentResourceGroup string

@description('Login server of the existing Azure Container Registry, e.g. myregistry.azurecr.io.')
param registryLoginServer string

@secure()
@description('Admin username for the Azure Container Registry.')
param registryUsername string

@secure()
@description('Admin password for the Azure Container Registry.')
param registryPassword string

@description('Full container image reference, e.g. myregistry.azurecr.io/agent42-backend:latest.')
param containerImage string

@description('Cosmos DB endpoint used for grain state (identity-based auth, no key).')
param cosmosAccountEndpoint string

@description('Blob service endpoint of the attachments storage account (identity-based auth, no key).')
param blobEndpoint string

@description('Endpoint of the Azure AI Foundry / Azure OpenAI resource. Dormant Foundry-provider inheritance source only (ADR 0019 removed the baseline chat provider; the embedding model is per-user, ADR 0007/0018). Non-secret; harmless if unused.')
param azureOpenAIEndpoint string

@description('Cosmos DB container name for document RAG vector search (ADR 0007). Its presence switches the app onto the Cosmos document index.')
param cosmosDocChunksContainerName string

@description('Cosmos DB container name for the chat-message search projection (ADR 0061). Its presence switches the app onto the Cosmos chat search index.')
param cosmosChatMessagesContainerName string

@description('Entra ID tenant id used for DefaultAzureCredential (blob storage + ARM).')
param azureOpenAITenantId string

@description('OTLP endpoint for OpenTelemetry export. Leave empty to disable OTLP export.')
param otlpEndpoint string

@description('Client id of the Entra ID app registration used by built-in authentication (Easy Auth).')
param authClientId string

@secure()
@description('Client secret of the Entra ID app registration used by built-in authentication.')
param authClientSecret string

@description('Entra ID tenant id that issues sign-in tokens for built-in authentication.')
param authTenantId string

@description('Easy Auth session lifetime as a .NET TimeSpan, e.g. 14.00:00:00 (ADR 0090).')
param authSessionLifetime string

@description('CPU cores allocated to the backend container, e.g. 0.5.')
param cpu string

@description('Memory allocated to the backend container, e.g. 1Gi.')
param memory string

@description('Full SearXNG sidecar image reference, e.g. myregistry.azurecr.io/agent42-searxng:latest (ADR 0011).')
param searxngImage string

@description('CPU cores allocated to the SearXNG sidecar, e.g. 0.5. Container Apps bills the per-replica total, and the sum across containers must land on an allowed CPU/memory combination.')
param searxngCpu string

@description('Memory allocated to the SearXNG sidecar, e.g. 1Gi. Must pair with searxngCpu so the replica total is an allowed combination.')
param searxngMemory string

@secure()
@description('Secret key SearXNG signs its state with. Any long random string; supplied as the SEARXNG_SECRET GitHub Actions secret. The container refuses to start without it.')
param searxngSecret string

@secure()
@description('Google Cloud API key with the Geocoding API enabled (ADR 0012). OPTIONAL: when empty, neither the secret nor the env var below is emitted at all, and the app degrades to coordinates-only reverse geocoding rather than failing to deploy. An empty-valued Container App secret is not a thing worth risking for a feature that is not on the availability path.')
param googleMapsApiKey string = ''

@secure()
@description('Client secret of the GitHub OAuth App behind "Sign in with GitHub" (ADR 0108). OPTIONAL, exactly like googleMapsApiKey: when empty, neither the secret nor the env var below is emitted, and the sign-in answers 503 rather than the deployment failing. Not on the availability path.')
param githubOAuthClientSecret string = ''

@description('API key for the Hindsight long-term-memory service (ADR 0056). OPTIONAL, exactly like googleMapsApiKey/vapidPrivateKey: when empty, neither the secret nor the env var below is emitted, and long-term memory is simply off rather than the deployment failing. Not on the availability path.')
param hindsightApiKey string = ''

@secure()
@description('Application Insights connection string. OPTIONAL, exactly like googleMapsApiKey: when empty, neither the secret nor the env var below is emitted, and the app exports to the OTLP collector only.')
param appInsightsConnectionString string = ''

@secure()
@description('VAPID private key for Web Push notifications (ADR 0028). OPTIONAL, exactly like googleMapsApiKey: when empty, neither the secret nor the env var below is emitted, and push simply stays disabled (GET /api/push/config returns enabled:false, the UI hides the toggle) rather than failing to deploy. Not on the availability path. The matching public key + subject are non-secret and committed in appsettings.json.')
param vapidPrivateKey string = ''

@secure()
@description('Mailgun HTTP webhook signing key for inbound email (ADR 0096). OPTIONAL in the deploy sense, like googleMapsApiKey: when empty, neither the secret nor the env var below is emitted and the app starts. But POST /api/email-in then answers 503 and accepts nothing - it never skips verification.')
param mailgunSigningKey string = ''

@description('Minimum replica count. Orleans localhost clustering (used by this single-instance deployment) only supports one instance.')
param minReplicas int

@description('Maximum replica count. Orleans localhost clustering (used by this single-instance deployment) only supports one instance.')
param maxReplicas int

@description('Tags applied to the Container App.')
param tags object

@description('Custom domain hostname bound to the Container App ingress, e.g. chat.example.com. Leave empty to skip binding a custom domain.')
param customDomainName string = ''

@description('Name of the existing managed certificate resource (Microsoft.App/managedEnvironments/managedCertificates, in the same resource group as the environment) backing customDomainName. Azure auto-generates this name (format <domain>-<environment-name>-<timestamp>) when the certificate is first created via `az containerapp hostname bind` or the portal; look it up with `az containerapp env certificate list -n <environmentName> -g <environmentResourceGroup>`. Required when customDomainName is set. If the certificate is ever recreated, this value must be updated to match, or this deployment will unbind the domain again.')
param customDomainCertificateName string = ''

@description('Public VAPID key (ADR 0028). OPTIONAL: when empty the Push__VapidPublicKey env var is not emitted and the committed appsettings value stays in force.')
param vapidPublicKey string = ''

@description('VAPID subject for Web Push. OPTIONAL, like vapidPublicKey (Push__VapidSubject).')
param vapidSubject string = ''

@description('Client id of the GitHub OAuth App (ADR 0108). OPTIONAL: when empty GitHub__OAuth__ClientId is not emitted.')
param githubOAuthClientId string = ''

@description('Inbound email domain (ADR 0096). OPTIONAL: when empty Email__InboundDomain is not emitted.')
param emailInboundDomain string = ''

@description('Owner Entra object ids (ADR 0121/0128). OPTIONAL: when empty no Access__Owners__<i> env var is emitted. The shipped Access:Owners list is empty.')
param accessOwners array = []

@description('Identity aliases (ADR 0074), additional Entra object id -> owning object id. OPTIONAL: when empty no Identity__Aliases__<id> env var is emitted.')
param identityAliases object = {}

@description('Name of the environment storage link (Microsoft.App/managedEnvironments/storages) to the Azure Files share of copilot chats\' workspaces (ADR 0107).')
param copilotEnvironmentStorageName string

@description('Name of the environment storage link to the Azure Files share of the signal-cli store of the chat gateway (ADR 0111, phase 3).')
param signalEnvironmentStorageName string

@description('Whether the chat gateway offers Signal. Off, the share is still mounted but nothing reads it and no daemon starts.')
param signalEnabled bool = false

// The image runs as $APP_UID (src/Agent42.Backend/Dockerfile), 1654 in the aspnet base image (measured with
// `docker run --rm mcr.microsoft.com/dotnet/aspnet:11.0 id app`), while an SMB mount is root-owned by default: without
// these options the app could not write to its own share. SMB has no chmod and no symlinks, so the modes are fixed.
var copilotWorkspaceMountOptions = 'uid=1654,gid=1654,dir_mode=0700,file_mode=0600'

resource containerApp 'Microsoft.App/containerApps@2026-01-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: resourceId(environmentResourceGroup, 'Microsoft.App/managedEnvironments', environmentName)
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        // Declared explicitly (rather than left to out-of-band `az containerapp hostname bind`)
        // because every Bicep deployment overwrites the whole `ingress` object, including
        // `customDomains` — an undeclared custom domain gets silently unbound (though its
        // managed certificate resource survives) on the next redeploy otherwise.
        customDomains: !empty(customDomainName) ? [
          {
            name: customDomainName
            bindingType: 'SniEnabled'
            certificateId: resourceId(
              environmentResourceGroup,
              'Microsoft.App/managedEnvironments/managedCertificates',
              environmentName,
              customDomainCertificateName)
          }
        ] : []
      }
      registries: [
        {
          server: registryLoginServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      // The geocoding key is appended only when one was supplied — an absent key must not fail the
      // deployment (ADR 0012), and a secret with an empty value is not worth finding out about in prod.
      secrets: concat(
        [
          {
            name: 'registry-password'
            value: registryPassword
          }
          {
            name: 'microsoft-provider-authentication-secret'
            value: authClientSecret
          }
          {
            name: 'searxng-secret'
            value: searxngSecret
          }
        ],
        empty(googleMapsApiKey)
          ? []
          : [
              {
                name: 'google-maps-api-key'
                value: googleMapsApiKey
              }
            ],
        empty(githubOAuthClientSecret)
          ? []
          : [
              {
                name: 'github-oauth-client-secret'
                value: githubOAuthClientSecret
              }
            ],
        empty(mailgunSigningKey)
          ? []
          : [
              {
                name: 'mailgun-signing-key'
                value: mailgunSigningKey
              }
            ],
        empty(hindsightApiKey)
          ? []
          : [
              {
                name: 'hindsight-api-key'
                value: hindsightApiKey
              }
            ],
        empty(appInsightsConnectionString)
          ? []
          : [
              {
                name: 'appinsights-connection-string'
                value: appInsightsConnectionString
              }
            ],
        empty(vapidPrivateKey)
          ? []
          : [
              {
                name: 'vapid-private-key'
                value: vapidPrivateKey
              }
            ]
      )
    }
    template: {
      containers: [
        {
          name: 'backend'
          image: containerImage
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat([
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__cosmos', value: cosmosAccountEndpoint }
            { name: 'Storage__BlobEndpoint', value: blobEndpoint }
            // Dormant Foundry-provider inheritance source only (ADR 0019/0018); no operator embedding key
            // any more — the embedding model is per-user (ADR 0007/0018).
            { name: 'AzureOpenAI__Endpoint', value: azureOpenAIEndpoint }
            { name: 'Cosmos__DocChunksContainerName', value: cosmosDocChunksContainerName }
            { name: 'Cosmos__ChatMessagesContainerName', value: cosmosChatMessagesContainerName }
            { name: 'AzureOpenAI__TenantId', value: azureOpenAITenantId }
            { name: 'OTEL_SERVICE_NAME', value: containerAppName }
            { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: otlpEndpoint }
            // Pin the OTLP wire protocol: the collector listens on the gRPC port (4317), so a default
            // of http/protobuf would silently send to the wrong format. Harmless when otlpEndpoint is
            // empty — no exporter is registered in that case (see ServiceDefaults AddOpenTelemetryExporters).
            { name: 'OTEL_EXPORTER_OTLP_PROTOCOL', value: 'grpc' }
            // The Azure Monitor exporter's Statsbeat sends SDK health to Microsoft-owned ingestion keys,
            // which is egress outside the operator's boundary. Its option is internal, so this variable is
            // the only switch. Unconditional: harmless when no connection string is set.
            { name: 'APPLICATIONINSIGHTS_STATSBEAT_DISABLED', value: 'true' }
            // The SearXNG sidecar below. Containers in one Container App share a network namespace,
            // so it is reached over localhost and is unreachable from outside by construction — no
            // second ingress, and Easy Auth still fronts the only exposed port (ADR 0002).
            { name: 'WebSearch__Endpoint', value: 'http://localhost:8888' }
            // Copilot chats' workspaces (ADR 0107): the share mounted below, on this container only.
            { name: 'Copilot__WorkspaceRoot', value: '/mnt/copilot' }
            // Signal (ADR 0111, phase 3): the signal-cli account store mounted below, read only by the daemon the backend
            // runs as a child process under the gateway lease. Dormant unless signalEnabled.
            { name: 'ChatGateway__Signal__Enabled', value: string(signalEnabled) }
            { name: 'ChatGateway__Signal__DataDir', value: '/mnt/signal' }
          ],
          // Deploy-time overrides of committed appsettings values (ADR 0128). Each is emitted only when set, so prod
          // (which sets none) keeps the committed values authoritative.
          empty(vapidPublicKey)
            ? []
            : [{ name: 'Push__VapidPublicKey', value: vapidPublicKey }],
          empty(vapidSubject)
            ? []
            : [{ name: 'Push__VapidSubject', value: vapidSubject }],
          empty(githubOAuthClientId)
            ? []
            : [{ name: 'GitHub__OAuth__ClientId', value: githubOAuthClientId }],
          empty(emailInboundDomain)
            ? []
            : [{ name: 'Email__InboundDomain', value: emailInboundDomain }],
          empty(accessOwners)
            ? []
            : map(range(0, length(accessOwners)), i => { name: 'Access__Owners__${i}', value: string(accessOwners[i]) }),
          empty(identityAliases)
            ? []
            : map(items(identityAliases), alias => { name: 'Identity__Aliases__${alias.key}', value: string(alias.value) }),
          // Reverse geocoding for get_current_location (ADR 0012). Emitted only when a key was
          // supplied: with no key the backend registers CoordinatesOnlyGeocoder and starts normally.
          empty(googleMapsApiKey)
            ? []
            : [{ name: 'Geocoding__GoogleApiKey', secretRef: 'google-maps-api-key' }],
          // The GitHub OAuth App secret behind "Sign in with GitHub" (ADR 0108). Emitted only when supplied: without it
          // the GitHub Copilot provider is simply not offered.
          empty(githubOAuthClientSecret)
            ? []
            : [{ name: 'GitHub__OAuth__ClientSecret', secretRef: 'github-oauth-client-secret' }],
          // The Mailgun signing key behind inbound email (ADR 0096). Emitted only when supplied, like the one above,
          // but its absence is a 503 on /api/email-in, never an unverified accept.
          empty(mailgunSigningKey)
            ? []
            : [{ name: 'Email__MailgunSigningKey', secretRef: 'mailgun-signing-key' }],
          // The VAPID private key behind Web Push (ADR 0028). Emitted only when supplied: with no key
          // push stays disabled and the app starts normally — not on the availability path, so no
          // empty-valued secret is risked (the searxngSecret-vs-optional contrast, like the two above).
          empty(vapidPrivateKey)
            ? []
            : [{ name: 'Push__VapidPrivateKey', secretRef: 'vapid-private-key' }],
          // The Hindsight key behind long-term memory (ADR 0056). Emitted only when supplied: with no key
          // the app starts normally and BuildAgent attaches no memory provider, so nothing is recalled or
          // retained - the optional-secret shape of the three above, not searxngSecret's availability-path
          // shape. Supplying it is what turns the always-on per-turn egress ON.
          empty(hindsightApiKey)
            ? []
            : [{ name: 'Hindsight__ApiKey', secretRef: 'hindsight-api-key' }],
          // Application Insights: traces (chat content stripped) and metrics, alongside OTLP. Emitted only
          // when supplied: with no connection string the app exports to OTLP only and starts normally.
          empty(appInsightsConnectionString)
            ? []
            : [{ name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-connection-string' }])
          // The backend ONLY: the SearXNG sidecar has no business with chat files.
          volumeMounts: [
            { volumeName: 'copilot-workspaces', mountPath: '/mnt/copilot' }
            { volumeName: 'signal-data', mountPath: '/mnt/signal' }
          ]
        }
        // Self-hosted metasearch backing the web_search tool (ADR 0011): free at any volume and no API
        // key. NOT private, though — SearXNG is a *meta*search engine and forwards every query on to
        // Google/DuckDuckGo/Bing (ADR 0060); what self-hosting buys is no key, no bill and no per-user attribution, not
        // confidentiality. A thin image over upstream SearXNG with our settings.yml baked in
        // (infra/searxng) — `search.formats` must include `json` or the API answers 403.
        {
          name: 'searxng'
          image: searxngImage
          resources: {
            cpu: json(searxngCpu)
            memory: searxngMemory
          }
          env: [
            // NOT SearXNG's default 8080: that is the backend's ingress targetPort, and the two
            // containers share a network namespace. The image's entrypoint takes the listening port
            // from this variable (server.port in settings.yml is inert for the bind).
            { name: 'SEARXNG_PORT', value: '8888' }
            { name: 'SEARXNG_BIND_ADDRESS', value: '0.0.0.0' }
            // Carries the /searxng/ SUBPATH the backend proxies the UI under (ADR 0059). Measured on
            // the pinned image: with a subpath here, every generated href/src in the served HTML is
            // prefixed root-relative (`/searxng/static/...`), which is what makes the proxy work with
            // no markup rewriting. Set as an env var rather than in settings.yml because base_url
            // lives under `server:`, which the container's SEARXNG_* overrides DO cover — the inverse
            // of the `search.formats` case the Dockerfile records as NOT reachable that way.
            //
            // The authority only shows up in redirect Location headers (the proxy reduces those to a
            // root-relative path) and in opensearch.xml, so the custom domain is a correctness nicety
            // rather than load-bearing; falling back to localhost still proxies correctly. Serving is
            // unaffected: the sidecar answers on BOTH `/` and `/searxng/`, so web_search's own
            // `/search?format=json` call over localhost:8888 keeps working unchanged.
            {
              name: 'SEARXNG_BASE_URL'
              value: empty(customDomainName) ? 'http://localhost:8888/searxng/' : 'https://${customDomainName}/searxng/'
            }
            { name: 'SEARXNG_SECRET', secretRef: 'searxng-secret' }
          ]
        }
      ]
      // One replica (rule 12) mounts the share, so the in-process write gates are enough.
      volumes: [
        {
          name: 'copilot-workspaces'
          storageType: 'AzureFile'
          storageName: copilotEnvironmentStorageName
          mountOptions: copilotWorkspaceMountOptions
        }
        // The signal-cli store: same owner and modes as the workspaces. Plan Step 24 decides whether `nobrl` is needed for
        // SQLite's WAL locks on SMB; until it has, Signal stays off.
        {
          name: 'signal-data'
          storageType: 'AzureFile'
          storageName: signalEnvironmentStorageName
          mountOptions: copilotWorkspaceMountOptions
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

// Built-in authentication (Easy Auth): every request must carry a Microsoft Entra
// sign-in; unauthenticated browsers are redirected to the Entra login page.
// The ONLY allowlist is the "assignment required" setting on the agent42-easyauth enterprise app: only users
// assigned to it (members, or B2B guests the operator invited) can complete sign-in (ADR 0121, invite-only
// variant). There is deliberately no allowedPrincipals copy here: it needed a bicep edit and a deploy per person.
// The cost is that this template can no longer catch portal drift: if "assignment required" is switched off, every
// user of the tenant can sign in. Check it with
//   az ad sp show --id <authClientId> --query appRoleAssignmentRequired    (must print true)
resource authConfig 'Microsoft.App/containerApps/authConfigs@2026-01-01' = {
  parent: containerApp
  name: 'current'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
      // The only holes in the ADR 0002 gate — every other browser surface stays fully Easy-Auth-gated. Each
      // excluded path bypasses the Easy Auth login redirect and therefore SELF-ENFORCES access and fails closed.
      //
      // MATCHING RULE (measured against this app on 2026-08-20, ADR 0042 — do NOT assume prefix matching):
      // a bare entry matches the path EXACTLY and does not cover its sub-paths; an entry ending in '/*'
      // spans sub-paths across multiple segments, matches an empty remainder ('/shared/*' covers '/shared/'),
      // and keeps the trailing slash in its prefix — so '/api/share/*' does not match '/api/sharezzz'.
      // This is why '/api/share' and '/shared' alone left every recipient at the login page.
      //   • /api/agent-run  — per-agent API key, not an Entra session (ADR 0022); fails closed 401.
      //   • /api/share/*    — read-only share view (ADR 0035): the token is the sole credential; owner + chat
      //                       come from the directory grain (never GetUserId); fails closed 404. The wildcard
      //                       is required for both the token route and the multi-segment attachment route.
      //                       No bare '/api/share' entry: nothing is routed there.
      //   • /shared         — courtesy exact match so a hand-typed URL without the trailing slash gets the 301.
      //   • /shared/*       — the static read-only viewer page and its self-contained assets an unauthenticated
      //                       recipient opens; it carries no data itself and calls /api/share for content.
      //   • /api/agent-mcp   — the server-side MCP surface (ADR 0070): an external MCP client authenticates
      //     /api/agent-mcp/*   with the per-agent key (ADR 0022), not an Entra session. AgentMcpKeyGate is
      //                       middleware over the prefix, so initialize/tools/list are refused with the same
      //                       uniform 401 as tools/call — a check inside the tool method alone would leave
      //                       the protocol handshake anonymously reachable. BOTH entries are required and
      //                       neither is spare: measured on the SDK, MapMcp registers ONE endpoint that
      //                       matches the bare prefix AND its trailing-slash form, and a bare entry here
      //                       matches EXACTLY. Without the wildcard a client posting to /api/agent-mcp/ gets
      //                       the login page — the ADR 0035 failure, exactly.
      //   • /api/local-bridge — the local-bridge WebSocket (ADR 0076): a helper on the user's own machine
      //                       authenticates with its per-server bridge key in X-Api-Key, never an Entra session.
      //                       LocalBridgeEndpoint self-enforces and fails closed. EXACT, not /*: the route has no
      //                       sub-paths. Unmeasured in production: that the ingress and Easy Auth pass the
      //                       Upgrade through on an excluded path — ADR 0076 carries the post-deploy check.
      //   • /api/email-in   — Mailgun's inbound forward (ADR 0096): no Entra session, ever. The handler verifies the
      //                       Mailgun HMAC, then resolves the agent from the address token in the email directory —
      //                       never GetUserId — and fails closed. EXACT, not /*: the route takes no sub-paths.
      excludedPaths: [
        '/api/agent-run'
        '/api/share/*'
        '/shared'
        '/shared/*'
        '/api/agent-mcp'
        '/api/agent-mcp/*'
        '/api/local-bridge'
        '/api/email-in'
        '/legal/*'
      ]
    }
    // ADR 0090: the session cookie outlives the 8-hour platform default, so a returning user is not sent back
    // to Entra on every open. The app reads only X-MS-CLIENT-PRINCIPAL (CurrentUser.cs), never an Entra token,
    // so nothing breaks when the session outlives the tokens it was issued with.
    login: {
      cookieExpiration: {
        convention: 'FixedTime'
        timeToExpiration: authSessionLifetime
      }
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: authClientId
          clientSecretSettingName: 'microsoft-provider-authentication-secret'
          openIdIssuer: '${environment().authentication.loginEndpoint}${authTenantId}/v2.0'
        }
        // No login_hint (ADR 0121 amends ADR 0090): a hint would pin every sign-in, invited guests included, to
        // one account, so Entra shows its account picker and each person chooses their own.
      }
    }
  }
}

output principalId string = containerApp.identity.principalId
output fqdn string = containerApp.properties.configuration.ingress.fqdn
