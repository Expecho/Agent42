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
@description('Azure OpenAI resource API key (ADR 0017). Authenticates the Foundry chat/Responses provider and the embedding client, replacing the managed identity. UNCONDITIONAL, unlike googleMapsApiKey/githubModelsToken: it is the availability path (the backend throws without it), so it is always emitted as a secret + env var — the searxngSecret shape, not the optional one. main.bicep enforces @minLength.')
param azureOpenAIApiKey string

@description('Model deployment name used for chat completions.')
param azureOpenAIDeploymentName string

@description('Embedding model deployment name used for document RAG (ADR 0007).')
param azureOpenAIEmbeddingDeploymentName string

@description('Cosmos DB container name for document RAG vector search (ADR 0007). Its presence switches the app onto the Cosmos document index.')
param cosmosDocChunksContainerName string

@description('Entra ID tenant id used for DefaultAzureCredential when calling Azure OpenAI.')
param azureOpenAITenantId string

@description('Subscription id of the Azure AI Foundry account, used by the live model picker to list deployments.')
param azureOpenAISubscriptionId string

@description('Resource group of the Azure AI Foundry account, used by the live model picker to list deployments.')
param azureOpenAIResourceGroup string

@description('Name of the Azure AI Foundry account, used by the live model picker to list deployments.')
param azureOpenAIAccountName string

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
@description('GitHub PAT with the models:read scope, authorizing the GitHub Models provider (ADR 0013). OPTIONAL, exactly like googleMapsApiKey and deliberately unlike searxngSecret: when empty, neither the secret nor the env var is emitted, the backend skips the provider with a warning and serves on Azure Foundry alone. A third-party token expiring must never take the app down.')
param githubModelsToken string = ''

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
        empty(githubModelsToken)
          ? []
          : [
              {
                name: 'github-models-token'
                value: githubModelsToken
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
            // The Azure OpenAI key (ADR 0017), UNCONDITIONAL — both the Foundry provider (which inherits
            // AzureOpenAI:ApiKey) and the embedding client read it; a single env var, one source of truth.
            { name: 'AzureOpenAI__ApiKey', secretRef: 'azure-openai-api-key' }
            { name: 'AzureOpenAI__DeploymentName', value: azureOpenAIDeploymentName }
            { name: 'AzureOpenAI__EmbeddingDeploymentName', value: azureOpenAIEmbeddingDeploymentName }
            { name: 'Cosmos__DocChunksContainerName', value: cosmosDocChunksContainerName }
            { name: 'AzureOpenAI__TenantId', value: azureOpenAITenantId }
            { name: 'AzureOpenAI__SubscriptionId', value: azureOpenAISubscriptionId }
            { name: 'AzureOpenAI__ResourceGroup', value: azureOpenAIResourceGroup }
            { name: 'AzureOpenAI__AccountName', value: azureOpenAIAccountName }
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
          // The GitHub Models provider's API key (ADR 0013). Emitted only when a token was supplied:
          // with none, the backend skips that provider with a warning and serves on Foundry alone. The
          // double underscore is the config-binder's section separator — this lands as
          // Models:Providers:github:ApiKey, i.e. the `github` key in appsettings' Models:Providers map.
          empty(githubModelsToken)
            ? []
            : [{ name: 'Models__Providers__github__ApiKey', secretRef: 'github-models-token' }])
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
