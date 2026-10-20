// Deployed into the Container Apps environment's resource group (rg-hosting), which is SHARED with other apps: this
// adds one storage link to the environment and touches nothing else there. The deploy SP is Contributor on rg-hosting.
//
// The link outlives the app: deleting ca-agent42-backend leaves it in cae-hosting. It holds a COPY of the account
// key, re-read by listKeys() on every deploy, so after a key rotation the mount fails until the next deploy (ADR 0107).

@description('Name of the existing Container Apps managed environment.')
param environmentName string

@description('Name of the storage link in the environment. Distinct, because the environment is shared.')
param storageName string

@description('Name of the storage account holding the share.')
param accountName string

@secure()
@description('Key of that storage account (Container Apps mounts Azure Files with the account key).')
param accountKey string

@description('Name of the file share.')
param shareName string

resource environment 'Microsoft.App/managedEnvironments@2026-01-01' existing = {
  name: environmentName
}

resource storage 'Microsoft.App/managedEnvironments/storages@2026-01-01' = {
  parent: environment
  name: storageName
  properties: {
    azureFile: {
      accountName: accountName
      accountKey: accountKey
      shareName: shareName
      accessMode: 'ReadWrite'
    }
  }
}

output name string = storage.name
