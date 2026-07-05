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

@description('Endpoint of the existing Azure AI Foundry / Azure OpenAI resource.')
param azureOpenAIEndpoint string = 'https://agent42.services.ai.azure.com'

@description('Model deployment name used for chat completions.')
param azureOpenAIDeploymentName string = 'gpt-4o'

@description('Entra ID tenant id used for DefaultAzureCredential when calling Azure OpenAI.')
param azureOpenAITenantId string = '2250fce3-5bc9-4ae7-a5e9-58c510a422ec'

@description('Subscription id of the Azure AI Foundry account backing the live model picker. Defaults to this deployment\'s subscription.')
param azureOpenAISubscriptionId string = subscription().subscriptionId

@description('Resource group of the Azure AI Foundry account backing the live model picker.')
param azureOpenAIResourceGroup string = 'agent42'

@description('Name of the Azure AI Foundry account backing the live model picker.')
param azureOpenAIAccountName string = 'Agent42'

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

@description('CPU cores allocated to the container.')
param containerCpu string = '0.5'

@description('Memory allocated to the container.')
param containerMemory string = '1Gi'

@description('Minimum replica count. Orleans localhost clustering (this deployment does not provision a distributed clustering provider) only supports a single instance.')
@minValue(1)
@maxValue(1)
param minReplicas int = 1

@description('Maximum replica count. Orleans localhost clustering (this deployment does not provision a distributed clustering provider) only supports a single instance.')
@minValue(1)
@maxValue(1)
param maxReplicas int = 1

@description('Tags applied to all created resources.')
param tags object = {
  application: 'agent42'
}

var containerImage = '${containerRegistryName}.azurecr.io/${imageName}:${imageTag}'

module cosmos 'modules/cosmos.bicep' = {
  name: 'agent42-cosmos'
  params: {
    location: cosmosLocation
    accountName: cosmosAccountName
    databaseName: cosmosDatabaseName
    containerName: cosmosContainerName
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
    cosmosAccountEndpoint: cosmos.outputs.documentEndpoint
    azureOpenAIEndpoint: azureOpenAIEndpoint
    azureOpenAIDeploymentName: azureOpenAIDeploymentName
    azureOpenAITenantId: azureOpenAITenantId
    azureOpenAISubscriptionId: azureOpenAISubscriptionId
    azureOpenAIResourceGroup: azureOpenAIResourceGroup
    azureOpenAIAccountName: azureOpenAIAccountName
    otlpEndpoint: otlpEndpoint
    authClientId: authClientId
    authClientSecret: authClientSecret
    authTenantId: authTenantId
    authAllowedUserObjectIds: authAllowedUserObjectIds
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

@description('The URL of the deployed Agent 42 backend.')
output backendUrl string = 'https://${containerApp.outputs.fqdn}'

@description('Principal id of the system-assigned managed identity of the Container App. After the first deployment (or any deploy that creates a new identity), grant it two roles on the Agent 42 AI Foundry account (subscriptions/{sub}/resourceGroups/agent42/providers/Microsoft.CognitiveServices/accounts/Agent42): "Cognitive Services OpenAI User" (to call the model) and "Reader" (so the live model picker can list deployments via the management plane).')
output containerAppPrincipalId string = containerApp.outputs.principalId

@description('Name of the Cosmos DB account created for grain state.')
output cosmosAccountName string = cosmos.outputs.accountName
