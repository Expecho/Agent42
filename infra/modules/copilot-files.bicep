@description('Location for the storage account.')
param location string

@description('Name of the storage account. Must be globally unique, 3-24 lowercase alphanumeric characters.')
param accountName string

@description('Name of the file share that holds copilot chats\' workspaces.')
param shareName string

@description('Size cap of the share in GiB.')
param shareQuotaGiB int

@description('Name of the file share that holds the signal-cli account store (ADR 0111, phase 3).')
param signalShareName string

@description('Size cap of the signal-cli share in GiB.')
param signalShareQuotaGiB int

@description('Tags applied to the account.')
param tags object

// Copilot chats' working directories (ADR 0107): one directory per conversation, mounted into the backend container
// over SMB so the Copilot runtime's shell and file tools work on real files that survive a deploy. The Copilot runtime's
// own state (transcripts) stays in the key-free attachments account's 'copilot-sessions' container.
//
// The account also holds a second share: the signal-cli account store of the chat gateway (ADR 0111, phase 3), keys
// and a WAL-mode SQLite per Signal number, written by ONE daemon at a time because the backend runs it only under the
// gateway lease. That single writer is why SQLite over SMB is acceptable there when ADR 0085/0107 rejected it for the
// Copilot runtime.
//
// A SEPARATE account because Container Apps mounts Azure Files with the account KEY, and the attachments account is
// key-free by design (ADR 0004, storage.bicep). Shared-key access is on here, and this account holds only these shares.
// The key reaches the environment storage through listKeys() in main.bicep, so no role assignment is involved.
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
    allowSharedKeyAccess: true
    supportsHttpsTrafficOnly: true
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: account
  name: 'default'
}

resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: shareName
  properties: {
    shareQuota: shareQuotaGiB
  }
}

resource signalShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: signalShareName
  properties: {
    shareQuota: signalShareQuotaGiB
  }
}

output accountName string = account.name
output shareName string = share.name
output signalShareName string = signalShare.name
