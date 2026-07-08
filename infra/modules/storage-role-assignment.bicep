@description('Name of the existing storage account holding attachments.')
param accountName string

@description('Principal id of the identity that should read/write attachment blobs (the Container App).')
param principalId string

// Storage Blob Data Contributor — grants blob read/write/delete AND generateUserDelegationKey
// (so the app mints user-delegation SAS without an account key). Well-known built-in role id.
var storageBlobDataContributorId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: accountName
}

// Unlike the Cosmos data-plane grant, this is classic Azure RBAC
// (Microsoft.Authorization/roleAssignments), which a plain Contributor cannot create — so the
// deploy service principal must additionally hold "Role Based Access Control Administrator" on
// this resource group (a one-time manual grant; see the gotchas file). guid() gives a stable,
// idempotent assignment name so redeploys are no-ops rather than duplicate-name failures.
resource blobDataContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: account
  name: guid(account.id, principalId, storageBlobDataContributorId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions', storageBlobDataContributorId)
  }
}
