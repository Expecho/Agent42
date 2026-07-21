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

@description('Endpoint of the Azure AI Foundry / Azure OpenAI resource used by the agent.')
param azureOpenAIEndpoint string

@secure()
@description('Azure OpenAI resource API key (ADR 0017, narrowed by ADR 0019). Since ADR 0019 the app ships no baseline Foundry chat provider (chat is fully per-user, ADR 0018), so this authenticates the embedding client only — the one fixed model. UNCONDITIONAL, unlike googleMapsApiKey: it is the availability path (the backend throws without it), so it is always emitted as a secret + env var — the searxngSecret shape, not the optional one. main.bicep enforces @minLength.')
param azureOpenAIApiKey string

@description('Embedding model deployment name used for document RAG (ADR 0007).')
param azureOpenAIEmbeddingDeploymentName string

@description('Cosmos DB container name for document RAG vector search (ADR 0007). Its presence switches the app onto the Cosmos document index.')
param cosmosDocChunksContainerName string

@description('Entra ID tenant id used for DefaultAzureCredential when calling Azure OpenAI.')
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

@secure()
@description('VAPID private key for Web Push notifications (ADR 0028). OPTIONAL, exactly like googleMapsApiKey/contextApiKey: when empty, neither the secret nor the env var below is emitted, and push simply stays disabled (GET /api/push/config returns enabled:false, the UI hides the toggle) rather than failing to deploy. Not on the availability path. The matching public key + subject are non-secret and committed in appsettings.json.')
param vapidPrivateKey string = ''

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
          {
            name: 'azure-openai-api-key'
            value: azureOpenAIApiKey
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
            { name: 'AzureOpenAI__Endpoint', value: azureOpenAIEndpoint }
            // The Azure OpenAI key (ADR 0017, narrowed by ADR 0019), UNCONDITIONAL — the embedding client
            // reads it (the app ships no baseline Foundry chat provider any more; chat is per-user, ADR 0018).
            { name: 'AzureOpenAI__ApiKey', secretRef: 'azure-openai-api-key' }
            { name: 'AzureOpenAI__EmbeddingDeploymentName', value: azureOpenAIEmbeddingDeploymentName }
            { name: 'Cosmos__DocChunksContainerName', value: cosmosDocChunksContainerName }
            { name: 'AzureOpenAI__TenantId', value: azureOpenAITenantId }
            { name: 'OTEL_SERVICE_NAME', value: containerAppName }
            { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: otlpEndpoint }
            // Pin the OTLP wire protocol: the collector listens on the gRPC port (4317), so a default
            // of http/protobuf would silently send to the wrong format. Harmless when otlpEndpoint is
            // empty — no exporter is registered in that case (see ServiceDefaults AddOpenTelemetryExporters).
            { name: 'OTEL_EXPORTER_OTLP_PROTOCOL', value: 'grpc' }
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
          // The VAPID private key behind Web Push (ADR 0028). Emitted only when supplied: with no key
          // push stays disabled and the app starts normally — not on the availability path, so no
          // empty-valued secret is risked (the searxngSecret-vs-optional contrast, like the two above).
          empty(vapidPrivateKey)
            ? []
            : [{ name: 'Push__VapidPrivateKey', secretRef: 'vapid-private-key' }])
        }
        // Self-hosted metasearch backing the web_search tool (ADR 0011): free at any volume and no API
        // key. NOT private, though — SearXNG is a *meta*search engine and forwards every query on to
        // DuckDuckGo/Bing; what self-hosting buys is no key, no bill and no per-user attribution, not
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
            { name: 'SEARXNG_BASE_URL', value: 'http://localhost:8888/' }
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
      // The per-agent async run endpoint (ADR 0022) authenticates by a per-agent API key, not an Entra
      // session, so it must bypass the Easy Auth login redirect. It self-enforces the key and fails closed
      // (401). This is the ONLY hole in the ADR 0002 gate — every browser surface stays fully Easy-Auth-gated.
      excludedPaths: [
        '/api/agent-run'
      ]
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: authClientId
          clientSecretSettingName: 'microsoft-provider-authentication-secret'
          openIdIssuer: '${environment().authentication.loginEndpoint}${authTenantId}/v2.0'
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
