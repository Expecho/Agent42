@description('Name of the existing Cosmos DB account.')
param accountName string

@description('Principal id of the identity that should read/write grain state (the Container App).')
param principalId string

// Cosmos DB's own data-plane RBAC system (Microsoft.DocumentDB/.../sqlRoleAssignments)
// is a distinct resource type from classic Azure RBAC (Microsoft.Authorization/roleAssignments),
// so a plain "Contributor" deployment principal can create this without needing
// "User Access Administrator".
resource account 'Microsoft.DocumentDB/databaseAccounts@2026-03-15' existing = {
  name: accountName
}

resource dataContributorAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2026-03-15' = {
  parent: account
  name: guid(account.id, principalId, 'cosmos-data-contributor')
  properties: {
    principalId: principalId
    roleDefinitionId: '${account.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: account.id
  }
}
