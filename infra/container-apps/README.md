# Azure Container Apps validation environment

このBicepは、Multi-node chat Web applicationとStorage integration validatorを
同じAzure Container Apps environmentへ配置します。

| Workload | Azure resource | Scaling |
| --- | --- | --- |
| Chat UI / API + Copilot CLI sidecar | Azure Container App | HTTP、0～2 replicas |
| Blob/Table integration validator | Azure Container Apps Job | Manual、実行時のみ1 replica |

Web replicaごとにASP.NET Core containerとheadless GitHub Copilot CLI sidecarが
一緒に起動します。各replicaは独立したCLI runtimeを持ち、conversation state、
metadata、Artifact、distributed lockだけをAzure Storageで共有します。

Web ingressはpublicですが、Azure Container Apps built-in authenticationで
Microsoft Entra ID sign-inを要求します。Storageはsubscription policyに従ってpublic
network accessを無効にし、Blob/Table Private Endpoint経由で接続します。Shared Keyと
container public accessも無効にし、user-assigned managed identityへ付与した
`Storage Blob Data Contributor`と`Storage Table Data Contributor`だけでdata planeへ
アクセスします。

## Web authentication

Microsoft Entra IDでWeb application用のapplication registrationを1つ作成します。
Azure resourceと別tenantのregistrationも使用できます。

必要な値:

- Directory tenant ID
- Application client ID
- Client secret
- Container App の application origin

Web redirect URI は Container App の application origin に
`/.auth/login/aad/callback` を付けた値です。実環境の URL は source や parameter file
へ記録しません。

初回deploymentではContainer App URLがまだ決まっていないため、次の順序で設定します。

1. Microsoft Entra IDでsingle-tenant application registrationを作成する
2. Client secretを作成する
3. ID tokenの発行を有効にする
4. BicepをdeploymentしてContainer Appのapplication originを確認する
5. Application registrationへcallback URIをWeb redirect URIとして追加する

Application registrationを作成したtenantの値をdeployment前に設定します。

```powershell
$env:WEB_AUTH_TENANT_ID = '<directory-tenant-id>'
$env:WEB_AUTH_CLIENT_ID = '<application-client-id>'
$env:WEB_AUTH_CLIENT_SECRET = '<client-secret>'
```

未認証browserはMicrosoft sign-inへredirectします。`/api/health`はliveness probeのため
authentication対象外です。Client secretはContainer Apps secretに保存し、sourceや
parameter fileへ書きません。Application registrationを所有するtenantのuserは全員
authenticationできます。このPoCは全sessionを1つの共有namespaceに保存するため、sign-in
したuserはほかのuserが作成したsessionも参照・更新・削除できます。

Application registrationのtenantとAzure Container Appのtenantは同一である必要は
ありません。BicepはDirectory tenant IDからissuerを構成し、既存registrationの
client IDとclient secretをContainer Apps built-in authenticationへ渡します。

Client secretには有効期限があります。期限前にapplication registrationで新しいsecretを
作成し、deployment parameterを更新して再deploymentした後、古いsecretを削除します。

## Cost characteristics

`minReplicas: 0`のConsumption workloadはrequestがないとscale to zeroになるため、
Web containerとCLI sidecarのidle computeは発生しません。Manual Jobも実行中だけ
computeを使用します。

- Blob/Table用Azure Private Endpoint 2個
- Azure Private DNS zone 2個
- Storage Accountの保存容量とtransaction
- Azure Container Registryの固定料金とimage storage
- request受信時のAzure Container Apps compute/request

このtemplateではLog Analytics workspaceもAzure Monitor log destinationも構成せず、
検証環境の固定費を抑えています。ただし、Storage policyによりPrivate Endpointが必要なため、
既存Azure Container Registryを除いても月$16～17程度のnetwork固定費が残ります。

## Validation environment

Public Webとmanual validation Jobで次を確認します。実行結果は
[`docs/validation.md`](../../docs/validation.md)に記録します。

- Web scaling: 0～2 replicas
- Web health: `healthy`
- Persistence backend: `AzureStorage`
- Copilot CLI sidecar: `reachable`
- Manual validation Job: `Succeeded`
- Microsoft Entra ID sign-inへのredirect
- Session create/message/history/delete smoke test

> Public Web APIはMicrosoft Entra ID sign-inで保護します。Application registrationの
> client secretには有効期限があるため、期限前にrotateしてください。

## Build images

```powershell
az acr build `
  --registry <registry-name> `
  --image <web-image>:<tag> `
  --file Dockerfile .

az acr build `
  --registry <registry-name> `
  --image <copilot-cli-image>:<tag> `
  --file tests\Dockerfile.copilot-cli .

az acr build `
  --registry <registry-name> `
  --image <validator-image>:<tag> `
  --file tests\Dockerfile.azure-integration .
```

GitHub Copilot CLIには公式pre-built container imageがないため、GitHub release binary
から専用imageをbuildします。

## Validate and deploy

```powershell
$env:COPILOT_GITHUB_TOKEN = '<token>'
$env:COPILOT_CONNECTION_TOKEN = '<random-token>'
$env:AZURE_SUBSCRIPTION_ID = '<subscription-id>'
$env:ACR_NAME = '<registry-name>'
$env:ACR_RESOURCE_GROUP = '<registry-resource-group>'
$env:SESSIONFS_WEB_IMAGE = '<registry>.azurecr.io/sessionfs-web:<tag>'
$env:COPILOT_CLI_IMAGE = '<registry>.azurecr.io/copilot-cli:<tag>'
$env:SESSIONFS_VALIDATOR_IMAGE = '<registry>.azurecr.io/sessionfs-validator:<tag>'
$env:WEB_AUTH_TENANT_ID = '<directory-tenant-id>'
$env:WEB_AUTH_CLIENT_ID = '<application-client-id>'
$env:WEB_AUTH_CLIENT_SECRET = '<client-secret>'

az deployment group validate `
  --resource-group <resource-group> `
  --template-file infra\container-apps\main.bicep `
  --parameters infra\container-apps\main.bicepparam

az deployment group create `
  --resource-group <resource-group> `
  --template-file infra\container-apps\main.bicep `
  --parameters infra\container-apps\main.bicepparam

Remove-Item Env:\COPILOT_GITHUB_TOKEN
Remove-Item Env:\COPILOT_CONNECTION_TOKEN
Remove-Item Env:\AZURE_SUBSCRIPTION_ID
Remove-Item Env:\ACR_NAME
Remove-Item Env:\ACR_RESOURCE_GROUP
Remove-Item Env:\SESSIONFS_WEB_IMAGE
Remove-Item Env:\COPILOT_CLI_IMAGE
Remove-Item Env:\SESSIONFS_VALIDATOR_IMAGE
Remove-Item Env:\WEB_AUTH_TENANT_ID
Remove-Item Env:\WEB_AUTH_CLIENT_ID
Remove-Item Env:\WEB_AUTH_CLIENT_SECRET
```

Tokenをsource、parameter file、deployment outputへ保存しないでください。Productionでは
Azure Key Vault referenceまたはper-user OAuthへの変更が必要です。

## Run the validator

```powershell
az containerapp job start `
  --resource-group <resource-group> `
  --name <validation-job-name>

az containerapp job execution list `
  --resource-group <resource-group> `
  --name <validation-job-name> `
  --output table
```

## Scale behavior

```powershell
az containerapp replica list `
  --resource-group <resource-group> `
  --name <container-app-name> `
  --output table
```

HTTP requestがない期間はreplica数が0になります。次のrequestでcold startが発生し、
ASP.NET CoreとCopilot CLI sidecarの両方が起動してからresponseを返すため、初回response
は遅くなります。

## Remove the environment

検証専用resource groupを使っている場合:

```powershell
az group delete --name <validation-resource-group>
```

共有resource groupへ配置した場合はdeploymentで作られたresourceを個別に削除してください。
