@description('Location for the Cosmos DB account.')
param location string

@description('Name of the serverless Cosmos DB account. Must be globally unique.')
param accountName string

@description('Cosmos DB SQL database name.')
param databaseName string

@description('Cosmos DB SQL container name used for Orleans grain storage.')
param containerName string

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

output accountName string = account.name
output documentEndpoint string = account.properties.documentEndpoint
