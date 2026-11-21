"""Engine commands. Each takes the parsed job dict and returns a JSON-serializable summary.

Libraries (all permissively licensed, no AGPL): pypdfium2 (Apache-2.0/BSD-3, Google's PDFium),
pdfminer.six (MIT), python-docx (MIT), Pillow (MIT-CMU), onnxruntime (MIT), numpy (BSD-3).
"""

from __future__ import annotations

import io
import os
import statistics
import warnings
from pathlib import Path

import numpy as np
import pypdfium2 as pdfium
import pypdfium2.raw as pdfium_c
from docx import Document
from docx.enum.section import WD_ORIENT
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK
from docx.shared import Emu, Pt
from pdfminer.high_level import extract_pages
from pdfminer.layout import LAParams, LTChar, LTTextBox, LTTextLine
from PIL import Image, ImageOps

from . import BadInput

PT_PER_INCH = 72
EMU_PER_PT = 12700


def _limits(job: dict) -> None:
    """Pillow refuses images above max_pixels (decompression bombs) instead of only warning."""
    Image.MAX_IMAGE_PIXELS = int(job.get("max_pixels", 50_000_000))
    warnings.simplefilter("error", Image.DecompressionBombWarning)


def _out_dir(job: dict) -> Path:
    out = Path(job["output_dir"])
    out.mkdir(parents=True, exist_ok=True)
    return out


def _open_pdf(path: str, max_pages: int) -> pdfium.PdfDocument:
    try:
        pdf = pdfium.PdfDocument(path)
    except pdfium.PdfiumError as e:
        if "password" in str(e).lower():
            raise BadInput("This PDF is password-protected. Unlock it first.") from e
        raise BadInput(f"Not a readable PDF ({e}).") from e
    if len(pdf) == 0:
        raise BadInput("The PDF has no pages.")
    if len(pdf) > max_pages:
        raise BadInput(f"The PDF has {len(pdf)} pages; the limit is {max_pages}.")
    return pdf


def _page_range(spec: str | None, count: int) -> list[int]:
    """'1-3,5' -> [0,1,2,4]; empty means all pages."""
    if not spec:
        return list(range(count))
    pages: list[int] = []
    for part in spec.replace(" ", "").split(","):
        if not part:
            continue
        a, _, b = part.partition("-")
        try:
            start, end = int(a), int(b or a)
        except ValueError as e:
            raise BadInput(f"Invalid page range '{part}'.") from e
        if start < 1 or end < start or end > count:
            raise BadInput(f"Page range '{part}' is outside 1-{count}.")
        pages.extend(range(start - 1, end))
    return list(dict.fromkeys(pages))


def _open_image(path: str) -> Image.Image:
    try:
        img = Image.open(path)
        img.load()
    except Image.DecompressionBombError as e:
        raise BadInput("The image dimensions are too large.") from e
    except (OSError, SyntaxError, ValueError) as e:
        raise BadInput(f"Not a readable image ({e}).") from e
    return ImageOps.exif_transpose(img)


# ---------------------------------------------------------------------------------------------------------- PDF -> image

def pdf_to_images(job: dict) -> dict:
    _limits(job)
    out = _out_dir(job)
    fmt = job.get("format", "jpeg").lower()
    if fmt not in ("jpeg", "png", "webp"):
        raise BadInput("Image format must be jpeg, png or webp.")
    dpi = max(36, min(int(job.get("dpi", 150)), 600))
    quality = max(10, min(int(job.get("quality", 90)), 100))
    max_pixels = int(job.get("max_pixels", 50_000_000))
    pdf = _open_pdf(job["input"], int(job.get("max_pages", 300)))
    pages = _page_range(job.get("pages"), len(pdf))
    stem = job.get("stem", "page")
    ext = "jpg" if fmt == "jpeg" else fmt
    files = []
    for i in pages:
        page = pdf[i]
        w, h = page.get_size()
        scale = dpi / PT_PER_INCH
        if w * scale * h * scale > max_pixels:  # keep giant pages inside the pixel budget
            scale = (max_pixels / (w * h)) ** 0.5
        bitmap = page.render(scale=scale, may_draw_forms=True, fill_color=(255, 255, 255, 255))
        img = bitmap.to_pil().convert("RGB")
        name = f"{stem}-{i + 1:03d}.{ext}"
        save = {"jpeg": dict(format="JPEG", quality=quality, optimize=True, progressive=True),
                "png": dict(format="PNG", optimize=True),
                "webp": dict(format="WEBP", quality=quality, method=4)}[fmt]
        img.save(out / name, **save)
        files.append({"name": name, "width": img.width, "height": img.height})
        page.close()
    pdf.close()
    return {"files": files, "pages": len(files)}


# ---------------------------------------------------------------------------------------------------------- image -> PDF

PAGE_SIZES = {"a4": (595.28, 841.89), "letter": (612.0, 792.0)}


def images_to_pdf(job: dict) -> dict:
    """One PDF page per image. JPEGs are embedded byte-for-byte (no re-compression); others losslessly."""
    _limits(job)
    out = _out_dir(job)
    size = job.get("page_size", "fit")
    margin = max(0.0, min(float(job.get("margin_pt", 0)), 144.0))
    pdf = pdfium.PdfDocument.new()
    for path in job["inputs"]:
        img = _open_image(path)
        iw, ih = img.size
        if size in PAGE_SIZES:
            pw, ph = PAGE_SIZES[size]
            if (iw > ih) != (pw > ph):  # rotate the page to match the image orientation
                pw, ph = ph, pw
            avail_w, avail_h = pw - 2 * margin, ph - 2 * margin
            s = min(avail_w / iw, avail_h / ih)
            dw, dh = iw * s, ih * s
        else:  # "fit": page is the image at 96 DPI plus margins
            dw, dh = iw * PT_PER_INCH / 96, ih * PT_PER_INCH / 96
            pw, ph = dw + 2 * margin, dh + 2 * margin
        page = pdf.new_page(pw, ph)
        obj = pdfium.PdfImage.new(pdf)
        raw = Image.open(path)
        if raw.format == "JPEG" and raw.mode in ("RGB", "L") and img.size == raw.size and not _has_rotation(raw):
            with open(path, "rb") as f:
                obj.load_jpeg(io.BytesIO(f.read()), inline=True)
        else:
            obj.set_bitmap(pdfium.PdfBitmap.from_pil(_flatten(img)))
        obj.set_matrix(pdfium.PdfMatrix().scale(dw, dh).translate((pw - dw) / 2, (ph - dh) / 2))
        page.insert_obj(obj)
        page.gen_content()
        page.close()
    name = job.get("output_name", "images.pdf")
    pdf.save(out / name)
    count = len(pdf)
    pdf.close()
    return {"files": [{"name": name}], "pages": count}


def _has_rotation(img: Image.Image) -> bool:
    return img.getexif().get(0x0112, 1) not in (1, None)


def _flatten(img: Image.Image) -> Image.Image:
    if img.mode in ("RGBA", "LA", "P"):
        rgba = img.convert("RGBA")
        bg = Image.new("RGB", rgba.size, (255, 255, 255))
        bg.paste(rgba, mask=rgba.split()[3])
        return bg
    return img.convert("RGB") if img.mode != "RGB" else img


# ---------------------------------------------------------------------------------------------------------- PDF -> Word

def pdf_to_docx(job: dict) -> dict:
    """Rebuilds the PDF as an editable Word document: text blocks become paragraphs (font size, bold, italic,
    alignment and indent kept), embedded pictures are placed in reading order, and pages without a text layer
    (scans) are inserted as full-page images."""
    _limits(job)
    out = _out_dir(job)
    max_pages = int(job.get("max_pages", 300))
    pdf = _open_pdf(job["input"], max_pages)
    doc = Document()
    _clear_body(doc)
    laparams = LAParams(line_margin=0.5, char_margin=2.0, word_margin=0.1, boxes_flow=0.5)

    text_pages = 0
    for index, layout in enumerate(extract_pages(job["input"], laparams=laparams, maxpages=max_pages)):
        page = pdf[index]
        pw, ph = page.get_size()
        section = doc.sections[0] if index == 0 else doc.add_section()
        _setup_section(section, pw, ph)

        blocks = []  # (top, kind, payload)
        for element in layout:
            if isinstance(element, LTTextBox) and element.get_text().strip():
                blocks.append((ph - element.y1, "text", element))
        for img, top, width_pt in _page_images(page, ph):
            blocks.append((top, "image", (img, width_pt)))

        has_text = any(kind == "text" for _, kind, _ in blocks)
        if not has_text:
            # No text layer (scanned page): keep it as a picture so nothing is lost.
            bitmap = page.render(scale=150 / PT_PER_INCH, fill_color=(255, 255, 255, 255))
            usable = pw - 72
            _add_picture(doc, bitmap.to_pil().convert("RGB"), usable)
        else:
            text_pages += 1
            body_size = _body_font_size(layout)
            for _, kind, payload in sorted(blocks, key=lambda b: b[0]):
                if kind == "text":
                    _add_text_box(doc, payload, pw, body_size)
                else:
                    img, width_pt = payload
                    _add_picture(doc, img, min(width_pt, pw - 72))
        page.close()

    name = job.get("output_name", "document.docx")
    doc.save(out / name)
    pages = len(pdf)
    pdf.close()
    return {"files": [{"name": name}], "pages": pages, "text_pages": text_pages}


def _clear_body(doc: Document) -> None:
    body = doc.element.body
    for child in list(body):
        if not child.tag.endswith("sectPr"):
            body.remove(child)


def _setup_section(section, pw: float, ph: float) -> None:
    section.page_width = Emu(int(pw * EMU_PER_PT))
    section.page_height = Emu(int(ph * EMU_PER_PT))
    section.orientation = WD_ORIENT.LANDSCAPE if pw > ph else WD_ORIENT.PORTRAIT
    for side in ("left_margin", "right_margin", "top_margin", "bottom_margin"):
        setattr(section, side, Pt(36))


def _page_images(page: pdfium.PdfPage, ph: float):
    """Yields (PIL image, distance from top in pt, displayed width in pt) for each picture on the page."""
    for obj in page.get_objects(filter=[pdfium_c.FPDF_PAGEOBJ_IMAGE], max_depth=2):
        try:
            left, bottom, right, top = obj.get_bounds()
            width_pt = right - left
            if width_pt < 24 or (top - bottom) < 24:  # skip bullets, rules and tiny decorations
                continue
            bitmap = obj.get_bitmap(render=True)
            img = bitmap.to_pil()
            yield _flatten(img), ph - top, width_pt
        except pdfium.PdfiumError:
            continue


def _chars(box):
    for line in box:
        if isinstance(line, LTTextLine):
            for ch in line:
                if isinstance(ch, LTChar):
                    yield ch


def _body_font_size(layout) -> float:
    sizes = [round(ch.size, 1) for el in layout if isinstance(el, LTTextBox) for ch in _chars(el)]
    return statistics.median(sizes) if sizes else 11.0


def _add_text_box(doc: Document, box: LTTextBox, pw: float, body_size: float) -> None:
    lines = [line.get_text().rstrip("\n") for line in box if isinstance(line, LTTextLine)]
    # Join soft-wrapped lines into one paragraph; keep hyphenated words whole.
    text = ""
    for line in lines:
        line = line.strip()
        if not line:
            continue
        if text.endswith("-"):
            text = text[:-1] + line
        else:
            text = f"{text} {line}" if text else line
    if not text:
        return

    chars = list(_chars(box))
    size = statistics.median([c.size for c in chars]) if chars else body_size
    fonts = [c.fontname.lower() for c in chars]
    bold = sum("bold" in f or "black" in f or "heavy" in f for f in fonts) > len(fonts) / 2
    italic = sum("italic" in f or "oblique" in f for f in fonts) > len(fonts) / 2

    paragraph = doc.add_paragraph()
    run = paragraph.add_run(text)
    run.font.size = Pt(max(6.0, min(size, 72.0)))
    run.bold = bold or size >= body_size * 1.35
    run.italic = italic
    fmt = paragraph.paragraph_format
    fmt.space_after = Pt(max(2.0, size * 0.4))
    center = (box.x0 + box.x1) / 2
    if abs(center - pw / 2) < pw * 0.04 and (box.x1 - box.x0) < pw * 0.75:
        paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
    else:
        indent = box.x0 - 36
        if indent > 18:
            fmt.left_indent = Pt(min(indent, pw / 2))


def _add_picture(doc: Document, img: Image.Image, width_pt: float) -> None:
    buf = io.BytesIO()
    img.save(buf, format="PNG" if img.mode == "RGBA" else "JPEG", quality=90)
    buf.seek(0)
    doc.add_paragraph().add_run().add_picture(buf, width=Pt(max(36.0, width_pt)))


# ---------------------------------------------------------------------------------------------------------- image -> Word

def images_to_docx(job: dict) -> dict:
    _limits(job)
    out = _out_dir(job)
    doc = Document()
    _clear_body(doc)
    for i, path in enumerate(job["inputs"]):
        img = _flatten(_open_image(path))
        landscape = img.width > img.height
        pw, ph = (841.89, 595.28) if landscape else (595.28, 841.89)
        section = doc.sections[0] if i == 0 else doc.add_section()
        _setup_section(section, pw, ph)
        usable_w, usable_h = pw - 72, ph - 72
        width = min(usable_w, usable_h * img.width / img.height)
        _add_picture(doc, img, width)
    name = job.get("output_name", "images.docx")
    doc.save(out / name)
    return {"files": [{"name": name}], "pages": len(job["inputs"])}


# ---------------------------------------------------------------------------------------------------------- AI upscale

_SESSION = None


def upscale(job: dict) -> dict:
    """Real-ESRGAN general x4v3 (BSD-3) on CPU via ONNX Runtime, tiled so memory stays bounded.
    scale 2 runs the x4 model and downsamples with Lanczos, which looks sharper than a native x2 bicubic."""
    _limits(job)
    out = _out_dir(job)
    scale = int(job.get("scale", 2))
    if scale not in (2, 3, 4):
        raise BadInput("AI upscale supports 2x, 3x or 4x.")
    max_input = int(job.get("max_input_pixels", 4_000_000))
    img = _open_image(job["input"])
    if img.width * img.height > max_input:
        raise BadInput(f"AI upscale accepts images up to {max_input / 1_000_000:g} megapixels; resize it first.")

    alpha = img.getchannel("A") if img.mode in ("RGBA", "LA") else None
    rgb = np.asarray(img.convert("RGB"), dtype=np.float32) / 255.0
    sr = _run_tiled(_session(job["model_path"], int(job.get("threads", 0))), rgb, tile=int(job.get("tile", 256)))
    result = Image.fromarray(np.clip(sr * 255.0 + 0.5, 0, 255).astype(np.uint8), "RGB")
    target = (img.width * scale, img.height * scale)
    if result.size != target:
        result = result.resize(target, Image.Resampling.LANCZOS)
    if alpha is not None:
        result.putalpha(alpha.resize(target, Image.Resampling.LANCZOS))

    fmt = job.get("format", "png").lower()
    name = job.get("output_name", f"upscaled.{'jpg' if fmt == 'jpeg' else fmt}")
    if fmt == "jpeg":
        _flatten(result).save(out / name, format="JPEG", quality=int(job.get("quality", 92)), optimize=True, progressive=True)
    elif fmt == "webp":
        result.save(out / name, format="WEBP", quality=int(job.get("quality", 92)), method=4)
    else:
        result.save(out / name, format="PNG", optimize=True)
    return {"files": [{"name": name, "width": target[0], "height": target[1]}]}


def _session(model_path: str, threads: int):
    global _SESSION
    if _SESSION is None:
        import onnxruntime as ort

        options = ort.SessionOptions()
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        if threads > 0:
            options.intra_op_num_threads = threads
        _SESSION = ort.InferenceSession(model_path, sess_options=options, providers=["CPUExecutionProvider"])
    return _SESSION


def _run_tiled(session, rgb: np.ndarray, tile: int, pad: int = 16, factor: int = 4) -> np.ndarray:
    """Runs the model over overlapping tiles and stitches the centres, so seams don't show."""
    h, w, _ = rgb.shape
    out = np.zeros((h * factor, w * factor, 3), dtype=np.float32)
    name = session.get_inputs()[0].name
    for y in range(0, h, tile):
        for x in range(0, w, tile):
            y0, x0 = max(0, y - pad), max(0, x - pad)
            y1, x1 = min(h, y + tile + pad), min(w, x + tile + pad)
            patch = np.ascontiguousarray(rgb[y0:y1, x0:x1].transpose(2, 0, 1)[None])
            sr = session.run(None, {name: patch})[0][0].transpose(1, 2, 0)
            ty1, tx1 = min(h, y + tile), min(w, x + tile)
            oy, ox = (y - y0) * factor, (x - x0) * factor
            out[y * factor:ty1 * factor, x * factor:tx1 * factor] = sr[oy:oy + (ty1 - y) * factor, ox:ox + (tx1 - x) * factor]
    return out


# ---------------------------------------------------------------------------------------------------------- info

def info(job: dict) -> dict:
    """Engine self-check used by the API at startup."""
    return {"python": os.sys.version.split()[0], "pdfium": pdfium.PDFIUM_INFO.version if hasattr(pdfium, "PDFIUM_INFO") else "ok"}


COMMANDS = {
    "pdf_to_images": pdf_to_images,
    "images_to_pdf": images_to_pdf,
    "pdf_to_docx": pdf_to_docx,
    "images_to_docx": images_to_docx,
    "upscale": upscale,
    "info": info,
}
