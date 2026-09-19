targetScope = 'resourceGroup'

// Deploy with:
//   az deployment group create -g rg-agent42 -f infra/main.bicep \
//     -p acrAdminUsername=... acrAdminPassword=... imageTag=<git-sha>

@description('Location for resources created by this deployment. The referenced Container Apps environment and registry are fixed to their own existing locations.')
param location string = resourceGroup().location

@description('Name of the Container App running the Agent 42 backend/silo.')
param containerAppName string = 'ca-agent42-backend'

@description('Name of the existing Azure Container Apps environment.')
param containerAppsEnvironmentName string = 'cae-hosting'

@description('Resource group containing the existing Azure Container Apps environment.')
param containerAppsEnvironmentResourceGroup string = 'rg-hosting'

@description('Name of the existing Azure Container Registry.')
param containerRegistryName string = 'crexpechohosting'

@description('Repository name of the backend image (without registry host or tag).')
param imageName string = 'agent42-backend'

@description('Tag of the image to deploy, e.g. a git commit SHA.')
param imageTag string = 'latest'

@description('Repository name of the SearXNG sidecar image (without registry host or tag) — ADR 0011.')
param searxngImageName string = 'agent42-searxng'

@description('Tag of the SearXNG sidecar image to deploy, e.g. a git commit SHA.')
param searxngImageTag string = 'latest'

@secure()
@minLength(16)
@description('Secret key SearXNG signs its state with (the SEARXNG_SECRET GitHub Actions secret — any long random string). A manual prerequisite. @minLength is load-bearing: an absent GitHub secret expands to an EMPTY string, which would crash-loop the sidecar — and because Container Apps gates revision health on every container in the replica, that would take the BACKEND down with it. Failing here, at deployment validation, beats a dead revision in production.')
param searxngSecret string

@secure()
@description('Google Cloud API key with the Geocoding API enabled, for reverse-geocoding the user\'s position (ADR 0012 — the GOOGLE_MAPS_API_KEY GitHub Actions secret). A manual prerequisite, but deliberately OPTIONAL: unlike searxngSecret there is no @minLength guard and the default is empty, because an absent key must NOT fail the deployment. It is not on the availability path — the app starts fine without it and get_current_location simply reports coordinates with no street address.')
param googleMapsApiKey string = ''

@secure()
@description('API key for the hosted context.dev page-scrape API behind fetch_web_page (ADR 0027 — the CONTEXT_API_KEY GitHub Actions secret). A manual prerequisite, but deliberately OPTIONAL like googleMapsApiKey (no @minLength, empty default): an absent key must NOT fail the deployment. It is not on the availability path — the app starts fine without it and fetch_web_page simply fails per call, telling the model the page could not be read (unlike searxngSecret, whose absence crash-loops the sidecar and takes the backend down).')
param contextApiKey string = ''

@description('API key for the Hindsight long-term-memory service (ADR 0056 - the HINDSIGHT_API_KEY GitHub Actions secret). A manual prerequisite, but deliberately OPTIONAL like googleMapsApiKey/contextApiKey (no @minLength, empty default): an absent key must NOT fail the deployment. It is not on the availability path - the app starts fine without it and memory is simply off, with BuildAgent attaching no memory provider at all, so a turn makes zero Hindsight calls rather than calls that quietly fail. NOTE the reverse: SETTING this key turns on an always-on egress in which every non-incognito turn, user message and assistant reply both, is POSTed to a third party. Read ADR 0056 before supplying it.')
param hindsightApiKey string = ''

@secure()
@description('Connection string of the existing Application Insights resource agent-42-appinsights (the APPLICATIONINSIGHTS_CONNECTION_STRING GitHub Actions secret). OPTIONAL, like googleMapsApiKey: when empty the app exports to the OTLP collector only. When set, traces with chat content stripped and metrics also go to Application Insights; logs never do. The resource is not managed here: it already exists, and connection-string ingestion needs no role assignment.')
param appInsightsConnectionString string = ''

@secure()
@description('VAPID private key for Web Push notifications (ADR 0028 — the VAPID_PRIVATE_KEY GitHub Actions secret). A manual prerequisite, but deliberately OPTIONAL like googleMapsApiKey/contextApiKey (no @minLength, empty default): an absent key must NOT fail the deployment. It is not on the availability path — the app starts fine without it and push simply stays disabled (the UI hides the toggle). The matching public key + subject are non-secret and committed in appsettings.json.')
param vapidPrivateKey string = ''

@secure()
@description('Admin username for the Azure Container Registry (used for image pull).')
param acrAdminUsername string

@secure()
@description('Admin password for the Azure Container Registry (used for image pull).')
param acrAdminPassword string

@description('Name of the serverless Cosmos DB account to create. Must be globally unique, lowercase, 3-44 characters.')
param cosmosAccountName string = 'cosmos-agent42-${uniqueString(resourceGroup().id)}'

@description('Location for the Cosmos DB account. Defaults to the same location as other resources, but can be overridden independently if that region is temporarily out of capacity for new accounts.')
param cosmosLocation string = location

@description('Cosmos DB SQL database name used by the Orleans grain storage provider.')
param cosmosDatabaseName string = 'agent42'

@description('Cosmos DB SQL container name used by the Orleans grain storage provider.')
param cosmosContainerName string = 'agent-state'

@description('Cosmos DB SQL container name used by the Orleans durable reminders provider.')
param cosmosRemindersContainerName string = 'reminders'

@description('Cosmos DB SQL container name used for document RAG vector search (ADR 0007). Setting it also switches the app onto the Cosmos document index (vs the in-memory one).')
param cosmosDocChunksContainerName string = 'doc-chunks'

@description('Cosmos DB SQL container name used for the searchable chat-message projection (ADR 0061). Setting it also switches the app onto the Cosmos chat search index (vs the in-memory one).')
param cosmosChatMessagesContainerName string = 'chat-messages'

@description('Name of the storage account to create for image attachments. Must be globally unique, lowercase alphanumeric, 3-24 characters.')
param storageAccountName string = 'stagent42${uniqueString(resourceGroup().id)}'

@description('Name of the private blob container that holds conversation image attachments.')
param attachmentsContainerName string = 'attachments'

@description('Endpoint of the existing Azure AI Foundry / Azure OpenAI resource. Kept only as the DORMANT Foundry-provider inheritance source (ProviderSettings.ResolveFoundry): baseline Models:Providers is empty (ADR 0019), so nothing reads it today; a user\'s Foundry provider declares its own. Non-secret; harmless if unused.')
param azureOpenAIEndpoint string = 'https://agent42.services.ai.azure.com'

@description('Entra ID tenant id bound to DefaultAzureCredential for blob storage (user-delegation SAS) and ARM. Still required.')
param azureOpenAITenantId string = '2250fce3-5bc9-4ae7-a5e9-58c510a422ec'

@description('OTLP endpoint for OpenTelemetry export. Leave empty to disable OTLP export.')
param otlpEndpoint string = ''

@description('Client id of the agent42-easyauth Entra ID app registration used by built-in authentication.')
param authClientId string = 'e9a02aec-5368-4c9a-8211-d21222c4f30c'

@secure()
@description('Client secret of the agent42-easyauth app registration. Rotate with: az ad app credential reset --id <authClientId>, then update the AAD_CLIENT_SECRET GitHub secret.')
param authClientSecret string

@description('Entra ID tenant id that issues sign-in tokens for built-in authentication.')
param authTenantId string = tenant().tenantId

@description('Object ids of the only users allowed to sign in: peter.bons@aceve.com and msn@2000miles.nl. Must stay in sync with the user assignments on the agent42-easyauth enterprise app.')
param authAllowedUserObjectIds array = [
  '2da7fa48-e41e-4ebd-b665-96bc4210fefd' // peter.bons@aceve.com
  '4e85e5fd-1130-4498-a29f-d02cad7ca43b' // msn@2000miles.nl
]

// Container Apps bills the PER-REPLICA total, and the sum across all containers in the replica must
// land on an allowed CPU/memory combination (CPU in 0.25 steps, memory = 2 GiB per CPU). Backend
// 0.5/1Gi + SearXNG 0.5/1Gi = 1.0 CPU / 2Gi per replica — an allowed pair, and roughly double the
// app's previous compute cost. Replicas stay pinned at 1 (Orleans localhost clustering), so there is
// exactly one SearXNG per deployment.
@description('CPU cores allocated to the backend container.')
param containerCpu string = '0.5'

@description('Memory allocated to the backend container.')
param containerMemory string = '1Gi'

@description('CPU cores allocated to the SearXNG sidecar. Must keep the replica total (backend + sidecar) on an allowed CPU/memory combination.')
param searxngCpu string = '0.5'

@description('Memory allocated to the SearXNG sidecar. Must keep the replica total (backend + sidecar) on an allowed CPU/memory combination.')
param searxngMemory string = '1Gi'

@description('Minimum replica count. Orleans localhost clustering (this deployment does not provision a distributed clustering provider) only supports a single instance.')
@minValue(1)
@maxValue(1)
param minReplicas int = 1

@description('Maximum replica count. Orleans localhost clustering (this deployment does not provision a distributed clustering provider) only supports a single instance.')
@minValue(1)
@maxValue(1)
param maxReplicas int = 1

@description('Custom domain hostname bound to the Container App ingress. Leave empty to skip binding a custom domain.')
param customDomainName string = 'chat.agent42.nl'

@description('Name of the existing managed certificate resource in the Container Apps environment (rg-hosting/cae-hosting) backing customDomainName. Look up with: az containerapp env certificate list -n cae-hosting -g rg-hosting. Update if the certificate is ever recreated.')
param customDomainCertificateName string = 'chat.agent42.nl-cae-host-260706080049'

@description('Tags applied to all created resources.')
param tags object = {
  application: 'agent42'
}

var containerImage = '${containerRegistryName}.azurecr.io/${imageName}:${imageTag}'
var searxngImage = '${containerRegistryName}.azurecr.io/${searxngImageName}:${searxngImageTag}'

module cosmos 'modules/cosmos.bicep' = {
  name: 'agent42-cosmos'
  params: {
    location: cosmosLocation
    accountName: cosmosAccountName
    databaseName: cosmosDatabaseName
    containerName: cosmosContainerName
    remindersContainerName: cosmosRemindersContainerName
    docChunksContainerName: cosmosDocChunksContainerName
    chatMessagesContainerName: cosmosChatMessagesContainerName
    tags: tags
  }
}

module storage 'modules/storage.bicep' = {
  name: 'agent42-storage'
  params: {
    location: location
    accountName: storageAccountName
    containerName: attachmentsContainerName
    tags: tags
  }
}

module containerApp 'modules/container-app.bicep' = {
  name: 'agent42-container-app'
  params: {
    location: location
    containerAppName: containerAppName
    environmentName: containerAppsEnvironmentName
    environmentResourceGroup: containerAppsEnvironmentResourceGroup
    registryLoginServer: '${containerRegistryName}.azurecr.io'
    registryUsername: acrAdminUsername
    registryPassword: acrAdminPassword
    containerImage: containerImage
    searxngImage: searxngImage
    searxngCpu: searxngCpu
    searxngMemory: searxngMemory
    searxngSecret: searxngSecret
    googleMapsApiKey: googleMapsApiKey
    contextApiKey: contextApiKey
    hindsightApiKey: hindsightApiKey
    appInsightsConnectionString: appInsightsConnectionString
    vapidPrivateKey: vapidPrivateKey
    cosmosAccountEndpoint: cosmos.outputs.documentEndpoint
    blobEndpoint: storage.outputs.blobEndpoint
    azureOpenAIEndpoint: azureOpenAIEndpoint
    cosmosDocChunksContainerName: cosmosDocChunksContainerName
    cosmosChatMessagesContainerName: cosmosChatMessagesContainerName
    azureOpenAITenantId: azureOpenAITenantId
    otlpEndpoint: otlpEndpoint
    authClientId: authClientId
    authClientSecret: authClientSecret
    authTenantId: authTenantId
    authAllowedUserObjectIds: authAllowedUserObjectIds
    customDomainName: customDomainName
    customDomainCertificateName: customDomainCertificateName
    cpu: containerCpu
    memory: containerMemory
    minReplicas: minReplicas
    maxReplicas: maxReplicas
    tags: tags
  }
}

// Deployed after the Container App exists so its managed identity's principal id
// is known; deployed after Cosmos so the account exists to attach the role to.
module cosmosRoleAssignment 'modules/cosmos-role-assignment.bicep' = {
  name: 'agent42-cosmos-role-assignment'
  params: {
    accountName: cosmos.outputs.accountName
    principalId: containerApp.outputs.principalId
  }
}

// Grants the Container App identity Storage Blob Data Contributor on the attachments account.
// This is classic Azure RBAC (Microsoft.Authorization/roleAssignments), so it works here ONLY
// because the deploy service principal was granted "Role Based Access Control Administrator" on
// this resource group (a one-time manual step — see the gotchas file). If that elevation is ever
// removed, or the SP recreated, fall back to the manual `az role assignment create` grant.
module storageRoleAssignment 'modules/storage-role-assignment.bicep' = {
  name: 'agent42-storage-role-assignment'
  params: {
    accountName: storage.outputs.accountName
    principalId: containerApp.outputs.principalId
  }
}

@description('The URL of the deployed Agent 42 backend.')
output backendUrl string = 'https://${containerApp.outputs.fqdn}'

@description('Principal id of the system-assigned managed identity of the Container App. As of ADR 0017 the Foundry data plane (chat, Responses, embeddings) authenticates with azureOpenAIApiKey, and ARM model discovery is off, so the AI Foundry account grants "Cognitive Services OpenAI User" and "Reader" are NO LONGER REQUIRED for the app to function (leaving any existing grants is harmless). The identity is still used for blob storage (Storage Blob Data Contributor, handled in Bicep — modules/storage-role-assignment.bicep) and Cosmos (cosmos-role-assignment.bicep).')
output containerAppPrincipalId string = containerApp.outputs.principalId

@description('Name of the Cosmos DB account created for grain state.')
output cosmosAccountName string = cosmos.outputs.accountName

@description('Name of the storage account created for image attachments. Its blob endpoint is passed to the container app as Storage__BlobEndpoint; the container app identity is granted Storage Blob Data Contributor on it in Bicep (modules/storage-role-assignment.bicep).')
output storageAccountName string = storage.outputs.accountName
