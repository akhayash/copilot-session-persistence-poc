import hashlib
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
