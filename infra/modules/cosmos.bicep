@description('Location for the Cosmos DB account.')
param location string

@description('Name of the serverless Cosmos DB account. Must be globally unique.')
param accountName string

@description('Cosmos DB SQL database name.')
param databaseName string

@description('Cosmos DB SQL container name used for Orleans grain storage.')
param containerName string

@description('Cosmos DB SQL container name used for Orleans durable reminders.')
param remindersContainerName string

@description('Cosmos DB SQL container name used for document RAG vector search (ADR 0007).')
param docChunksContainerName string

@description('Tags applied to the account.')
param tags object

// Serverless: pay-per-request, no provisioned throughput to size or pay for
// while idle — a good fit for a single-instance agent backend.
resource account 'Microsoft.DocumentDB/databaseAccounts@2026-03-15' = {
  name: accountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: [
      { name: 'EnableServerless' }
      // NoSQL vector search for the document-RAG container below (ADR 0007). This is an
      // account-level capability; adding it to the existing account is an in-place update.
      { name: 'EnableNoSQLVectorSearch' }
    ]
    minimalTlsVersion: 'Tls12'
    disableLocalAuth: true
  }
}

// Pre-created (rather than left to the app's own IsResourceCreationEnabled
// auto-create path) so the partition key matches what Orleans.Persistence.Cosmos
// expects (/PartitionKey), and so the running app only ever needs the Cosmos
// *data-plane* role below — never the separate, classic-RBAC "Cosmos DB Operator"
// role that resource creation over AAD auth would otherwise require.
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2026-03-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
  }
}

resource container 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2026-03-15' = {
  parent: database
  name: containerName
  properties: {
    resource: {
      id: containerName
      partitionKey: {
        paths: ['/PartitionKey']
        kind: 'Hash'
      }
    }
  }
}

// Pre-created for the same reason as the grain-state container above: the account has
// disableLocalAuth: true, and Cosmos's AAD data-plane RBAC cannot create databases or
// containers (see https://aka.ms/cosmos-native-rbac) — Orleans.Reminders.Cosmos's own
// IsResourceCreationEnabled auto-create path fails with a 403 (substatus 5300) for the
// same reason. Partition key path matches Orleans.Reminders.Cosmos's own default
// (ContainerProperties(containerName, "/PartitionKey")).
resource remindersContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2026-03-15' = {
  parent: database
  name: remindersContainerName
  properties: {
    resource: {
      id: remindersContainerName
      partitionKey: {
        paths: ['/PartitionKey']
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/*' }
        ]
        excludedPaths: [
          { path: '/StartAt/?' }
          { path: '/Period/?' }
          { path: '/_etag/?' }
        ]
      }
    }
  }
}

// Document-RAG vector container (ADR 0007). Pre-created — like the containers above — because the
// account has disableLocalAuth: true and Cosmos AAD data-plane RBAC cannot create containers, so
// CosmosDocumentIndex never creates on use. Partitioned by /ownerKey (the {userId}/{conversationId}
// grain key) so every retrieval is single-partition and owner-scoped. The 3072-dim
// text-embedding-3-large vector forces quantizedFlat (a flat index caps at 505 dims); at
// per-conversation scale (<1000 vectors) quantizedFlat is a full scan, which is correct and cheap.
// The /embedding vector path is excluded from normal indexing (it is served by the vector index).
resource docChunksContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2026-03-15' = {
  parent: database
  name: docChunksContainerName
  properties: {
    resource: {
      id: docChunksContainerName
      partitionKey: {
        paths: ['/ownerKey']
        kind: 'Hash'
      }
      vectorEmbeddingPolicy: {
        vectorEmbeddings: [
          {
            path: '/embedding'
            dataType: 'float32'
            distanceFunction: 'cosine'
            dimensions: 3072
          }
        ]
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/*' }
        ]
        excludedPaths: [
          { path: '/embedding/*' }
          { path: '/_etag/?' }
        ]
        vectorIndexes: [
          {
            path: '/embedding'
            type: 'quantizedFlat'
          }
        ]
      }
    }
  }
}

output accountName string = account.name
output documentEndpoint string = account.properties.documentEndpoint
