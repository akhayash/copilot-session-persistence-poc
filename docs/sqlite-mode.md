# Local SQLite mode

## Purpose

Local SQLite modeは、1つのASP.NET Core processでGitHub Copilot SDK sessionの
restart persistenceを検証する構成です。Azure Storageは使用しません。

```text
Persistence:Backend = Sqlite

Application metadata --> SQLite app_sessions
SessionFS agent state --> SQLite session_fs_nodes
Session exclusion     --> In-process SemaphoreSlim
Artifact store        --> Not configured
```

## Configuration

`appsettings.json`のdefaultはLocal SQLite modeです。

```json
{
  "Persistence": {
    "Backend": "Sqlite",
    "DatabasePath": "data/copilot-sessions.db",
    "BusyTimeoutMilliseconds": 5000
  }
}
```

`Program.cs`はSQLite用のrepository、SessionFS provider、diagnostics reader、
in-process lockだけをdependency injectionへ登録します。Azure Storage client、
Azure Table repository、Blob provider、Blob lease、Artifact storeは登録・初期化しません。

## Stored state

| State | SQLite table | Owner |
| --- | --- | --- |
| Local owner ID、session ID、title、model、initialized flag、timestamps、version | `app_sessions` | Application |
| Events、checkpoints、plan、workspaceなどのvirtual file tree | `session_fs_nodes` | GitHub Copilot SDK via `SessionFsProvider` |

1つのdatabase fileを使いますが、application metadataとagent stateはtableと
access layerを分けています。Application serviceは`session_fs_nodes`を直接操作しません。
Localではdefaultの`local-user`をSHA-256 owner keyへ変換します。既存databaseを開くと
`owner_id` columnを追加し、既存sessionをconfigured local ownerへ引き継ぎます。

## Run

```powershell
$env:COPILOT_CONNECTION_TOKEN = "<random-local-token>"
copilot --headless --port 4321
```

別terminal:

```powershell
$env:COPILOT_CONNECTION_TOKEN = "<random-local-token>"
$env:ASPNETCORE_URLS = "http://localhost:5000"
dotnet run --project src\CopilotSessionPersistencePoc
```

## Guarantees and limitations

- Web process restart後も同じdatabase fileからsessionをresumeできます。
- `~/.copilot/session-state`のhost file treeをsource of truthにしません。
- Lockはprocess内だけなので、複数Web nodeでSQLite fileを共有する構成は対象外です。
- Azure Storageへ失敗時fallbackするmodeではありません。
- Artifact storeはLocal SQLite modeでは未登録です。
