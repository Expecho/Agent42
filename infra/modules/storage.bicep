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

// The Data Protection key ring (ADR 0018): the app persists the master key here so a restart does
// not rotate it and orphan every user's encrypted provider secret. Same privacy posture as the
// attachments container — reached only by the app MI's Storage Blob Data Contributor grant (no
// shared key). The master key itself is protected by Storage SSE + RBAC only (no ProtectKeysWith…);
// the user secrets it encrypts are DP-encrypted on top, and Cosmos at-rest encryption covers the
// persisted ciphertext.
// The name is a fixed literal (NOT a param) so there is ONE source of truth with the app, which
// hardcodes the same "dataprotection" path when it points the DP key ring at this container
// (Program.cs). A param here would be a second knob that could drift from the code.
resource dataProtectionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'dataprotection'
  properties: {
    publicAccess: 'None'
  }
}

// Uploaded agent skill packages (ADR 0049): one normalized zip per installed skill, at
// {userId}/{skillId}.zip. Same privacy posture as the other two containers — no public access, reached
// only by the app MI's account-scoped Storage Blob Data Contributor grant, which is why this container
// needs NO new role assignment (and so no manual RBAC step).
// This is the DURABLE record; the container filesystem the skills are materialized onto is only a cache
// of it, wiped by every restart.
// A fixed literal, not a param, for the same reason as `dataprotection`: BlobSkillPackageStore hardcodes
// the same name, and a param here would be a second knob that could drift from the code.
resource skillPackagesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'skill-packages'
  properties: {
    publicAccess: 'None'
  }
}

// The copilot agent kind: the Copilot runtime's own session files (its transcript, workspace and checkpoints), one
// prefix per conversation ({userId}/{conversationId}/). The AF session in Cosmos only points at them, so without
// this container a deploy would lose every copilot chat's context. Private and never handed a SAS; reached only by
// the app MI's account-scoped Storage Blob Data Contributor grant, so it needs NO new role assignment.
// A fixed literal for the same reason as `skill-packages`: BlobCopilotSessionStore hardcodes the same name.
resource copilotSessionsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'copilot-sessions'
  properties: {
    publicAccess: 'None'
  }
}

// The agent fleet (ADR 0097): per user, the fleet's tasks, their event logs (append blobs), workspace files and
// edited prompts ({userId}/tasks|events|workspaces|prompts.json). Private and never handed a SAS. It is reached only
// through the app MI's account-scoped Storage Blob Data Contributor grant, so it needs NO new role assignment.
// A fixed literal, like `copilot-sessions`: AzureFleetBlobs hardcodes the same name, and a param here would be a
// second knob that could drift from the code.
resource agentFleetContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'agent-fleet'
  properties: {
    publicAccess: 'None'
  }
}

// The resumable chat turn's replay log (ADR 0153): one append blob and two small JSON blobs per running turn, under
// `{user}/{conversation}/`. Retention is the app's own ChatTurnSweeper (any provider, incognito memory too), so this
// container carries no lifecycle rule. A fixed literal, like `agent-fleet`: AzureChatTurnBlobs.ContainerName must match
// (ChatTurnContainerNameTests pins both).
resource chatTurnsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'chat-turns'
  properties: {
    publicAccess: 'None'
  }
}

// The chat gateway's single-runner lease (ADR 0111): one empty blob, `chat-gateway/lease`, whose Blob lease decides
// which app revision runs the chat bots while an old and a new revision overlap during a rollover. Without it, two
// revisions would both poll Telegram (409 storms) and both answer every Discord message. Reached through the same
// account-scoped Storage Blob Data Contributor grant, so NO new role assignment. A fixed literal: BlobChatGatewayLease
// hardcodes the same name.
resource chatGatewayContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'chat-gateway'
  properties: {
    publicAccess: 'None'
  }
}

output accountName string = account.name
output blobEndpoint string = account.properties.primaryEndpoints.blob
