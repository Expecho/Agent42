@description('Location for the Container App.')
param location string

@description('Name of the Container App.')
param containerAppName string

@description('Name of the existing Container Apps managed environment.')
param environmentName string

@description('Resource group containing the existing Container Apps managed environment.')
param environmentResourceGroup string

@description('Login server of the existing Azure Container Registry, e.g. myregistry.azurecr.io.')
param registryLoginServer string

@secure()
@description('Admin username for the Azure Container Registry.')
param registryUsername string

@secure()
@description('Admin password for the Azure Container Registry.')
param registryPassword string

@description('Full container image reference, e.g. myregistry.azurecr.io/agent42-backend:latest.')
param containerImage string

@description('Cosmos DB endpoint used for grain state (identity-based auth, no key).')
param cosmosAccountEndpoint string

@description('Endpoint of the Azure AI Foundry / Azure OpenAI resource used by the agent.')
param azureOpenAIEndpoint string

@description('Model deployment name used for chat completions.')
param azureOpenAIDeploymentName string

@description('Entra ID tenant id used for DefaultAzureCredential when calling Azure OpenAI.')
param azureOpenAITenantId string

@description('OTLP endpoint for OpenTelemetry export. Leave empty to disable OTLP export.')
param otlpEndpoint string

@description('Client id of the Entra ID app registration used by built-in authentication (Easy Auth).')
param authClientId string

@secure()
@description('Client secret of the Entra ID app registration used by built-in authentication.')
param authClientSecret string

@description('Entra ID tenant id that issues sign-in tokens for built-in authentication.')
param authTenantId string

@description('Object ids of the only Entra ID users allowed to sign in. Defense-in-depth next to the assignment requirement on the enterprise app.')
param authAllowedUserObjectIds array

@description('CPU cores allocated to the container, e.g. 0.5.')
param cpu string

@description('Memory allocated to the container, e.g. 1Gi.')
param memory string

@description('Minimum replica count. Orleans localhost clustering (used by this single-instance deployment) only supports one instance.')
param minReplicas int

@description('Maximum replica count. Orleans localhost clustering (used by this single-instance deployment) only supports one instance.')
param maxReplicas int

@description('Tags applied to the Container App.')
param tags object

resource containerApp 'Microsoft.App/containerApps@2026-01-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: resourceId(environmentResourceGroup, 'Microsoft.App/managedEnvironments', environmentName)
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: registryLoginServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: [
        {
          name: 'registry-password'
          value: registryPassword
        }
        {
          name: 'microsoft-provider-authentication-secret'
          value: authClientSecret
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'backend'
          image: containerImage
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__cosmos', value: cosmosAccountEndpoint }
            { name: 'AzureOpenAI__Endpoint', value: azureOpenAIEndpoint }
            { name: 'AzureOpenAI__DeploymentName', value: azureOpenAIDeploymentName }
            { name: 'AzureOpenAI__TenantId', value: azureOpenAITenantId }
            { name: 'OTEL_SERVICE_NAME', value: containerAppName }
            { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: otlpEndpoint }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

// Built-in authentication (Easy Auth): every request must carry a Microsoft Entra
// sign-in; unauthenticated browsers are redirected to the Entra login page.
// Primary access control is the assignment requirement on the enterprise app
// (only explicitly assigned users can complete sign-in); allowedPrincipals
// repeats the allowlist here so the restriction survives portal config drift.
resource authConfig 'Microsoft.App/containerApps/authConfigs@2026-01-01' = {
  parent: containerApp
  name: 'current'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: authClientId
          clientSecretSettingName: 'microsoft-provider-authentication-secret'
          openIdIssuer: '${environment().authentication.loginEndpoint}${authTenantId}/v2.0'
        }
        validation: {
          defaultAuthorizationPolicy: {
            allowedPrincipals: {
              identities: authAllowedUserObjectIds
            }
          }
        }
      }
    }
  }
}

output principalId string = containerApp.identity.principalId
output fqdn string = containerApp.properties.configuration.ingress.fqdn
