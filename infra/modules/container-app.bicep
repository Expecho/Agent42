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

@description('Object ids of the only Entra ID users allowed to sign in. Defense-in-depth next to the assignment requirement on the enterprise app.')
param authAllowedUserObjectIds array

@description('Account Entra signs in without showing the account picker. Empty sends no hint (ADR 0090).')
param authLoginHint string

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
@description('API key for the hosted context.dev page-scrape API behind fetch_web_page (ADR 0027). OPTIONAL, exactly like googleMapsApiKey: when empty, neither the secret nor the env var below is emitted, and fetch_web_page degrades to a per-call failure rather than failing to deploy. Not on the availability path.')
param contextApiKey string = ''

@description('API key for the Hindsight long-term-memory service (ADR 0056). OPTIONAL, exactly like googleMapsApiKey/contextApiKey/vapidPrivateKey: when empty, neither the secret nor the env var below is emitted, and long-term memory is simply off rather than the deployment failing. Not on the availability path.')
param hindsightApiKey string = ''

@secure()
@description('Application Insights connection string. OPTIONAL, exactly like googleMapsApiKey: when empty, neither the secret nor the env var below is emitted, and the app exports to the OTLP collector only.')
param appInsightsConnectionString string = ''

@secure()
@description('VAPID private key for Web Push notifications (ADR 0028). OPTIONAL, exactly like googleMapsApiKey/contextApiKey: when empty, neither the secret nor the env var below is emitted, and push simply stays disabled (GET /api/push/config returns enabled:false, the UI hides the toggle) rather than failing to deploy. Not on the availability path. The matching public key + subject are non-secret and committed in appsettings.json.')
param vapidPrivateKey string = ''

@secure()
@description('Mailgun HTTP webhook signing key for inbound email (ADR 0095). OPTIONAL in the deploy sense, like contextApiKey: when empty, neither the secret nor the env var below is emitted and the app starts. But POST /api/email-in then answers 503 and accepts nothing - it never skips verification.')
param mailgunSigningKey string = ''

@description('Minimum replica count. Orleans localhost clustering (used by this single-instance deployment) only supports one instance.')
param minReplicas int

@description('Maximum replica count. Orleans localhost clustering (used by this single-instance deployment) only supports one instance.')
param maxReplicas int

@description('Tags applied to the Container App.')
param tags object

@description('Custom domain hostname bound to the Container App ingress, e.g. chat.agent42.nl. Leave empty to skip binding a custom domain.')
param customDomainName string = ''

@description('Name of the existing managed certificate resource (Microsoft.App/managedEnvironments/managedCertificates, in the same resource group as the environment) backing customDomainName. Azure auto-generates this name (format <domain>-<environment-name>-<timestamp>) when the certificate is first created via `az containerapp hostname bind` or the portal; look it up with `az containerapp env certificate list -n <environmentName> -g <environmentResourceGroup>`. Required when customDomainName is set. If the certificate is ever recreated, this value must be updated to match, or this deployment will unbind the domain again.')
param customDomainCertificateName string = ''

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
        empty(contextApiKey)
          ? []
          : [
              {
                name: 'context-api-key'
                value: contextApiKey
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
          ],
          // Reverse geocoding for get_current_location (ADR 0012). Emitted only when a key was
          // supplied: with no key the backend registers CoordinatesOnlyGeocoder and starts normally.
          empty(googleMapsApiKey)
            ? []
            : [{ name: 'Geocoding__GoogleApiKey', secretRef: 'google-maps-api-key' }],
          // The context.dev scrape key behind fetch_web_page (ADR 0027). Emitted only when supplied:
          // with no key the fetcher is still registered and simply fails per call — not on the
          // availability path, so no empty-valued secret is risked.
          empty(contextApiKey)
            ? []
            : [{ name: 'ContextScrape__ApiKey', secretRef: 'context-api-key' }],
          // The Mailgun signing key behind inbound email (ADR 0095). Emitted only when supplied, like the one above,
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
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

// Built-in authentication (Easy Auth): every request must carry a Microsoft Entra
// sign-in; unauthenticated browsers are redirected to the Entra login page.
// Primary access control is the assignment requirement on the enterprise app
// (only explicitly assigned users can complete sign-in); allowedPrincipals
// repeats the allowlist here so the restriction survives portal config drift.
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
      //   • /api/email-in   — Mailgun's inbound forward (ADR 0095): no Entra session, ever. The handler verifies the
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
        // ADR 0090: both allowed accounts live in one tenant, so without a hint Entra shows its account
        // picker on every sign-in. The hint makes it sign straight in. Reaching the other account means
        // Sign out (the sidebar link to /.auth/logout) and then choosing it at Entra.
        login: {
          loginParameters: empty(authLoginHint) ? [] : [ 'login_hint=${authLoginHint}' ]
        }
        validation: {
          defaultAuthorizationPolicy: {
            allowedPrincipals: {
              identities: authAllowedUserObjectIds
            }
          }
        }
      }
    }
  }
}

output principalId string = containerApp.identity.principalId
output fqdn string = containerApp.properties.configuration.ingress.fqdn
