// Hive BDC as an Azure managed application: one instance per customer, in the managed resource group.
// Built into mainTemplate.json (bash deploy/azure/test-template.sh checks that both match). Managed applications
// accept ARM language version 1.0 only, so no Bicep feature that needs 2.0 is used here.

@description('Region of the instance.')
param location string = resourceGroup().location

@description('App Service plan size. B1 handles a few thousand documents a day.')
@allowed([
  'B1'
  'B2'
  'P0v3'
  'P1v3'
])
param skuName string = 'B1'

@description('Application (client) ID of the app registration "Hive BDC sign-in" that New-HiveBDCSignInApp.ps1 created in this tenant.')
@minLength(36)
@maxLength(36)
param signInClientId string

@description('Base URI of the deployment package; set by Azure during the deployment.')
param _artifactsLocation string = deployment().properties.templateLink.uri

@description('SAS token for _artifactsLocation; set by Azure during the deployment and valid only while it runs.')
@secure()
param _artifactsLocationSasToken string = ''

var suffix = uniqueString(resourceGroup().id)
var planName = 'plan-hivebdc-${suffix}'
var siteName = 'hivebdc-${suffix}'
var storageName = 'sthivebdc${suffix}'
var vaultName = 'kv-hivebdc-${suffix}'
var signInIdentityName = 'id-hivebdc-signin-${suffix}'
var containerName = 'hivebdc'
var signInSecretSetting = 'OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID'
var packageUri = uri(_artifactsLocation, 'app/hivebdc.zip${_artifactsLocationSasToken}')

var roles = {
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
}

resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 14
    }
    isVersioningEnabled: true
  }
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobService
  name: containerName
  properties: {
    publicAccess: 'None'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2025-05-01' = {
  name: vaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
  }
}

// Used only by App Service sign-in (federated credential on the customer's app registration); it gets no role.
resource signInIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: signInIdentityName
  location: location
}

resource plan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: {
    name: skuName
  }
  properties: {
    reserved: true
  }
}

resource site 'Microsoft.Web/sites@2025-03-01' = {
  name: siteName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${signInIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet HiveBDC.dll'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      healthCheckPath: '/healthz'
      appSettings: [
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'HiveBDC__RunMode'
          value: 'azure'
        }
        {
          name: 'HiveBDC__Azure__TableEndpoint'
          value: storage.properties.primaryEndpoints.table
        }
        {
          name: 'HiveBDC__Azure__SettingsBlobUrl'
          value: '${storage.properties.primaryEndpoints.blob}${containerName}/settings.json'
        }
        {
          name: 'HiveBDC__Azure__KeyVaultUrl'
          value: vault.properties.vaultUri
        }
        {
          name: 'HiveBDC__LogDir'
          value: '/home/LogFiles/hivebdc'
        }
        {
          name: signInSecretSetting
          value: signInIdentity.properties.clientId
        }
      ]
    }
  }
}

resource slotSettings 'Microsoft.Web/sites/config@2025-03-01' = {
  parent: site
  name: 'slotConfigNames'
  properties: {
    appSettingNames: [
      signInSecretSetting
    ]
  }
}

resource ftpCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: site
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource scmCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: site
  name: 'scm'
  properties: {
    allow: false
  }
}

resource signIn 'Microsoft.Web/sites/config@2025-03-01' = {
  parent: site
  name: 'authsettingsV2'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
      excludedPaths: [
        '/healthz'
        '/inbound'
        '/inbound/*'
      ]
    }
    httpSettings: {
      requireHttps: true
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: signInClientId
          clientSecretSettingName: signInSecretSetting
          openIdIssuer: '${environment().authentication.loginEndpoint}${subscription().tenantId}/v2.0'
        }
      }
    }
    login: {
      tokenStore: {
        enabled: true
      }
    }
  }
}

resource tableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, site.id, roles.storageTableDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageTableDataContributor)
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, site.id, roles.storageBlobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource vaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, site.id, roles.keyVaultSecretsOfficer)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsOfficer)
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// The package is copied into the app while the deployment's SAS is valid; WEBSITE_RUN_FROM_PACKAGE=1 then runs it
// from /home/data/SitePackages, so nothing depends on the offer's storage after the deployment.
resource package 'Microsoft.Web/sites/extensions@2025-03-01' = {
  parent: site
  name: 'onedeploy'
  // The type definition has no properties; packageUri and type are documented in
  // https://learn.microsoft.com/azure/app-service/deploy-zip#deploy-to-network-secured-apps
  #disable-next-line BCP187
  properties: {
    packageUri: packageUri
    type: 'zip'
  }
  dependsOn: [
    signIn
    slotSettings
    tableRole
    blobRole
    vaultRole
    container
  ]
}

var hostName = site.properties.defaultHostName

@description('Address of the Hive BDC web console.')
output consoleUrl string = uri('https://${hostName}', '/')

@description('Redirect URI that step 2 of New-HiveBDCSignInApp.ps1 adds to the app registration.')
output signInRedirectUri string = uri('https://${hostName}', '/.auth/login/aad/callback')

@description('Object ID of the sign-in identity that step 2 trusts on the app registration.')
output signInIdentityObjectId string = signInIdentity.properties.principalId

@description('Step 2 of the sign-in setup, to run once after the installation.')
output signInStep2 string = './New-HiveBDCSignInApp.ps1 -ClientId ${signInClientId} -RedirectUri https://${hostName}/.auth/login/aad/callback -IdentityObjectId ${signInIdentity.properties.principalId}'

@description('Address an ERP posts documents to (HTTPS source): /inbound/{documentType}/{company}.')
output inboundUrl string = uri('https://${hostName}', '/inbound/')