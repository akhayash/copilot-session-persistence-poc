targetScope = 'resourceGroup'

@description('Short environment identifier used in resource names.')
@minLength(2)
@maxLength(20)
param environmentName string = 'sessionfs'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Existing Azure Container Registry name.')
param acrName string

@description('Resource group containing the existing Azure Container Registry.')
param acrResourceGroupName string = resourceGroup().name

@description('Subscription containing the existing Azure Container Registry.')
param acrSubscriptionId string = subscription().subscriptionId

@description('ASP.NET Core application image, including tag.')
param webImage string

@description('Headless GitHub Copilot CLI sidecar image, including tag.')
param copilotCliImage string

@description('Storage integration validator image, including tag.')
param validatorImage string

@description('GitHub token used by the headless Copilot CLI.')
@secure()
param copilotGitHubToken string

@description('Shared connection token used between the Web container and its CLI sidecar.')
@secure()
param copilotConnectionToken string

@description('Microsoft Entra tenant ID that owns the Web authentication app registration.')
param webAuthTenantId string

@description('Application client ID used by Azure Container Apps built-in authentication.')
param webAuthClientId string

@description('Client secret for the Web authentication app registration.')
@secure()
param webAuthClientSecret string

@description('Minimum Web replicas. Zero enables scale-to-zero.')
@minValue(0)
@maxValue(2)
param minReplicas int = 0

@description('Maximum Web replicas used for the multi-node test.')
@minValue(1)
@maxValue(10)
param maxReplicas int = 2

@description('Deploy the Web chat Container App. The validation Job is always deployed.')
param deployWebApp bool = true

@description('Storage Account name. Must be globally unique.')
param storageAccountName string = take(
  'st${replace(replace(toLower(environmentName), '-', ''), '_', '')}${uniqueString(subscription().id, resourceGroup().id)}',
  24
)

@description('Virtual network address range.')
param vnetAddressPrefix string = '10.91.0.0/16'

@description('Container Apps infrastructure subnet.')
param containerAppsSubnetPrefix string = '10.91.0.0/23'

@description('Private Endpoint subnet.')
param privateEndpointSubnetPrefix string = '10.91.2.0/24'

var normalizedEnvironmentName = toLower(replace(environmentName, '_', '-'))
var managedEnvironmentName = 'cae-${normalizedEnvironmentName}'
var webAppName = 'ca-${normalizedEnvironmentName}-web'
var validationJobName = 'caj-${normalizedEnvironmentName}-validate'
var identityName = 'id-${normalizedEnvironmentName}'
var vnetName = 'vnet-${normalizedEnvironmentName}'
var blobPrivateDnsZoneName = 'privatelink.blob.${environment().suffixes.storage}'
var tablePrivateDnsZoneName = 'privatelink.table.${environment().suffixes.storage}'
var blobDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)
var tableDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
)
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  scope: resourceGroup(acrSubscriptionId, acrResourceGroupName)
  name: acrName
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource sessionFsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'sessionfs'
  properties: {
    publicAccess: 'None'
  }
}

resource sessionLocksContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'session-locks'
  properties: {
    publicAccess: 'None'
  }
}

resource artifactsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'artifacts'
  properties: {
    publicAccess: 'None'
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource appSessionsTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'appsessions'
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
    subnets: [
      {
        name: 'snet-container-apps'
        properties: {
          addressPrefix: containerAppsSubnetPrefix
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: privateEndpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource containerAppsSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'snet-container-apps'
}

resource privateEndpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'snet-private-endpoints'
}

resource blobPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: blobPrivateDnsZoneName
  location: 'global'
}

resource tablePrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: tablePrivateDnsZoneName
  location: 'global'
}

resource blobDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: blobPrivateDnsZone
  name: 'link-${vnetName}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

resource tableDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: tablePrivateDnsZone
  name: 'link-${vnetName}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

resource blobPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${normalizedEnvironmentName}-blob'
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          groupIds: [
            'blob'
          ]
          privateLinkServiceId: storage.id
        }
      }
    ]
    subnet: {
      id: privateEndpointSubnet.id
    }
  }
}

resource blobDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: blobPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob'
        properties: {
          privateDnsZoneId: blobPrivateDnsZone.id
        }
      }
    ]
  }
}

resource tablePrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${normalizedEnvironmentName}-table'
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        name: 'table'
        properties: {
          groupIds: [
            'table'
          ]
          privateLinkServiceId: storage.id
        }
      }
    ]
    subnet: {
      id: privateEndpointSubnet.id
    }
  }
}

resource tableDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: tablePrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'table'
        properties: {
          privateDnsZoneId: tablePrivateDnsZone.id
        }
      }
    ]
  }
}

resource storageBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, blobDataContributorRoleId)
  scope: storage
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobDataContributorRoleId
  }
}

resource storageTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, tableDataContributorRoleId)
  scope: storage
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: tableDataContributorRoleId
  }
}

module acrPullRole './acr-pull-role.bicep' = {
  name: 'acr-pull-${uniqueString(acr.id, identity.id)}'
  scope: resourceGroup(acrSubscriptionId, acrResourceGroupName)
  params: {
    acrName: acrName
    principalId: identity.properties.principalId
  }
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: managedEnvironmentName
  location: location
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: containerAppsSubnet.id
      internal: false
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

resource webApp 'Microsoft.App/containerApps@2025-07-01' = if (deployWebApp) {
  name: webAppName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: managedEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 8080
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
        transport: 'http'
      }
      registries: [
        {
          identity: identity.id
          server: acr.properties.loginServer
        }
      ]
      secrets: [
        {
          name: 'copilot-github-token'
          value: copilotGitHubToken
        }
        {
          name: 'copilot-connection-token'
          value: copilotConnectionToken
        }
        {
          name: 'web-auth-client-secret'
          value: webAuthClientSecret
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          env: [
            {
              name: 'ASPNETCORE_HTTP_PORTS'
              value: '8080'
            }
            {
              name: 'Persistence__Backend'
              value: 'AzureStorage'
            }
            {
              name: 'SessionOwnership__RequireAuthenticatedPrincipal'
              value: 'true'
            }
            {
              name: 'AzureStorage__BlobServiceUri'
              value: storage.properties.primaryEndpoints.blob
            }
            {
              name: 'AzureStorage__TableServiceUri'
              value: storage.properties.primaryEndpoints.table
            }
            {
              name: 'Copilot__CliUrl'
              value: 'http://localhost:4321'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: identity.properties.clientId
            }
            {
              name: 'COPILOT_CONNECTION_TOKEN'
              secretRef: 'copilot-connection-token'
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/api/health'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 30
              periodSeconds: 30
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
        {
          name: 'copilot-cli'
          image: copilotCliImage
          args: [
            '--headless'
            '--host'
            '0.0.0.0'
            '--port'
            '4321'
          ]
          env: [
            {
              name: 'COPILOT_GITHUB_TOKEN'
              secretRef: 'copilot-github-token'
            }
            {
              name: 'COPILOT_CONNECTION_TOKEN'
              secretRef: 'copilot-connection-token'
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '10'
              }
            }
          }
        ]
      }
    }
    workloadProfileName: 'Consumption'
  }
  dependsOn: [
    acrPullRole
    blobDnsZoneGroup
    storageBlobRole
    storageTableRole
    tableDnsZoneGroup
  ]
}

resource webAuth 'Microsoft.App/containerApps/authConfigs@2025-07-01' = if (deployWebApp) {
  parent: webApp
  name: 'current'
  properties: {
    globalValidation: {
      excludedPaths: [
        '/api/health'
      ]
      redirectToProvider: 'azureActiveDirectory'
      unauthenticatedClientAction: 'RedirectToLoginPage'
    }
    httpSettings: {
      requireHttps: true
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: webAuthClientId
          clientSecretSettingName: 'web-auth-client-secret'
          openIdIssuer: '${environment().authentication.loginEndpoint}${webAuthTenantId}/v2.0'
        }
      }
    }
    login: {
      tokenStore: {
        enabled: true
      }
    }
    platform: {
      enabled: true
    }
  }
}

resource validationJob 'Microsoft.App/jobs@2025-07-01' = {
  name: validationJobName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    configuration: {
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          identity: identity.id
          server: acr.properties.loginServer
        }
      ]
      replicaRetryLimit: 0
      replicaTimeout: 900
      triggerType: 'Manual'
    }
    environmentId: managedEnvironment.id
    template: {
      containers: [
        {
          name: 'validator'
          image: validatorImage
          env: [
            {
              name: 'AZURE_CLIENT_ID'
              value: identity.properties.clientId
            }
            {
              name: 'AZURE_STORAGE_BLOB_SERVICE_URI'
              value: storage.properties.primaryEndpoints.blob
            }
            {
              name: 'AZURE_STORAGE_TABLE_SERVICE_URI'
              value: storage.properties.primaryEndpoints.table
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
    }
    workloadProfileName: 'Consumption'
  }
  dependsOn: [
    acrPullRole
    blobDnsZoneGroup
    storageBlobRole
    storageTableRole
    tableDnsZoneGroup
  ]
}

output storageAccountName string = storage.name
output blobServiceUri string = storage.properties.primaryEndpoints.blob
output tableServiceUri string = storage.properties.primaryEndpoints.table
output containerAppsEnvironmentName string = managedEnvironment.name
output webAppName string = deployWebApp ? webApp!.name : ''
output webAppUrl string = deployWebApp ? 'https://${webApp!.properties.configuration.ingress.fqdn}' : ''
output validationJobName string = validationJob.name
output managedIdentityClientId string = identity.properties.clientId
