targetScope = 'resourceGroup'

// Create-or-reuse shared infrastructure (ADR 0128): the container registry, and the Container Apps environment with its
// Log Analytics workspace. Deployed by deploy.yml BEFORE main.bicep, because the image push needs the registry.
//
//   - All three existing* params empty  -> everything is created in this resource group.
//   - All three set                     -> nothing is created; the names pass straight through (the owner's prod mode).
// deploy.yml's preflight job rejects a mix, so create mode is an explicit choice and never a side effect of one
// missing variable.

@description('Location for the resources created here.')
param location string = resourceGroup().location

@description('Name of an existing Azure Container Registry to reuse. Leave empty to create one in this resource group.')
param existingRegistryName string = ''

@description('Name of an existing Container Apps environment to reuse. Leave empty to create one (plus a Log Analytics workspace) in this resource group.')
param existingEnvironmentName string = ''

@description('Resource group of the existing Container Apps environment. Ignored (and replaced by this resource group) when a new environment is created.')
param existingEnvironmentResourceGroup string = ''

@description('Tags applied to all created resources.')
param tags object = {
  application: 'agent42'
}

var createRegistry = empty(existingRegistryName)
var createEnvironment = empty(existingEnvironmentName)
var newRegistryName = 'cr${uniqueString(resourceGroup().id)}'
var newEnvironmentName = 'cae-${uniqueString(resourceGroup().id)}'

// Admin user stays on: the Container App pulls with admin credentials (registries in modules/container-app.bicep and
// `az acr credential show` in deploy.yml). Managed-identity AcrPull is out of scope (ADR 0128).
resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' = if (createRegistry) {
  name: newRegistryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-07-01' = if (createEnvironment) {
  name: 'log-${newEnvironmentName}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource environment 'Microsoft.App/managedEnvironments@2026-01-01' = if (createEnvironment) {
  name: newEnvironmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace!.properties.customerId
        sharedKey: workspace!.listKeys().primarySharedKey
      }
    }
  }
}

@description('Name of the container registry images are pushed to and pulled from.')
output registryName string = createRegistry ? newRegistryName : existingRegistryName

@description('Name of the Container Apps environment the app runs in.')
output environmentName string = createEnvironment ? newEnvironmentName : existingEnvironmentName

@description('Resource group of that environment.')
output environmentResourceGroup string = createEnvironment ? resourceGroup().name : existingEnvironmentResourceGroup
