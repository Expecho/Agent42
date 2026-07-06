@description('Name of the existing Azure AI Foundry / Cognitive Services account.')
param accountName string

@description('Principal id of the identity that should call chat completions (the Container App).')
param principalId string

// Unlike Cosmos, Cognitive Services has no provider-native RBAC system — granting
// data-plane access here goes through classic Azure RBAC
// (Microsoft.Authorization/roleAssignments), which requires the deploying principal
// to hold "User Access Administrator" / "Role Based Access Control Administrator" (or
// an equivalent custom/conditioned role) at this scope. Contributor alone cannot
// create this resource. See the one-time bootstrap grant documented alongside this
// module's use in main.bicep.
resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: accountName
}

resource openAIUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, 'cognitive-services-openai-user')
  scope: account
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}
