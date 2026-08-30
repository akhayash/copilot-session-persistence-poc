import hashlib
import base64
import json
import zipfile
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

import app


@pytest.fixture
def client(tmp_path, monkeypatch):
    monkeypatch.setattr(app, "OUTPUT_DIR", tmp_path.resolve())
    return TestClient(app.app)


def request_payload(file_name="briefing.pptx", content_count=2):
    return {
        "fileName": file_name,
        "title": "製品方針",
        "subtitle": "意思決定のための要約",
        "audience": "経営チーム",
        "slides": [
            {"title": f"論点 {index + 1}", "body": f"提供された内容 {index + 1}", "highlight": "重要"}
            for index in range(content_count)
        ],
    }


def fake_render(pptx_path: Path, output_dir: Path, expected_pages: int):
    pdf_path = output_dir / f"{pptx_path.stem}.pdf"
    pdf_path.write_bytes(b"%PDF-test")
    png_paths = []
    for index in range(1, expected_pages + 1):
        png = output_dir / f"{pptx_path.stem}-slide-{index:02d}.png"
        png.write_bytes(b"\x89PNG\r\n" + bytes([index]))
        png_paths.append(png)
    return pdf_path, png_paths


@pytest.mark.parametrize(
    "file_name",
    ["../briefing.pptx", r"..\briefing.pptx", "/briefing.pptx", "briefing.pptx.exe", ".pptx"],
)
def test_rejects_unsafe_file_names(client, file_name):
    response = client.post("/presentations", json=request_payload(file_name=file_name))
    assert response.status_code == 422


def test_generated_pptx_has_exact_slide_count(tmp_path):
    data = app.PresentationRequest.model_validate(request_payload(content_count=4))
    path = tmp_path / data.fileName
    app.create_pptx(data, path)

    validation = app.validate_pptx(path, 5)
    assert validation["passed"] is True
    with zipfile.ZipFile(path) as archive:
        slide_xml = [name for name in archive.namelist() if app.SLIDE_XML.fullmatch(name)]
    assert len(slide_xml) == 5


def test_validation_rejects_wrong_expected_slide_count(tmp_path):
    data = app.PresentationRequest.model_validate(request_payload(content_count=1))
    path = tmp_path / data.fileName
    app.create_pptx(data, path)
    with pytest.raises(ValueError, match="Expected 3 slides, found 2"):
        app.validate_pptx(path, 3)


def test_manifest_hashes_and_artifact_download(client, monkeypatch):
    monkeypatch.setattr(app, "render_presentation", fake_render)
    response = client.post("/presentations", json=request_payload(content_count=2))

    assert response.status_code == 200
    manifest = response.json()
    assert manifest["validationPassed"] is True
    assert manifest["validation"]["passed"] is True
    assert manifest["slideCount"] == 3
    assert len(manifest["files"]) == 6
    for entry in manifest["files"]:
        artifact_response = client.get(f"/artifacts/{entry['fileName']}")
        assert artifact_response.status_code == 200
        assert entry["sizeBytes"] == len(artifact_response.content)
        assert entry["sha256"] == hashlib.sha256(artifact_response.content).hexdigest()
        assert artifact_response.headers["content-type"] == entry["contentType"]
        assert "attachment" in artifact_response.headers["content-disposition"]

    validation_entry = next(entry for entry in manifest["files"] if entry["fileName"] == "validation.json")
    validation_response = client.get("/artifacts/validation.json")
    audit = json.loads(validation_response.content)
    assert validation_entry["contentType"] == "application/json"
    assert audit["validationPassed"] is True
    assert audit["slideCount"] == manifest["slideCount"]
    assert len(audit["files"]) == 5
    assert all(entry["fileName"] != "validation.json" for entry in audit["files"])
    assert audit["files"] == [entry for entry in manifest["files"] if entry["fileName"] != "validation.json"]


def test_total_slide_bounds(client):
    too_many = request_payload(content_count=2)
    too_many["slides"] = [{"title": "題", "body": "本文"}] * 8
    assert client.post("/presentations", json=too_many).status_code == 422

    no_content = request_payload()
    no_content["slides"] = []
    assert client.post("/presentations", json=no_content).status_code == 422


def test_workspace_files_persist_across_requests(client):
    content = "console.log('hello')\n"
    response = client.put(
        "/files/scripts/build.js",
        json={"encoding": "utf-8", "data": content},
    )
    assert response.status_code == 200
    assert response.json()["path"] == "scripts/build.js"

    listing = client.get("/files").json()["files"]
    assert [entry["path"] for entry in listing] == ["scripts/build.js"]
    downloaded = client.get("/files/scripts/build.js")
    assert downloaded.content == content.encode()
    assert client.delete("/files/scripts/build.js").status_code == 204
    assert client.get("/files").json()["files"] == []


def test_upload_does_not_overwrite_similarly_named_workspace_file(client):
    client.put("/files/.foo.tmp", json={"encoding": "utf-8", "data": "keep"})
    client.put("/files/foo", json={"encoding": "utf-8", "data": "replace"})

    assert client.get("/files/.foo.tmp").content == b"keep"
    assert client.get("/files/foo").content == b"replace"


@pytest.mark.parametrize("path", ["../secret", r"..\\secret", "/etc/passwd", "a/../secret"])
def test_workspace_files_reject_path_escape(client, path):
    assert client.get(f"/files/{path}").status_code == 404


def test_exec_uses_workspace_and_reports_exit_code(client):
    response = client.post(
        "/exec",
        json={"command": "echo workspace > result.txt && echo output"},
    )
    assert response.status_code == 200
    assert response.json()["exitCode"] == 0
    assert response.json()["stdout"].strip() == "output"
    assert response.json()["stderr"] == ""
    assert client.get("/files/result.txt").content.strip() == b"workspace"


def test_exec_bounds_retained_output(client):
    client.put(
        "/files/spam.py",
        json={
            "encoding": "utf-8",
            "data": (
                "import sys\n"
                f"sys.stdout.write('x' * {app.MAX_EXEC_OUTPUT_BYTES + 100})\n"
            ),
        },
    )
    response = client.post(
        "/exec",
        json={"command": "python spam.py"},
    )
    assert response.status_code == 200
    assert response.json()["stdoutTruncated"] is True
    assert len(response.json()["stdout"]) == app.MAX_EXEC_OUTPUT_BYTES


def test_render_returns_downscaled_base64_previews(client, monkeypatch):
    monkeypatch.setattr(app, "render_presentation", fake_render)
    data = app.PresentationRequest.model_validate(request_payload(content_count=1))
    pptx_path = app.workspace_root() / data.fileName
    app.create_pptx(data, pptx_path)

    def fake_preview(_path):
        content = b"preview"
        return base64.b64encode(content).decode("ascii"), len(content)

    monkeypatch.setattr(app, "preview_image", fake_preview)
    response = client.post("/render", json={"path": data.fileName})
    assert response.status_code == 200
    result = response.json()
    assert result["slideCount"] == 2
    assert len(result["images"]) == 2
    assert base64.b64decode(result["images"][0]["data"]) == b"preview"
    assert not (app.workspace_root() / ".render-briefing").exists()
