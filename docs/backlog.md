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

**State**: Resolved / **Impact**: 大 / **Scope**: `presentation-worker`、`Execution`、`skills`、`infra`

workspace API として `pptx_run` / `pptx_files` / `pptx_preview` / `pptx_publish` を実装しました。
stable deck identifier で同じ custom container session を再利用し、SessionFS との
materialize / commit で sandbox 回収後も復元します。`pptx_preview` は
`ToolResultObject.BinaryResultsForLlm` に slide PNG を載せるため、model が render 結果を見て
修正できます。

Python dynamic session pool は既存の汎用 `execute_python` 用に温存しました。presentation
Skill は custom presentation pool だけを使うため、PowerPoint workflow 内の能力分断は解消
しています。旧 `create_presentation` は後方互換用に実装を残しますが、
`PresentationSessions:EnableLegacyCreateTool`の明示設定がない限りmodelへ公開しません。

---

## 2. PowerPoint visual QA gate

**State**: Resolved / **Impact**: 大 / **Scope**: `Execution`、`SessionFS`、`tests`

実modelによるWeb UI E2Eで、`pptx_run`と`pptx_preview`は呼ばれましたが、矢印labelが重なった
slideがそのままpublishされました。Skillとsystem promptが要求する「preview後に最低1回修正し、
再previewする」は、modelへの文章指示だけでは保証できません。

`pptx_publish`側で、対象PPTXについて次の状態を署名付きSessionFS node
`/internal/presentation-qa/{deckId}.json`へatomicに保持し、条件未達ならpublishを拒否する
gateを実装しました。modelがnodeを変更しても署名検証に失敗し、状態なしとしてfail closedに
なります。

1. 初回preview時のPPTX hash
2. 初回preview後にPPTX hashが変化したこと
3. 変更後のhashに対して再previewが成功したこと
4. publish対象のhashが最終preview時と一致すること

PPTXの変更判定はZIP timestampを無視するcanonical member-content hashを使います。publishは
deck IDとhashから決定したArtifact IDを使い、response喪失後のretryでも既存Artifactを返します。
publish後に別内容をpreviewすると新しいfix-and-verify cycleを開始します。
これは見た目の良さ自体を完全には判定しませんが、少なくともfix-and-verify loopをTool
contractとして強制します。

---

## 3. Bicepと稼働imageのドリフト

**State**: Open / **Impact**: 中 / **Scope**: `infra`、deployment手順

停止前に検証したweb imageは`sessionfs-web:multi-turn-pptx-v5`、presentation workerは
`presentation-worker:multi-turn-v4`、Copilot CLI sidecarは
`copilot-cli:multi-turn-pptx-v5`です。Bicep parameterはenvironment variableを参照するため、
次回`az deployment group create`でも`SESSIONFS_WEB_IMAGE`、
`PRESENTATION_WORKER_IMAGE`、`COPILOT_CLI_IMAGE`へこの組み合わせを指定する必要があります。

恒久対応は、動作確認済みtagを`main.bicepparam`へ書き戻すか、imageのpromotionを手作業の
`containerapp update`ではなくdeployment経由に統一することです。

---

## 4. PowerPoint geometryの自動検査

**State**: Open / **Impact**: 大 / **Scope**: `presentation-worker`、`Execution`

fix-and-verify gateを通過したdeckでも、connectorがnodeやlabelを横切る問題が残りました。
画像をmodelへ返すだけでは、modelが見落とす、または修正が不十分な場合があります。

次の段階では、workerでslide geometryを解析し、少なくとも次をmachine-readableなwarningまたは
validation failureとして`pptx_preview`へ返す必要があります。

- connector segmentと、始点・終点以外のnode rectangleとの交差
- connectorと独立したtext box（edge label）との交差
- node rectangle同士の意図しない重なり
- slide外へ出たshape、0.3 inch未満の過密なgap

誤検知を避けるため、まずwarningとしてmodelへ返し、実deck corpusで閾値を調整してから
publish gateへ組み込みます。

---

## 5. Presentation poolのidle cost

**State**: Resolved operationally / **Impact**: 中 / **Scope**: `infra`

Web appは`minReplicas: 0`、Python poolは`readySessionInstances: 0`でscale-to-zeroします。
一方、custom container poolはplatform制約により`readySessionInstances`が1以上必須で、
利用が無くても1 vCPU／2 GiBのworker 1台分が常時課金されます。環境全体が完全な0 nodeに
なるのは`enablePresentationSessions=false`のときだけです。

これはplatformの制約であり、poolを残したまま回避できません。そのため
`main.bicepparam`は`enablePresentationSessions=false`を既定とし、検証終了時はWeb appを停止、
custom poolを削除します。Bicepへ`enablePresentationSessions=true`をoverrideすればpoolとRBACを
再作成できます。詳細は [infra/container-apps/README](../infra/container-apps/README.md) を
参照してください。

---

## 6. pytestのsecurity alert

**State**: Resolved / **Impact**: 小 / **Scope**: `presentation-worker/requirements*.txt`

Dependabotがmedium severityのalertを1件報告しています。`pytest`のtmpdir handlingに関する
ものです。`pytest`はworker imageのtest実行にのみ使い、production pathでは読み込まれません
が、現在は`requirements.txt`へ他のruntime依存と同列で固定されており、imageへ同梱されます。

`pytest`を9.0.3へ更新し、`requirements-dev.txt`へ分離しました。production image は
runtime用`requirements.txt`だけをinstallし、testsもcopyしません。

---

## 7. Artifactのサイズ上限と保持方針

**State**: Open / **Impact**: 小（課題1に着手する場合は中） / **Scope**: `Api`、`ArtifactStorage`

Artifact uploadは`SessionEndpoints`の`MaxArtifactUploadBytes`で10 MiB、worker側の返却は
`MAX_ARTIFACT_BYTES`で32 MiBに制限しています。現状のsingle-shot生成では十分ですが、課題1の
反復対応を入れるとiterationごとにslide PNGが生成されるため、Artifactの増加が速くなります。
保持期間、世代の掃除、上限値の見直しをあわせて検討する必要があります。

---

## 8. 既存の制約

以下はPoCのscope外として意図的に扱っていない項目です。詳細は
[Architecture](architecture.md) の「11. 現在の制約」を参照してください。

- Application roleベースのauthorization
- Ownership導入前のlegacy partitionからの自動移行
- SessionFSのwrite amplificationとBlob leaseのfencing token
- Multi-region scaling、backup、retention、encryption policy
- SQL ServerとAzure Cosmos DBのprovider実装

---

## Appendix. Multi-turn Skill実行のsandbox設計方針

課題1の実装時に採用した、shellとPython実行をscopeへ入れる場合の設計方針をまとめます。以下は
Container Appsのplatform制約を踏まえた結論と実装理由です。

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
