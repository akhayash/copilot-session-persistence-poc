# Backlog

現時点で判明している積み残し課題をまとめます。実装済みの制約そのものは
[Architecture](architecture.md) の「11. 現在の制約」に、検証済みの条件と結果は
[Validation](validation.md) に記録しています。ここでは **次に手を入れるとしたら何を、
どの順で、どんなトレードオフで扱うか** を扱います。

各項目の状態は次の意味で使います。

| State | 意味 |
| --- | --- |
| Open | 未着手。対応方針のみ決まっている |
| Decision needed | 実装前にownerの判断が必要 |
| Accepted | 現時点では意図的に受け入れている制約 |

---

## 1. PowerPoint生成の反復対応

**State**: Decision needed / **Impact**: 大 / **Scope**: `presentation-worker`、`Execution`、`skills`、`infra`

現在のPowerPoint生成は1回のtool callで完結するsingle-shotです。Workerは固定のcolor
palette、font、layoutでslideを組み立て、requestごとに出力directoryを空にし、renderした
slide PNGをmodelのcontextへ戻しません。結果として、renderした結果を見て要素の重なりや
text overflowを直すdesign QA loopと、既存deckへの追記・修正ができません。

構造的な原因は実行環境の分断です。Code実行ができるPython dynamic session poolには
`soffice`と`pdftoppm`が無く、poolが`EgressDisabled`のため追加導入もできません。逆に
LibreOfficeを持つcustom container poolは固定JSON APIのみでshellを持ちません。**rendering
能力と自由なcode実行環境が別poolに存在する**ことが、loopを閉じられない理由です。

### 対応方針

1. **Workerのworkspace service化**  `POST /presentations` を `build`／`render`／`text`／
   `files` の動詞APIへ分解し、`clear_outputs()` によるrequestごとの全消去をやめて
   workspace単位のdirectoryへ置き換える。Imageへpoppler、Node、pptxgenjsを追加する。
2. **Workspaceの永続化**  Custom container sessionはcooldown後に回収されるため、worker内の
   一時directoryだけでは会話をまたいだ編集が消える。既存の`ISessionFsProviderFactory`を
   使ってsession再開時にworkspaceをrehydrateする。
3. **QA loopを閉じる**  `render`結果のslide PNGをmodelのcontextへ戻す経路を作る。tool result
   がimageを運べない場合は、imageを読むsubagentへ検査させる形にする。
4. **Tool境界の再設計**  `create_presentation` を `presentation_build`／`render`／`inspect`／
   `publish` へ分割する。現在のslide数、file size、SHA-256、PDF page数の検証は捨てず、
   `publish` の最終gateとして残す。
5. **Skillの改訂**  `skills/presentation/SKILL.md` の「`create_presentation`のみを使う」方針を
   反転させ、inspectを通さずにpublishすることを禁じるloop強制の記述にする。
6. **Job modelの追随**  `PresentationExecutionCoordinator` は `toolCallId` を冪等keyにした
   単発前提のため、deck単位のworkspace識別子を別に持たせ、Artifactへのpublishを明示的な
   最終stepにする。

### 判断が必要な点

`build` の入力形式が最大の分岐です。現在の宣言的schemaを拡張する案はdeterministicで安全
ですが、palette選択やimage配置といった要求は表現しきれません。modelが書いた生成scriptを
worker内で実行する案が表現力の面では本命で、`execute_python`が既に任意code実行を許して
いる以上、新しいriskの種別が増えるわけではありません。ただし採用する場合も
`EgressDisabled`と非root実行は維持します。

もう一つの分岐がpool統合です。Python poolを畳んでcustom container poolへ集約すれば、
どのみち常時課金しているready workerを活かせますが、Azureが提供するcode interpreter型の
managedなhardeningを手放して自前containerの分離に依存することになります。

### 段階的に進める場合

フルスコープが重い場合、**1のworkspace化と3のfeedback経路**の2点だけでも「1回作って終わり」
から「見て直せる」への転換は得られます。2と4はその後で追加できます。

---

## 2. Bicepと稼働imageのドリフト

**State**: Open / **Impact**: 中 / **Scope**: `infra`、deployment手順

Artifact downloadの修正は `az containerapp update` で適用したため、稼働中のweb imageは
`sessionfs-web:pptx-download-fix-v3` で、Bicepの`webImage` parameterが参照する値と乖離して
います。次に `az deployment group create` を実行する際、`SESSIONFS_WEB_IMAGE` へ同じtagを
指定しないとimageが巻き戻り、GUID名でdownloadされる問題が再発します。

恒久対応は、動作確認済みtagを`main.bicepparam`へ書き戻すか、imageのpromotionを手作業の
`containerapp update`ではなくdeployment経由に統一することです。

---

## 3. Presentation poolのidle cost

**State**: Accepted / **Impact**: 中 / **Scope**: `infra`

Web appは`minReplicas: 0`、Python poolは`readySessionInstances: 0`でscale-to-zeroします。
一方、custom container poolはplatform制約により`readySessionInstances`が1以上必須で、
利用が無くても1 vCPU／2 GiBのworker 1台分が常時課金されます。環境全体が完全な0 nodeに
なるのは`enablePresentationSessions=false`のときだけです。

これはplatformの制約であり回避できません。コストを止める唯一の手段はfeatureごと無効化する
ことなので、常用しない環境では`enablePresentationSessions=false`を既定にする運用が妥当
です。詳細は [infra/container-apps/README](../infra/container-apps/README.md) を参照して
ください。

---

## 4. pytestのsecurity alert

**State**: Open / **Impact**: 小 / **Scope**: `presentation-worker/requirements.txt`

Dependabotがmedium severityのalertを1件報告しています。`pytest`のtmpdir handlingに関する
ものです。`pytest`はworker imageのtest実行にのみ使い、production pathでは読み込まれません
が、現在は`requirements.txt`へ他のruntime依存と同列で固定されており、imageへ同梱されます。

対応は、修正版へのversion更新に加えて、test依存をruntime依存から分離して production image
へ同梱しない構成へ変えることです。後者は依存表面を恒久的に減らします。

---

## 5. Artifactのサイズ上限と保持方針

**State**: Open / **Impact**: 小（課題1に着手する場合は中） / **Scope**: `Api`、`ArtifactStorage`

Artifact uploadは`SessionEndpoints`の`MaxArtifactUploadBytes`で10 MiB、worker側の返却は
`MAX_ARTIFACT_BYTES`で32 MiBに制限しています。現状のsingle-shot生成では十分ですが、課題1の
反復対応を入れるとiterationごとにslide PNGが生成されるため、Artifactの増加が速くなります。
保持期間、世代の掃除、上限値の見直しをあわせて検討する必要があります。

---

## 6. 既存の制約

以下はPoCのscope外として意図的に扱っていない項目です。詳細は
[Architecture](architecture.md) の「11. 現在の制約」を参照してください。

- Application roleベースのauthorization
- Ownership導入前のlegacy partitionからの自動移行
- SessionFSのwrite amplificationとBlob leaseのfencing token
- Multi-region scaling、backup、retention、encryption policy
- SQL ServerとAzure Cosmos DBのprovider実装

---

## Appendix. Multi-turn Skill実行のsandbox設計方針

課題1に着手する際、shellとPython実行をscopeへ入れる場合の設計方針をまとめます。以下は
Container Appsのplatform制約を踏まえた結論であり、実装前の判断材料です。

### 前提となるplatform制約

**Session poolはvolume mountに対応しません。** `2025-07-01`のsessionPools ARM APIにおける
`SessionContainer`のschemaは`name`／`image`／`command`／`args`／`env`／`resources`／`probes`
のみで、`volumeMounts`が存在しません。Azure Filesをsandboxへmountして状態を共有する構成は
選択肢になりません。したがって**状態はapplication側から明示的に注入・回収する以外に手段が
ない**というのが出発点です。

通常のContainer Appは Azure Files をmountできますが、Container Appsはfile shareへの
identityベースaccessに対応せず、storage account keyが必要です。sandboxへcredentialを渡さない
という本PoCのsecurity boundaryと衝突するため、web app側でも採用していません。

### 中核となる設計原則

**Sandboxはcacheであり、source of truthではない。** Custom container sessionはcooldownと
`maxAlivePeriod`で必ず回収されます。sandboxのlifetime（分単位）と会話のlifetime（日単位）は
別物なので、durabilityをsandboxに期待しません。SessionFSをsource of truthとし、sandboxは
warmな作業領域として扱います。

この原則から次が導かれます。

1. **1つのworkflowは1種類のsandboxで完結させる**  buildとrenderが別poolに分かれていると
   loopが閉じません。shell、Python、LibreOffice、renderingを1つのimageへまとめ、`exec`と
   file転送のAPIを持つcustom containerへ統合します。代償はcode interpreter型のmanaged
   hardeningを手放すことなので、`EgressDisabled`、非root実行、resource上限、sandboxへ
   identityを渡さない設定は維持します。
2. **Session identifierはconversationに対して安定させる**  連続するturnで同じwarm sandboxと
   filesystemを再利用できると、turnごとの転送が不要になります。identifierはuser入力から
   導出せず、server側のkeyを使ってapplication session IDから決定的かつ推測不能に生成します。
3. **Materialize／commitを明示的な契約にする**  turnの最初のtool callでSessionFSから必要な
   fileをsandboxへ展開し、変更操作の後に書き戻します。path→SHA-256のmanifestを持ち、差分の
   ある fileだけを転送します。毎回workspace全体を送らないことが前提です。
4. **大きなbinaryはsnapshotへ入れない**  SessionFSは単一JSON snapshotのためwrite
   amplificationがあります。PPTXやPNGはArtifact Blobへ置き、snapshotにはmanifestだけを
   保持します。
5. **同時実行は既存のBlob leaseで抑える**  identifierを安定させると複数nodeが同じsandboxへ
   到達しうるため、1 application sessionにつき実行は1つに制限します。commitはETagで冪等に
   します。
6. **長時間実行はasync jobにする**  1回の実行に時間上限があるため、turnをblockせず既存の
   `IExecutionJobRepository`のjob追跡を再利用します。

### Toolの粒度

Modelへ公開する面は狭く、契約は強くします。

| Tool | 役割 |
| --- | --- |
| `run` | workspaceをcwdとしてshell／Pythonを実行し、stdout／stderr／exit codeを返す |
| `read_file`／`write_file`／`list_files` | workspaceのfile操作 |
| `render_preview` | slideをPNGへrenderし、**imageとしてmodelのcontextへ返す** |
| `publish` | 検証を通してArtifactへ確定する唯一の経路 |

自由度は`run`が担い、`publish`が現在のslide数・file size・SHA-256・Open XML検証をgateとして
残します。`render_preview`をfirst-class toolにするのは、QA loopをskillから強制できるように
するためです。**render結果がmodelのcontextへ戻らない限りloopは閉じません。**

### 避けるべき構成

- Sandboxへshareをmountする（platformが対応しない）
- Sandboxのlifetimeにdurabilityを期待する
- Web appやCopilot CLI containerと同じ実行環境でmodelのcodeを動かす
- Tool callのたびにworkspace全体を転送する
- Sandbox内から直接Storageへaccessさせる（applicationが仲介する）

