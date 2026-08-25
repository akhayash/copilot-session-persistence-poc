# Copilot Session Persistence PoC

GitHub Copilot SDK の session persistence を、React、ASP.NET Core、SQLite で
検証する local Web application です。

この PoC の中心は、Web process を stateful にせず、次の 2 種類の状態を
明確に分離することです。

| 状態 | 保存先 | 責務 |
| --- | --- | --- |
| Session 一覧、title、model、作成日時、初期化状態 | SQLite `app_sessions` | Application |
| Conversation events、checkpoints、plan、workspace、temporary files | SQLite-backed custom `SessionFsProvider` | GitHub Copilot SDK |

Application が Copilot の内部状態を独自形式へシリアライズするのではなく、
agent state は SDK の SessionFS contract を通して保存します。

## 最初に検証すること

1. React から session を作成して message を送る
2. ASP.NET Core backend が GitHub Copilot SDK session を作成する
3. SDK が出力する session state を custom `SessionFsProvider` 経由で SQLite へ保存する
4. Backend process を停止する
5. 同じ SQLite database を使って backend を再起動する
6. 新しい `CopilotClient` で session を resume する
7. 再起動前の context を使った response が返ることを確認する

成功条件は、active `CopilotSession` object や local
`~/.copilot/session-state` directory に依存せず、SQLite から conversation を
再開できることです。

## 構成

```text
React + Vite
    |
    v
ASP.NET Core Minimal API (.NET 10)
    |
    +-- IAppSessionRepository
    |       `-- SqliteAppSessionRepository --> app_sessions
    |
    `-- GitHub Copilot SDK
            `-- ISessionFsProviderFactory
                    `-- SqliteSessionFsProvider --> session_fs_nodes
```

deployable application は 1 project にまとめます。React は `ClientApp` に置き、
development では Vite から `/api` を proxy し、build 後は ASP.NET Core が
static files として配信します。

詳細は [Architecture](docs/architecture.md) を参照してください。

## Technology

- .NET 10
- ASP.NET Core Minimal API
- React
- TypeScript
- Vite
- `GitHub.Copilot.SDK`
- `Microsoft.Data.Sqlite`
- xUnit

## Dependency injection

最初の backend は SQLite ですが、application code は具体実装へ直接依存させません。

- `IAppSessionRepository`
  - Current: SQLite
  - Future: SQL Server、Azure Cosmos DB
- `ISessionFsProviderFactory`
  - Current: SQLite-backed SessionFS
  - Future: Azure Blob Storage、Azure Cosmos DB-backed SessionFS
- `ICopilotClientFactory`
  - Current: external local headless Copilot CLI
  - Future: hosted runtime connection

同じ SQLite file を使っても、application state と agent state は table と
access layer を分離します。

## Local authentication

この PoC では GitHub OAuth UI や token database を作りません。

既定では、sign in 済みの GitHub Copilot CLI を headless server として起動し、
ASP.NET Core は `localhost:4321` へ接続します。外部 runtime への接続では、
authentication は headless CLI process が管理します。必要であれば CLI 起動前に
`COPILOT_GITHUB_TOKEN`、`GH_TOKEN`、または `GITHUB_TOKEN` を設定してください。

Credential は次の場所へ保存しません。

- React/browser
- SQLite
- `appsettings.json`
- Git history
- Application log

## Prerequisites

- .NET SDK 10
- Node.js 24 以降と npm
- GitHub Copilot CLI
- GitHub Copilot subscription を利用できる GitHub account

最初に CLI の対話 mode を起動し、`/login` で sign in 済みであることを確認します。

## Run locally

初回だけ frontend dependencies を復元して build します。

```powershell
cd src\CopilotSessionPersistencePoc\ClientApp
npm ci
npm run build
cd ..\..\..
```

推奨構成では、推測困難な connection token を 2 つの terminal へ同じ値で設定します。
次の例では説明のため固定文字列を使っていますが、実際には password manager などで
生成した random value を使ってください。

Terminal 1:

```powershell
$env:COPILOT_CONNECTION_TOKEN = "<random-local-token>"
copilot --headless --port 4321
```

Terminal 2:

```powershell
$env:COPILOT_CONNECTION_TOKEN = "<random-local-token>"
$env:ASPNETCORE_URLS = "http://localhost:5000"
dotnet run --project src\CopilotSessionPersistencePoc
```

Browser で `http://localhost:5000` を開きます。local SQLite database は
`src\CopilotSessionPersistencePoc\data\copilot-sessions.db` に作成されます。
この directory と WAL/SHM files は Git 対象外です。

Frontend HMR を使う場合は backend と headless CLI に加えて次を実行し、
Vite が表示する URL を開きます。`/api` は port 5000 へ proxy されます。

```powershell
cd src\CopilotSessionPersistencePoc\ClientApp
npm run dev
```

## Test

```powershell
dotnet test CopilotSessionPersistencePoc.slnx

cd src\CopilotSessionPersistencePoc\ClientApp
npm run lint
npm run build
```

通常の tests は real Copilot service を呼びません。認証を伴う restart verification
は local-only です。実際の検証では 2 messages 後に Web と headless CLI の両 process
を停止し、同じ SQLite file で再起動しました。再起動前の 4 messages を読み込んだ後、
以前記憶させた marker を follow-up で回答し、history が 6 events へ更新されることを
確認しています。

## SessionFS Inspector

Session を選択して **Inspect storage** を開くと、custom SessionFS provider が扱う
virtual files と実際の保存先を read-only で確認できます。

- `session_fs_nodes` の row、file、directory、content byte 数
- `/session-state/events.jsonl` の event 数
- Virtual path、size、timestamp、version
- 選択した row の content preview と SQLite primary key
- SQLite database file の実 path と size
- 対応する `~/.copilot/session-state/{sessionId}` の存在確認

SQLite database file 自体は host filesystem 上に存在しますが、Inspector は
`events.jsonl`、`workspace.yaml`、checkpoint などが個別の host file ではなく
`session_fs_nodes` の row/content として保存されていることを evidence とともに表示します。
任意 SQL は実行できず、content preview は最大 65,536 characters に制限され、既知の
GitHub/Bearer token pattern は redact されます。

## Scope

### Included

- Session metadata の SQLite persistence
- SQLite-backed custom `SessionFsProvider`
- Session create、dispose、resume
- React chat UI
- Backend restart 後の context recovery
- Unit、integration、restart E2E tests

### Not included

- Azure deployment
- GitHub OAuth / GitHub App
- Multi-user authorization
- SQL Server / Azure Cosmos DB / Azure Blob Storage implementation
- SDK の `ISessionFsSqliteProvider` と agent SQL tool
- Distributed lock と multi-instance scaling
- Production retention、backup、encryption policy

## Security and local-only limitations

- `CopilotClientMode.Empty` と空の `AvailableTools` を使い、host filesystem、network、
  shell tool を agent に公開しません。
- `COPILOT_CONNECTION_TOKEN` を設定しない場合、headless CLI は local client connection
  を認証しません。
- API に user authorization はありません。trusted local development machine 専用です。
- Lock は process 内だけです。複数 Web instance から同じ session を同時利用できません。
- SQLite file の backup、encryption、retention は実装していません。

## References

- [GitHub Copilot SDK](https://github.com/github/copilot-sdk)
- [Session resume and persistence](https://docs.github.com/en/copilot/how-tos/copilot-sdk/features/session-persistence)
- [Backend services setup](https://github.com/github/copilot-sdk/blob/main/docs/setup/backend-services.md)
- [ASP.NET Core with React](https://learn.microsoft.com/visualstudio/javascript/tutorial-asp-net-core-with-react)
