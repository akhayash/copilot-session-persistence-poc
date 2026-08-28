# Azure Storage mode

## Purpose

Azure Storage modeは、複数のASP.NET Core replicaがconversationとapplication stateを
共有するMulti-node検証構成です。SQLiteは使用しません。

```text
Persistence:Backend = AzureStorage

Application metadata --> Azure Table Storage appsessions
SessionFS agent state --> Azure Blob Storage sessionfs
Session exclusion     --> Azure Blob lease in session-locks
Artifact store        --> Azure Blob Storage artifacts
Execution job state   --> Azure Table Storage executionjobs
```

## Configuration

```powershell
$env:Persistence__Backend = "AzureStorage"
$env:AzureStorage__BlobServiceUri = "https://<account>.blob.core.windows.net"
$env:AzureStorage__TableServiceUri = "https://<account>.table.core.windows.net"
```

`Program.cs`はAzure Storage用のrepository、SessionFS provider、diagnostics reader、
distributed lock、Artifact storeだけをdependency injectionへ登録します。
SQLite connection factoryとdatabase initializerは登録・実行しません。

Azure configurationまたはcredentialが不正な場合は明示的に失敗します。Node-local
SQLiteへautomatic fallbackしません。

## Stored state

| State | Azure resource / key |
| --- | --- |
| Session metadata | Table `appsessions`; user owner hashごとのpartition |
| SessionFS snapshot | Container `sessionfs`; `sessions/{sessionId}/state.json` |
| Distributed lock | Container `session-locks`; sessionごとのBlob lease |
| Artifact | Container `artifacts`; session/artifact/fileごとのBlob |
| Execution job状態 | Table `executionjobs`; Python code実行ジョブのstatusとtimestamp |

SessionFSの`events.jsonl`、checkpoint、plan、workspaceは個別Blobではなく、
sessionごとの`state.json`内にvirtual nodesとして保存します。

`executionjobs`が記録するのはPython code実行のジョブ状態だけです。実行中のcodeが
読み書きするsandbox workspace自体はAzure Container Apps dynamic sessionが管理する
ephemeralな領域であり、SessionFSやArtifact Blobとは別で、session pool cooldown後に
破棄されます。詳細は
[Architecture: 5.6 Python code実行とdynamic sessionのworkspace](architecture.md)
を参照してください。

## Authentication

Azure Container Appsではuser-assigned managed identityと`DefaultAzureCredential`を使います。
Identityには`Storage Blob Data Contributor`と`Storage Table Data Contributor`を付与します。

Python code実行のdynamic session poolは別のdata planeです。同じuser-assigned
identityへ、built-in`Azure ContainerApps Session Executor`ロールをsession pool
だけへscopeして追加付与します。Storage roleとは異なるresourceへのroleのため、
`Contributor`のような広いroleは不要です（least privilege）。

Public Web ingressはAzure Container Apps built-in authenticationで保護します。
User sign-inにはMicrosoft Entra IDの既存application registrationを使います。
Application registrationがAzure resourceと別tenantにある場合も、tenant ID、client ID、
client secret、issuerを明示して構成できます。`/api/health`だけはContainer Apps probeの
ためauthentication対象外です。Application registrationを所有するtenantのuserは全員
authenticationできます。Azure Container Appsが注入する
`X-MS-CLIENT-PRINCIPAL-ID`をSHA-256 owner keyへ変換し、Table `PartitionKey`として
使用します。別userのsessionはlist、history、message、delete、diagnosticsの対象に
なりません。

現在のBicepはWeb ingressをpublicにし、Storage data planeへはBlob/Table Private Endpoint
経由で接続します。Storageでは次を無効にします。

- Public network access
- Shared Key access
- Anonymous Blob access
- Container public access

Private networkから到達できても、RBACを持たないclientはdataを読み書きできません。

## Multi-node topology

```text
Container App replica A                 Container App replica B
  ASP.NET Core                            ASP.NET Core
  Copilot CLI sidecar A                   Copilot CLI sidecar B
           \                                  /
            +------ shared Azure Storage ----+
```

各replicaは独立したCopilot CLI runtimeを持ちます。同じCLI runtimeへ複数のcustom
SessionFS provider clientを登録しません。

## Deploy

Azure Container AppsのBicep、image build、validation Job、cost特性は
[`infra/container-apps/README.md`](../infra/container-apps/README.md)を参照してください。

## Guarantees and limitations

- Blob leaseで同じsessionの同時操作を排他します。
- Blob ETagでSessionFS snapshotのlost updateを検出し、bounded retryします。
- Table、SessionFS Blob、Artifact Blobをまたぐdistributed transactionはありません。
- SessionFSはsessionごとの単一JSON Blobなので、large sessionではwrite amplificationが増えます。
- SQLite database fileの存在やnode-local filesystemに依存しません。
- Ownership導入前のlegacy `PartitionKey=session` rowは自動移行せず、user一覧へ表示しません。
