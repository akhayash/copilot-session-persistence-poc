# Validation

## 1. Purpose

この document は、repository の実装に対して実施した検証の条件と結果を記録します。
Repository の概要と実行方法は [README](../README.md)、設計と Multi-node coordination
の仕組みは [Architecture](architecture.md) を参照してください。

環境固有の resource 名、account 名、container image 名、digest、subscription 情報は
記録しません。

## 2. Automated tests

通常の test suite は real GitHub Copilot service を呼びません。

```powershell
dotnet test CopilotSessionPersistencePoc.slnx

cd src\CopilotSessionPersistencePoc\ClientApp
npm run lint
npm run build
```

主な contract test:

- SessionFS file create、read、overwrite、append
- Directory create、list、stat、recursive remove、rename
- Path validation、ENOENT mapping、session isolation
- 別 provider instance からの persisted state read
- Application metadata の optimistic concurrency
- SQLite migrationとauthenticated user間のsession isolation
- Diagnostics preview と redaction

## 3. Local restart verification

### Conditions

- SQLite persistence backend
- Sign in 済みの headless GitHub Copilot CLI
- Web process と CLI process を conversation の途中で停止
- 再起動後も同じ SQLite database を使用

### Result

再起動前の conversation history を新しい `CopilotClient` から読み込み、以前の message
で与えた marker に follow-up message が正しく回答することを確認しました。再開後の
user / assistant events も同じ SessionFS state に追記されました。

これにより、conversation resume が active `CopilotSession` object や
`~/.copilot/session-state/{sessionId}` の host file tree に依存しないことを確認しました。

## 4. Azure Storage multi-node integration

### Conditions

- Azure Table Storage を application metadata に使用
- Azure Blob Storage を SessionFS snapshot、session lock、Artifact に使用
- 2 つの独立した repository / provider / store instance から同じ storage に接続
- Azure 上の検証では user-assigned managed identity と `DefaultAzureCredential` を使用
- Blob / Table data-plane role のみを付与し、account key と connection string は不使用
- 2026-08-27のlive testでは、一時的なPrivate Endpoint経由でAzure Storage data planeに接続

現在の`infra/container-apps` deploymentも、subscription policyに従ってBlob/Table
Private Endpointとmanaged identity RBACを使用します。Web ingressだけをpublicにします。

Azurite でも同じ integration test を実行できます。

```powershell
$env:AZURE_STORAGE_CONNECTION_STRING = "UseDevelopmentStorage=true"
dotnet test CopilotSessionPersistencePoc.slnx `
  --filter FullyQualifiedName~AzureStorageMultiNodeIntegrationTests
```

### Verified behavior

- 別 SessionFS provider instance から同じ conversation state を共有
- Concurrent append の競合時に ETag retry を行い、event を欠落させない
- Azure Table Storage の session metadata を別 repository instance から参照・更新
- 異なるowner keyのrepositoryから別userのmetadataをlist、read、deleteできない
- Artifact を別 store instance から取得し、SHA-256 と content type を維持
- Blob lease によって同一 session の二重 lock を拒否
- Lease release 後に別 instance が同じ session lock を取得
- Session 削除時に metadata、SessionFS Blob、Artifact Blob を cleanup

### Result

2026-08-27 の Azure Storage data-plane integration test:

```text
Passed: 1
Failed: 0
Skipped: 0
Duration: 55 s
Process exit code: 0
```

## 5. Known limitations

- Multi-node test は storage-level contract を検証するもので、複数の live Copilot runtime
  を同時に使う browser E2E ではありません。
- Blob lease renewal failure は operation cancellation に伝播しますが、fencing token
  による stale writer の拒否は未検証かつ未実装です。
- SessionFS は session ごとの単一 `state.json` Blob であり、large session の性能、
  retention、backup、multi-region behavior は検証対象外です。
- Artifact store は contract と cleanup を検証しています。Web API と conversation
  metadata への配線は実装・検証対象外です。

## 6. Azure Container Apps deployment

2026-08-27に、次のtopologyをAzureへdeploymentしました。

```text
Public HTTPS ingress
        |
Azure Container App (0-2 replicas)
        +-- ASP.NET Core Web
        `-- GitHub Copilot CLI sidecar
        |
Container Apps environment VNet
        |
Blob / Table Private Endpoints
        |
Azure Storage (public network access disabled)
```

確認結果:

- Public Web root: HTTP 200
- `/api/health`: `healthy`、persistence `AzureStorage`、Copilot CLI `reachable`
- Manual Azure Container Apps Job: `Succeeded`
- Public APIからsession作成、Copilot message送信、persisted history取得、session削除: 成功
- Copilot responseがpersisted historyを引き継ぐことを確認
- 最初のuser messageからsession titleを生成し、listとheaderへ反映: 成功
- Debug panelでAzure Table、SessionFS Blob、Blob lease、Artifact Blobを個別表示: 成功
- Web scaling: minimum 0、maximum 2 replicas
- Storage: public network access、Shared Key、anonymous Blob accessはすべて無効

2026-08-28に、別tenantのsingle-tenant application registrationを使って
Azure Container Apps built-in authenticationを有効化しました。

- 未認証browser requestがapplication registrationのtenantへredirect: 成功
- `/api/health`のauthentication除外: HTTP 200
- Application client ID、issuer、callback URIの反映: 確認済み
- Container App revision: Healthy

Interactive sign-in後のchat操作を含むbrowser E2Eは未実施です。
Final templateはprincipal allow-listを設定せず、application registrationを所有するtenantの
userをauthentication対象にしています。2026-08-28のlive testはsign-in redirectの確認であり、
interactive browser E2Eは未実施です。Application-level isolationは
`X-MS-CLIENT-PRINCIPAL-ID`をowner keyにしたAPI contract testで検証します。

## 7. Python code実行（dynamic sessions）

BicepはPython code実行用に`Microsoft.App/sessionPools`（`PythonLTS`built-in
container、dynamic pool management、`EgressDisabled`）とAzure Table Storage
`executionjobs`を追加しました。設計上の制約は次のとおりです。

- 1回のcode実行は built-in code interpreter の上限である最大220秒
- Session poolのnetworkは`EgressDisabled`。Sandbox内のcodeは外部networkへ到達できない
- Sandbox containerにはAzure Storageのcredentialを渡さない。SessionFSやArtifact
  Blobへの直接アクセスはできない
- Data-plane APIは現時点で`2025-10-02-preview`のpreview API versionを使用する
- Web identityにはsession poolへscopeしたbuilt-in
  `Azure ContainerApps Session Executor`ロールのみを付与する

**この構成のAzure上でのlive validationは未実施です。** Session pool、role
assignment、environment variableの配線はBicep buildで構文検証済みですが、実際の
Azure subscriptionへのdeployment、code実行のsmoke test、cold start latencyの計測は
まだ行っていません。実施後は本sectionに条件と結果を追記します。
