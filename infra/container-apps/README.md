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

## Python dynamic session pool

BicepはPython code実行用に`Microsoft.App/sessionPools`（`PythonLTS`built-in
container、`2025-07-01`）を1つ作成します。この session pool はSessionFSやArtifact
Blobとは別のephemeralなsandboxで、agentが生成したPython codeだけを実行します。

| 項目 | 設定 |
| --- | --- |
| Container type | `PythonLTS`（built-in、custom containerではない） |
| Pool management | `Dynamic`（Container Appsがsandbox instanceを自動的に割当・回収） |
| Lifecycle | `Timed`。Idle sandboxは`cooldownPeriodInSeconds`後に回収される |
| Cooldown period | `sessionPoolCooldownPeriodInSeconds`パラメータ。300～3600秒、既定300秒 |
| 同時実行数 | `sessionPoolMaxConcurrentSessions`パラメータ。既定5 |
| Ready instance数 | `sessionPoolReadySessionInstances`パラメータ。既定0（pre-warm sandboxを持たずidle costを最小化） |
| Network | `EgressDisabled`。Sandbox内のcodeはinternetを含む外部networkへ到達できない |

`readySessionInstances: 0`は初回実行にcold start latencyが発生することを意味します。
低latencyが必要な場合は値を増やしてください（idle costとのtrade-off）。

Data-plane accessはbuilt-in `Azure ContainerApps Session Executor`ロール
（role ID `0fb8eba5-a2bb-4abe-b1c1-49dfad359bb0`）をsession poolだけへscopeして
Web identityに付与します。Session poolはStorage accountや他のresourceを操作しないため、
`Contributor`のような広いroleは付与しません。Sandbox container自体にはStorage
credentialを一切渡さないため、生成されたcodeがAzure Storageへ直接アクセスすることは
できません。

Web containerには次のenvironment variableを渡します。

| Environment variable | 値 |
| --- | --- |
| `DynamicSessions__Enabled` | `true` |
| `DynamicSessions__PoolManagementEndpoint` | Session poolの`properties.poolManagementEndpoint` |
| `DynamicSessions__ApiVersion` | `2025-10-02-preview`（data-plane API。preview版であることに注意） |
| `AzureStorage__ExecutionJobsTable` | `executionjobs`（実行ジョブの状態を記録するAzure Table） |

Application側の制約は次のとおりです。詳細は
[`docs/architecture.md`](../../docs/architecture.md)を参照してください。

- 1回のcode実行は最大220秒（built-in code interpreterのhard limit）
- Sandboxのegressは無効。外部APIやpackage registryへのnetwork呼び出しは失敗する
- Sandboxにcredentialを注入しないため、SessionFSやArtifact Blobへの直接アクセスはできない
- 現時点でAzure上のlive validationは未実施（`docs/validation.md`を参照）



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
authenticationできます。Platformが注入する`X-MS-CLIENT-PRINCIPAL-ID`をapplicationが
SHA-256 owner keyへ変換し、session metadataをuserごとにpartitionします。別userのsession
IDを指定したrequestもmetadata lookupで拒否されます。

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
