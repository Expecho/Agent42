@description('Location for the storage account.')
param location string

@description('Name of the storage account. Must be globally unique, 3-24 lowercase alphanumeric characters.')
param accountName string

@description('Name of the private blob container that holds conversation image attachments.')
param containerName string = 'attachments'

@description('Tags applied to the account.')
param tags object

// Key-free by design (ADR 0004): shared-key auth is disabled, so the only way in is Entra —
// the container app's managed identity holding Storage Blob Data Contributor, which also
// enables the user-delegation SAS the model fetches images through. Public blob access is off:
// images are reachable only via the app's authenticated display proxy or a short-lived SAS,
// never anonymously. The role assignment itself is classic Microsoft.Authorization RBAC, which
// the Contributor deploy SP cannot create — it is a manual step (see main.bicep output).
resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: accountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: account
  name: 'default'
}

// Pre-created here (like the Cosmos containers) so the container's privacy is declared in
// infra rather than left to the app to establish at runtime.
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: containerName
  properties: {
    publicAccess: 'None'
  }
}

output accountName string = account.name
output blobEndpoint string = account.properties.primaryEndpoints.blob
