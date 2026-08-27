using './main.bicep'

param environmentName = 'sessionfs-dev'
param location = 'japaneast'

param acrName = readEnvironmentVariable('ACR_NAME')
param acrResourceGroupName = readEnvironmentVariable('ACR_RESOURCE_GROUP')
param acrSubscriptionId = readEnvironmentVariable('AZURE_SUBSCRIPTION_ID')

param webImage = readEnvironmentVariable('SESSIONFS_WEB_IMAGE')
param copilotCliImage = readEnvironmentVariable('COPILOT_CLI_IMAGE')
param validatorImage = readEnvironmentVariable('SESSIONFS_VALIDATOR_IMAGE')

param minReplicas = 0
param maxReplicas = 2
param deployWebApp = true

param copilotGitHubToken = readEnvironmentVariable('COPILOT_GITHUB_TOKEN')
param copilotConnectionToken = readEnvironmentVariable('COPILOT_CONNECTION_TOKEN')
param webAuthTenantId = readEnvironmentVariable('WEB_AUTH_TENANT_ID')
param webAuthClientId = readEnvironmentVariable('WEB_AUTH_CLIENT_ID')
param webAuthClientSecret = readEnvironmentVariable('WEB_AUTH_CLIENT_SECRET')
param webAuthAllowedPrincipalObjectId = readEnvironmentVariable('WEB_AUTH_ALLOWED_PRINCIPAL_OBJECT_ID')
