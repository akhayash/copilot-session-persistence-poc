# Presentation worker

Azure Container Apps custom container Dynamic Sessions 向けの、egress 不要な PPTX
生成 worker です。16:9 の PPTX を作成し、Open XML を検証した後、LibreOffice
Impress で PDF、PyMuPDF で slide PNG を生成します。image は `8080/tcp` を公開し、
非 root user で動作します。

## Image contract

```powershell
docker build -t presentation-worker .\presentation-worker
docker run --rm -p 8080:8080 presentation-worker
```

image には pinned Python packages、Node.js、PptxGenJS、LibreOffice Impress、
Poppler、Noto CJK fonts、renderer が含まれます。test dependencies と tests は
production image に含めません。runtime network access は不要です。workspace は
custom container session の lifetime 中は保持され、application が SessionFS との
materialize / commit を担当します。同一 session では一度に 1 command または render を
処理します。

## API

- `GET /healthz` — `{"status":"ok"}`
- `POST /exec` — workspace を cwd として shell / Python / Node.js command を実行。
- `GET /files` — workspace file と size / SHA-256 を列挙。
- `GET /files/{path}` / `PUT /files/{path}` — workspace file の取得 / 書き込み。
- `POST /render` — workspace の `.pptx` を検証・renderし、縮小した slide PNG を返却。
- `POST /presentations` — `fileName`（安全な `.pptx` basename）、`title`、
  optional `subtitle`、`audience`、1–7 個の `slides`
  (`title`, `body`, optional `highlight`)。title slide を含め合計 2–8 slides。
- `GET /artifacts/{fileName}` — 現在の生成結果を attachment として取得。

成功 response は top-level `validationPassed: true`, `validation`, `slideCount`,
および PPTX/PDF/各 slide PNG/`validation.json` の `fileName`, `contentType`,
`sizeBytes`, `sha256` を含む manifest です。`validation.json` は検証結果、正確な
slide 数、PPTX/PDF/PNG の metadata と hash を監査用に保持します。自己 hash は
含めず、response manifest のみが `validation.json` 自体の hash を返します。
validation または rendering に失敗した request は artifact を返しません。

Tests:

```powershell
python -m pip install -r .\presentation-worker\requirements-dev.txt
python -m pytest .\presentation-worker\tests -q --basetemp=.\presentation-worker\.pytest-tmp
```
