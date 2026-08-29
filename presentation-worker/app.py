from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import threading
import zipfile
from pathlib import Path

import fitz
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse
from pydantic import BaseModel, ConfigDict, Field, field_validator
from pptx import Presentation
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_AUTO_SHAPE_TYPE
from pptx.enum.text import MSO_ANCHOR, MSO_AUTO_SIZE, PP_ALIGN
from pptx.util import Inches, Pt

MAX_REQUEST_BYTES = 64 * 1024
MAX_ARTIFACT_BYTES = 32 * 1024 * 1024
SAFE_FILE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,119}\.pptx$", re.IGNORECASE)
SLIDE_XML = re.compile(r"^ppt/slides/slide\d+\.xml$")
CONTENT_TYPES = {
    ".pptx": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    ".pdf": "application/pdf",
    ".png": "image/png",
    ".json": "application/json",
}

NAVY = RGBColor(24, 39, 66)
INK = RGBColor(36, 45, 58)
CREAM = RGBColor(247, 244, 236)
TEAL = RGBColor(24, 137, 141)
CORAL = RGBColor(229, 107, 87)
PALE_TEAL = RGBColor(221, 239, 237)
WHITE = RGBColor(255, 255, 255)
FONT = "Noto Sans CJK JP"

OUTPUT_DIR = Path(tempfile.mkdtemp(prefix="presentation-worker-")).resolve()
BUILD_LOCK = threading.Lock()


class ContentSlide(BaseModel):
    model_config = ConfigDict(extra="forbid")

    title: str = Field(min_length=1, max_length=100)
    body: str = Field(min_length=1, max_length=800)
    highlight: str | None = Field(default=None, min_length=1, max_length=180)

    @field_validator("title", "body", "highlight")
    @classmethod
    def reject_blank(cls, value: str | None) -> str | None:
        if value is not None and not value.strip():
            raise ValueError("must not be blank")
        return value


class PresentationRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    fileName: str = Field(min_length=6, max_length=125)
    title: str = Field(min_length=1, max_length=140)
    subtitle: str | None = Field(default=None, min_length=1, max_length=240)
    audience: str = Field(min_length=1, max_length=160)
    slides: list[ContentSlide] = Field(min_length=1, max_length=7)

    @field_validator("fileName")
    @classmethod
    def safe_file_name(cls, value: str) -> str:
        if not SAFE_FILE_NAME.fullmatch(value) or Path(value).name != value:
            raise ValueError("must be a safe .pptx base file name")
        return value

    @field_validator("title", "subtitle", "audience")
    @classmethod
    def reject_blank(cls, value: str | None) -> str | None:
        if value is not None and not value.strip():
            raise ValueError("must not be blank")
        return value


app = FastAPI(title="Presentation Worker", docs_url=None, redoc_url=None)


@app.middleware("http")
async def bound_request(request: Request, call_next):
    length = request.headers.get("content-length")
    if request.method == "POST" and request.url.path == "/presentations" and length is None:
        return JSONResponse(status_code=411, content={"detail": "Content-Length is required"})
    if length is not None:
        try:
            if int(length) > MAX_REQUEST_BYTES:
                return JSONResponse(status_code=413, content={"detail": "Request body is too large"})
        except ValueError:
            return JSONResponse(status_code=400, content={"detail": "Invalid Content-Length"})
    return await call_next(request)


@app.get("/healthz")
def healthz() -> dict[str, str]:
    return {"status": "ok"}


def add_text(
    slide,
    text: str,
    left: float,
    top: float,
    width: float,
    height: float,
    *,
    size: int,
    color: RGBColor = INK,
    bold: bool = False,
    align: PP_ALIGN = PP_ALIGN.LEFT,
    margin: float = 0.06,
):
    box = slide.shapes.add_textbox(Inches(left), Inches(top), Inches(width), Inches(height))
    frame = box.text_frame
    frame.clear()
    frame.word_wrap = True
    frame.auto_size = MSO_AUTO_SIZE.TEXT_TO_FIT_SHAPE
    frame.margin_left = frame.margin_right = Inches(margin)
    frame.margin_top = frame.margin_bottom = Inches(margin)
    frame.vertical_anchor = MSO_ANCHOR.MIDDLE
    paragraph = frame.paragraphs[0]
    paragraph.alignment = align
    run = paragraph.add_run()
    run.text = text
    run.font.name = FONT
    run.font.size = Pt(size)
    run.font.bold = bold
    run.font.color.rgb = color
    return box


def add_shape(slide, kind, left, top, width, height, fill, line=None, radius=None):
    shape = slide.shapes.add_shape(kind, Inches(left), Inches(top), Inches(width), Inches(height))
    shape.fill.solid()
    shape.fill.fore_color.rgb = fill
    shape.line.color.rgb = line or fill
    return shape


def body_parts(body: str) -> list[str]:
    parts = [line.strip() for line in body.splitlines() if line.strip()]
    return parts or [body]


def display_text(value: str) -> str:
    return re.sub(r"([、。])\s+", r"\1", value.strip())


def create_pptx(data: PresentationRequest, destination: Path) -> None:
    prs = Presentation()
    prs.slide_width = Inches(13.333333)
    prs.slide_height = Inches(7.5)
    blank = prs.slide_layouts[6]

    title_slide = prs.slides.add_slide(blank)
    background = title_slide.background.fill
    background.solid()
    background.fore_color.rgb = NAVY
    add_shape(title_slide, MSO_AUTO_SHAPE_TYPE.RECTANGLE, 0, 0, 0.28, 7.5, CORAL)
    add_shape(title_slide, MSO_AUTO_SHAPE_TYPE.OVAL, 10.75, 0.65, 1.45, 1.45, TEAL)
    add_shape(title_slide, MSO_AUTO_SHAPE_TYPE.OVAL, 11.75, 1.55, 0.62, 0.62, CORAL)
    add_text(title_slide, "POWERPOINT ARTIFACT", 0.9, 1.15, 4.0, 0.35, size=12, color=CORAL, bold=True)
    add_text(title_slide, display_text(data.title), 0.85, 2.0, 10.1, 1.05, size=42, color=WHITE, bold=True)
    if data.subtitle:
        add_text(title_slide, display_text(data.subtitle), 0.9, 3.35, 9.5, 0.72, size=20, color=PALE_TEAL)
    add_text(title_slide, f"対象  |  {display_text(data.audience)}", 0.9, 6.35, 8.5, 0.42, size=14, color=WHITE)

    for index, item in enumerate(data.slides, start=1):
        slide = prs.slides.add_slide(blank)
        fill = slide.background.fill
        fill.solid()
        fill.fore_color.rgb = CREAM
        add_text(slide, f"{index:02d}", 0.65, 0.45, 0.65, 0.38, size=12, color=CORAL, bold=True)
        add_text(slide, display_text(item.title), 1.35, 0.32, 10.9, 0.78, size=32, color=NAVY, bold=True)
        parts = body_parts(item.body)

        if item.highlight and index % 2 == 1:
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.ROUNDED_RECTANGLE, 0.75, 1.55, 4.0, 4.65, NAVY)
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.OVAL, 1.1, 1.95, 0.68, 0.68, CORAL)
            add_text(slide, "KEY POINT", 1.95, 2.02, 2.1, 0.42, size=12, color=PALE_TEAL, bold=True)
            add_text(
                slide,
                display_text(item.highlight),
                1.08,
                3.0,
                3.35,
                1.7,
                size=24,
                color=WHITE,
                bold=True,
                align=PP_ALIGN.CENTER,
            )
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.ROUNDED_RECTANGLE, 5.15, 1.55, 7.35, 4.65, WHITE, PALE_TEAL)
            add_text(slide, display_text(item.body), 5.62, 2.05, 6.4, 3.6, size=19, color=INK)
        elif item.highlight:
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.ROUNDED_RECTANGLE, 0.75, 1.5, 11.75, 2.25, WHITE, PALE_TEAL)
            add_text(slide, display_text(item.body), 1.15, 1.88, 10.95, 1.48, size=18, color=INK)
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.OVAL, 1.35, 4.35, 0.72, 0.72, TEAL)
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.RECTANGLE, 2.05, 4.68, 1.15, 0.06, TEAL)
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.ROUNDED_RECTANGLE, 3.18, 4.05, 6.95, 1.35, NAVY)
            add_text(
                slide,
                display_text(item.highlight),
                3.52,
                4.3,
                6.3,
                0.82,
                size=23,
                color=WHITE,
                bold=True,
                align=PP_ALIGN.CENTER,
            )
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.RECTANGLE, 10.12, 4.68, 1.15, 0.06, TEAL)
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.OVAL, 11.25, 4.35, 0.72, 0.72, CORAL)
        elif index % 2 == 0 and len(parts) > 1:
            count = min(len(parts), 6)
            for part_index, part in enumerate(parts[:count]):
                x = 0.9 + (part_index % 3) * 4.08
                y = 1.75 + (part_index // 3) * 2.25
                add_shape(slide, MSO_AUTO_SHAPE_TYPE.ROUNDED_RECTANGLE, x, y, 3.65, 1.75, WHITE, PALE_TEAL)
                add_text(slide, f"{part_index + 1:02d}", x + 0.18, y + 0.14, 0.5, 0.32, size=10, color=CORAL, bold=True)
                add_text(slide, part, x + 0.2, y + 0.5, 3.25, 1.05, size=15, color=INK)
            if len(parts) > count:
                add_text(slide, "\n".join(parts[count:]), 0.9, 6.32, 11.6, 0.55, size=11, color=INK)
        elif len(parts) > 1:
            count = min(len(parts), 7)
            start_y = 1.7
            step = min(0.72, 4.7 / count)
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.RECTANGLE, 1.23, start_y + 0.2, 0.04, step * (count - 1), TEAL)
            for part_index, part in enumerate(parts[:count]):
                y = start_y + part_index * step
                add_shape(slide, MSO_AUTO_SHAPE_TYPE.OVAL, 1.08, y + 0.13, 0.34, 0.34, CORAL)
                add_text(slide, part, 1.72, y, 10.2, 0.58, size=17, color=INK)
            if len(parts) > count:
                add_text(slide, "\n".join(parts[count:]), 1.72, 6.35, 10.2, 0.42, size=10, color=INK)
        else:
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.ROUNDED_RECTANGLE, 0.9, 1.7, 11.55, 4.75, WHITE, PALE_TEAL)
            add_shape(slide, MSO_AUTO_SHAPE_TYPE.RECTANGLE, 0.9, 1.7, 0.16, 4.75, TEAL)
            add_text(slide, item.body, 1.45, 2.05, 10.25, 4.0, size=22, color=INK)

        add_text(
            slide,
            display_text(data.audience),
            8.7,
            6.7,
            3.75,
            0.28,
            size=10,
            color=NAVY,
            align=PP_ALIGN.RIGHT,
        )

    prs.save(destination)


def validate_pptx(path: Path, expected_slides: int) -> dict[str, object]:
    if not path.is_file() or path.stat().st_size == 0:
        raise ValueError("PPTX was not created")
    if path.stat().st_size > MAX_ARTIFACT_BYTES:
        raise ValueError("PPTX exceeds the artifact size limit")
    try:
        with zipfile.ZipFile(path) as archive:
            bad_file = archive.testzip()
            if bad_file:
                raise ValueError(f"Open XML archive contains a corrupt member: {bad_file}")
            names = archive.namelist()
            if "[Content_Types].xml" not in names or "ppt/presentation.xml" not in names:
                raise ValueError("Required Open XML members are missing")
            slides = sorted(name for name in names if SLIDE_XML.fullmatch(name))
            if len(slides) != expected_slides:
                raise ValueError(f"Expected {expected_slides} slides, found {len(slides)}")
            if any(not archive.read(name).strip() for name in slides):
                raise ValueError("A slide XML member is empty")
    except zipfile.BadZipFile as exc:
        raise ValueError("PPTX is not a valid Open XML zip") from exc
    return {"passed": True, "openXml": True, "nonemptySlideXml": True}


def render_presentation(pptx_path: Path, output_dir: Path, expected_pages: int) -> tuple[Path, list[Path]]:
    profile = output_dir / "lo-profile"
    profile.mkdir()
    command = [
        "libreoffice",
        "--headless",
        "--nologo",
        "--nodefault",
        "--nofirststartwizard",
        f"-env:UserInstallation={profile.resolve().as_uri()}",
        "--convert-to",
        "pdf",
        "--outdir",
        str(output_dir),
        str(pptx_path),
    ]
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=90, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise RuntimeError(f"LibreOffice rendering failed: {exc}") from exc
    pdf_path = output_dir / f"{pptx_path.stem}.pdf"
    if result.returncode != 0 or not pdf_path.is_file() or pdf_path.stat().st_size == 0:
        detail = (result.stderr or result.stdout).strip()[-500:]
        raise RuntimeError(f"LibreOffice rendering failed: {detail or 'no PDF produced'}")

    png_paths: list[Path] = []
    try:
        with fitz.open(pdf_path) as document:
            if document.page_count != expected_pages:
                raise RuntimeError(
                    f"Rendered page count differs: expected {expected_pages}, found {document.page_count}"
                )
            for page_number, page in enumerate(document, start=1):
                pixmap = page.get_pixmap(matrix=fitz.Matrix(1.5, 1.5), alpha=False)
                png_path = output_dir / f"{pptx_path.stem}-slide-{page_number:02d}.png"
                pixmap.save(png_path)
                png_paths.append(png_path)
    except RuntimeError:
        raise
    except Exception as exc:
        raise RuntimeError(f"PDF-to-PNG rendering failed: {exc}") from exc

    for artifact in [pdf_path, *png_paths]:
        if not artifact.is_file() or artifact.stat().st_size == 0:
            raise RuntimeError(f"Rendered artifact is empty: {artifact.name}")
        if artifact.stat().st_size > MAX_ARTIFACT_BYTES:
            raise RuntimeError(f"Rendered artifact exceeds size limit: {artifact.name}")
    return pdf_path, png_paths


def artifact_manifest(path: Path) -> dict[str, object]:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return {
        "fileName": path.name,
        "contentType": CONTENT_TYPES[path.suffix.lower()],
        "sizeBytes": path.stat().st_size,
        "sha256": digest.hexdigest(),
    }


def clear_outputs() -> None:
    for child in OUTPUT_DIR.iterdir():
        if child.is_dir():
            shutil.rmtree(child)
        else:
            child.unlink()


@app.post("/presentations")
def presentations(data: PresentationRequest) -> dict[str, object]:
    expected_slides = len(data.slides) + 1
    with BUILD_LOCK:
        clear_outputs()
        pptx_path = OUTPUT_DIR / data.fileName
        try:
            create_pptx(data, pptx_path)
            validation = validate_pptx(pptx_path, expected_slides)
            pdf_path, png_paths = render_presentation(pptx_path, OUTPUT_DIR, expected_slides)
            artifacts = [pptx_path, pdf_path, *png_paths]
            artifact_entries = [artifact_manifest(path) for path in artifacts]
            validation_path = OUTPUT_DIR / "validation.json"
            validation_path.write_text(
                json.dumps(
                    {
                        "validationPassed": True,
                        "validation": validation,
                        "slideCount": expected_slides,
                        "files": artifact_entries,
                    },
                    ensure_ascii=False,
                    indent=2,
                )
                + "\n",
                encoding="utf-8",
            )
            return {
                "validationPassed": True,
                "validation": validation,
                "slideCount": expected_slides,
                "files": [*artifact_entries, artifact_manifest(validation_path)],
            }
        except (ValueError, RuntimeError) as exc:
            clear_outputs()
            raise HTTPException(status_code=500, detail=str(exc)) from exc
        except Exception as exc:
            clear_outputs()
            raise HTTPException(status_code=500, detail="Presentation generation failed") from exc


@app.get("/artifacts/{file_name}")
def artifact(file_name: str):
    if Path(file_name).name != file_name or file_name in {".", ".."}:
        raise HTTPException(status_code=404, detail="Artifact not found")
    path = (OUTPUT_DIR / file_name).resolve()
    if path.parent != OUTPUT_DIR or path.suffix.lower() not in CONTENT_TYPES or not path.is_file():
        raise HTTPException(status_code=404, detail="Artifact not found")
    return FileResponse(
        path,
        media_type=CONTENT_TYPES[path.suffix.lower()],
        filename=path.name,
        content_disposition_type="attachment",
    )
