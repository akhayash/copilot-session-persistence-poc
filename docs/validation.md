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
- PowerPoint worker manifest、PPTX/PDF/PNG/JSON signature、SHA-256、size、slide count
- Presentation session cleanup timeout、invalid outputのpublish拒否

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
- PowerPoint contentは構造化されたtitle/body/highlightから生成します。任意templateの
  upload／再編集、chartやimageの自動生成は検証対象外です。

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

Interactive sign-in後のchat操作を含むbrowser E2Eを2026-08-30に実施しました。
Final templateはprincipal allow-listを設定せず、application registrationを所有するtenantの
userをauthentication対象にしています。別user間のlive browser isolationは未実施です。
Application-level isolationは`X-MS-CLIENT-PRINCIPAL-ID`をowner keyにしたAPI contract
testで検証します。

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

### 2026-08-30 live validation

Azure subscriptionへsession pool、role assignment、`executionjobs` Table、Web revisionを
deploymentし、次の順で確認しました。

1. Chatを介さずDynamic Sessions REST APIを直接呼び、`/mnt/data/probe.txt`を作成
2. Live file-list responseが`name`、`type: "file"`、`sizeInBytes`に加え、root directoryを
   `directory: "."`として返すことを確認
3. `python-pptx`で3-slide、30,061 bytesのPPTXをsandbox内に生成
4. File APIから同一sizeでdownloadし、Open XML package validationとslide XML 3件を確認
5. LibreOfficeで3-page PDFへ変換し、各slideを画像化してvisual inspectionを実施
6. Microsoft Entra IDでWebへinteractive sign-inし、chatの`execute_python`から
   `chat-integration-test.pptx`を生成
7. Artifact一覧に30 KBのPPTXが表示され、Web APIからdownloadできることを確認
8. DownloadしたPPTXのOpen XML validation、3-slide render、SHA-256取得を実施
9. Page reload後に過去conversationとArtifact linkが再表示されることを確認

初回試験ではclientがroot directoryを空文字だけに限定していたため、live serviceが返す
`"."`を除外し、sandbox内で生成済みのfileをArtifactへpublishできませんでした。
`AzureDynamicSessionsClient`を空文字と`"."`の両方へ対応させ、live responseと同じcontract
をunit testへ固定しました。

Chat統合前に直接REST probeとPPTX package検証を行うことで、Dynamic Sessions、
file-list contract、download、PPTX生成、Artifact broker、Copilot tool invocationを
段階的に切り分けています。Cold start latencyの継続的な計測と別user間のlive isolationは
未実施です。

## 8. PowerPoint Skillとcustom container session

2026-08-30に、PowerPoint専用のAzure Container Apps custom container session poolと
single-shot版GitHub Copilot Skillをdeploymentし、次の順で検証しました。

1. Worker imageをAzure Container Registryでbuild
2. `CustomContainer` session poolを`EgressDisabled`、image pull identity
   `lifecycle: None`、ready instance 1で作成
3. 検証時だけ現在のAzure userへpool限定の`Azure ContainerApps Session Executor`
   roleを付与し、Worker APIをChatから切り離して直接probe
4. `POST /presentations`から3-slideのPPTX、PDF、3 PNG、`validation.json`を生成
5. Manifestの全fileについてsizeとSHA-256をdownload後のbytesと照合
6. PPTXのOpen XML package、manifest／package／audit JSONのslide count 3を照合
7. Render済みPNGをfresh-eyes reviewし、title decoration、余白、footer、highlight
   layoutを修正して再build／再deployment
8. 再生成した全6 Artifactのvalidationとhashを再確認。Visual reviewでblocking defectなし
9. 一時的に付与したuser role assignmentを削除し、Web managed identityだけに
   pool-scoped Session Executor roleを残した
10. 通常の自然文だけで3枚の日本語PowerPointを依頼
11. SessionFS eventで`custom:create_presentation`のtool invocationを確認
12. Artifact BlobへPPTX、PDF、3 PNG、validation JSONがpublishされ、Web UIへ表示
13. Page reload後もconversationと6 Artifactが再表示されることを確認

Web側のtrust boundaryでは、manifestのfilename、extension、MIME type、file count、
size、aggregate size、SHA-256に加え、PPTXのZIP/Open XML members、PDF／PNG signature、
JSON parseを再検証します。全fileをdownload・検証してからpublishし、途中のBlob uploadが
失敗した場合はjob単位でrollbackします。SandboxへStorage credential、SAS、managed
identityは渡していません。

2026-08-31にmulti-turn workspace版を実装し、workerの`exec`／`files`／`render` APIを
18件のpytest、.NET client契約と`ToolBinaryResult(image/png)`構築をunit test、
既存47件を含む53件の.NET test suiteで検証しました。Azure Container Appsへworker/Web/Skill
imageをdeploymentし、pool management endpoint経由でfile write、Python実行、PPTX生成、
Open XML検証、slide PNG返却、render一時fileの非表示、file削除をlive確認しました。

2026-08-31の最初のWeb UI E2Eでは、system promptがlegacy
`create_presentation`を優先していたため、1枚指定に対してtitle slideを含む2枚が生成されました。
`EnableLegacyCreateTool`を既定falseとしてlegacy Toolを非公開にし、system promptとSkillを
workspace Tool必須へ修正しました。再deployment後の新規sessionでは次を確認しました。

| 確認項目 | 結果 |
| --- | --- |
| Tool選択 | `create_presentation` 0回、`pptx_run` → `pptx_preview`を確認 |
| SessionFS | `/presentation/azure-architecture-deck/azure_solution.pptx`を確認 |
| browser download | `azure_solution.pptx`、29,616 bytes |
| PPTX package | Open XML正常、slide 1枚 |
| 日本語表示 | PowerPoint renderで正常 |
| 図の品質 | 不合格。複数の矢印labelが重なったままpublishされた |

これによりworkspace経路と画像返却の実model invocationは確認できましたが、prompt／Skillの
指示だけではfix-and-verify loopを保証できないことも判明しました。publish前の再修正・
再previewをTool側で強制する課題は [Backlog](backlog.md) に記録します。

## 9. Artifactのbrowser download

2026-08-30に、生成済みArtifactをbrowserへ渡す経路をAzure上で検証しました。

当初は、Artifactが拡張子のないGUID名でdownloadされPowerPointで開けない事象が報告され
ました。切り分けの結果、原因は次の2点でした。

1. 認証済みArtifact APIへ直接navigationしていたため、sign-in期限切れ時にsign-in pageへ
   cross-originのredirectが返り、browserが`download`属性を無視していた
2. `index.html`を`Cache-Control`なしで配信していたため、browserがSPA shellをheuristicに
   cacheし、deployment後も古いclientを実行し続けていた

対応として、clientでのArtifact取得をObject URL経由の明示file name保存へ変更し、redirect
された応答をArtifactとして保存しないようにしました。あわせてSPA shellを
`no-cache, must-revalidate`、content hash付き`/assets/*`を`immutable`で配信します。
Object URLはdownload確定前に失効させません。

### Result

| 確認項目 | 結果 |
| --- | --- |
| SPA shellの`Cache-Control` | `must-revalidate, no-cache` |
| `/assets/*`の`Cache-Control` | `public, max-age=31536000, immutable` |
| PPTXのdownload file name | `web_system_validation_report.pptx` |
| PPTXのpackage | 33,203 bytes、Open XML、slide 3枚 |
| PDFのdownload | 137,271 bytes、`%PDF-`署名 |
| slide PNG 3枚と`validation.json` | 正しいfile nameで取得 |

なお検証中にbrowser automationが記録したGUID名のfileは、automation側がdownloadを
横取りして一時保存した際の名前であり、application側の挙動ではありません。
