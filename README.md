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
  - Current: local auto-managed Copilot CLI
  - Future: external headless Copilot CLI

同じ SQLite file を使っても、application state と agent state は table と
access layer を分離します。

## Local authentication

この PoC では GitHub OAuth UI や token database を作りません。

既定では、developer が sign in 済みの GitHub Copilot CLI credential を
backend process から利用します。明示的な token が必要な場合は
`COPILOT_GITHUB_TOKEN` environment variable を使います。

Credential は次の場所へ保存しません。

- React/browser
- SQLite
- `appsettings.json`
- Git history
- Application log

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

## Project status

現在は architecture 定義の段階です。Application code と local 実行 command は、
設計に沿って次の step で追加します。

## References

- [GitHub Copilot SDK](https://github.com/github/copilot-sdk)
- [Session resume and persistence](https://docs.github.com/en/copilot/how-tos/copilot-sdk/features/session-persistence)
- [Backend services setup](https://github.com/github/copilot-sdk/blob/main/docs/setup/backend-services.md)
- [ASP.NET Core with React](https://learn.microsoft.com/visualstudio/javascript/tutorial-asp-net-core-with-react)
