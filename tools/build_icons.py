"""Build multi-size Windows .ico files for the app and associated PDF files."""

from __future__ import annotations

import struct
from io import BytesIO
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
APP_DIR = ROOT / "src" / "SignPdf.App"
PREVIEW_DIR = ROOT / "tools" / "icon-preview"

TEAL = (15, 108, 92, 255)
TEAL_DARK = (11, 82, 70, 255)
WHITE = (255, 255, 255, 255)
CLEAR = (0, 0, 0, 0)

# Explorer / taskbar / DPI sizes Windows actually asks for.
SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def _png_bytes(im: Image.Image) -> bytes:
    buf = BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def _and_mask(im: Image.Image) -> bytes:
    w, h = im.size
    stride = ((w + 31) // 32) * 4
    px = im.load()
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray(stride)
        for x in range(w):
            if px[x, y][3] < 128:
                row[x // 8] |= 0x80 >> (x % 8)
        rows.append(bytes(row))
    return b"".join(rows)


def _xor_dib(im: Image.Image) -> bytes:
    w, h = im.size
    px = im.load()
    out = bytearray(w * h * 4)
    i = 0
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            out[i : i + 4] = (b, g, r, a)
            i += 4
    return bytes(out)


def _bmp_icon_bytes(im: Image.Image) -> bytes:
    w, h = im.size
    xor = _xor_dib(im)
    mask = _and_mask(im)
    header = struct.pack(
        "<IiiHHIIiiII",
        40,
        w,
        h * 2,
        1,
        32,
        0,
        len(xor) + len(mask),
        0,
        0,
        0,
        0,
    )
    return header + xor + mask


def write_ico(path: Path, images: dict[int, Image.Image]) -> None:
    entries: list[tuple[int, bytes]] = []
    for size in SIZES:
        im = images[size].convert("RGBA")
        if size >= 64:
            data = _png_bytes(im)
        else:
            data = _bmp_icon_bytes(im)
        entries.append((size, data))

    count = len(entries)
    offset = 6 + 16 * count
    buf = BytesIO()
    buf.write(struct.pack("<HHH", 0, 1, count))
    blobs: list[bytes] = []
    for size, data in entries:
        stored = 0 if size >= 256 else size
        buf.write(
            struct.pack(
                "<BBBBHHII",
                stored,
                stored,
                0,
                0,
                1,
                32,
                len(data),
                offset,
            )
        )
        blobs.append(data)
        offset += len(data)
    for blob in blobs:
        buf.write(blob)
    path.write_bytes(buf.getvalue())


def _put(im: Image.Image, x: int, y: int, color: tuple[int, int, int, int]) -> None:
    if 0 <= x < im.width and 0 <= y < im.height:
        im.putpixel((x, y), color)


def _hline(im: Image.Image, x0: int, x1: int, y: int, color: tuple[int, int, int, int]) -> None:
    for x in range(x0, x1 + 1):
        _put(im, x, y, color)


def _vline(im: Image.Image, x: int, y0: int, y1: int, color: tuple[int, int, int, int]) -> None:
    for y in range(y0, y1 + 1):
        _put(im, x, y, color)


def _fill(im: Image.Image, x0: int, y0: int, x1: int, y1: int, color: tuple[int, int, int, int]) -> None:
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            _put(im, x, y, color)


def draw_file_16() -> Image.Image:
    """Crisp 16px document: 1px teal outline, dog-ear, two text bars."""
    im = Image.new("RGBA", (16, 16), CLEAR)
    # Page 10x13 at (3,1)
    l, t, r, b = 3, 1, 12, 14
    _fill(im, l, t, r, b, WHITE)
    # Dog-ear 3x3
    _fill(im, r - 2, t, r, t + 2, TEAL)
    _put(im, r - 2, t, WHITE)
    _put(im, r - 1, t, TEAL)
    _put(im, r, t, TEAL)
    _put(im, r, t + 1, TEAL)
    _put(im, r - 2, t + 2, TEAL)
    # Outline
    _hline(im, l, r - 3, t, TEAL)
    _hline(im, l, r, b, TEAL)
    _vline(im, l, t, b, TEAL)
    _vline(im, r, t + 3, b, TEAL)
    _put(im, r - 2, t, TEAL)
    # Fold inner edge
    _put(im, r - 2, t + 1, TEAL)
    _put(im, r - 1, t + 2, TEAL)
    # Text
    _hline(im, 5, 10, 6, TEAL)
    _hline(im, 5, 9, 8, TEAL)
    _hline(im, 5, 8, 11, TEAL)
    return im


def draw_file_20() -> Image.Image:
    im = Image.new("RGBA", (20, 20), CLEAR)
    l, t, r, b = 4, 2, 15, 17
    _fill(im, l, t, r, b, WHITE)
    _fill(im, r - 3, t, r, t + 3, TEAL)
    _hline(im, l, r - 4, t, TEAL)
    _hline(im, l, r, b, TEAL)
    _vline(im, l, t, b, TEAL)
    _vline(im, r, t + 4, b, TEAL)
    _hline(im, r - 3, r, t, TEAL)
    _vline(im, r, t, t + 3, TEAL)
    _hline(im, r - 3, r, t + 3, TEAL)
    _vline(im, r - 3, t, t + 3, TEAL)
    _hline(im, 6, 13, 7, TEAL)
    _hline(im, 6, 12, 9, TEAL)
    _hline(im, 6, 10, 13, TEAL)
    _put(im, 7, 14, TEAL)
    _hline(im, 8, 12, 15, TEAL)
    return im


def _draw_file_vector(size: int) -> Image.Image:
    """Supersampled document matching icon-small.png."""
    scale = 8
    s = size * scale
    im = Image.new("RGBA", (s, s), CLEAR)
    d = ImageDraw.Draw(im)

    m = round(s * 0.12)
    left, top = m, round(s * 0.08)
    right, bottom = s - m - 1, s - m
    fold = max(scale * 3, round((right - left) * 0.28))
    radius = max(scale, round(s * 0.06))
    stroke = max(scale, round(s * 0.07))

    page = [
        (left + radius, top),
        (right - fold, top),
        (right, top + fold),
        (right, bottom - radius),
        (right - radius, bottom),
        (left + radius, bottom),
        (left, bottom - radius),
        (left, top + radius),
    ]
    d.polygon(page, fill=WHITE)
    d.rounded_rectangle(
        (left, top, right, bottom),
        radius=radius,
        outline=None,
        fill=None,
    )
    # Re-fill with cut corner by covering fold triangle in teal, then outline.
    d.polygon(page, fill=WHITE)
    d.polygon(
        [
            (right - fold, top),
            (right, top + fold),
            (right - fold, top + fold),
        ],
        fill=TEAL,
    )
    d.line([(right - fold, top), (right - fold, top + fold), (right, top + fold)], fill=TEAL, width=stroke)

    # Stroke the page by drawing a slightly larger/smaller pair.
    d.line(
        [
            (left, top + radius),
            (left, bottom - radius),
        ],
        fill=TEAL,
        width=stroke,
    )
    d.line([(left + radius, bottom), (right - radius, bottom)], fill=TEAL, width=stroke)
    d.line([(right, top + fold), (right, bottom - radius)], fill=TEAL, width=stroke)
    d.line([(left + radius, top), (right - fold, top)], fill=TEAL, width=stroke)
    d.arc((left, top, left + radius * 2, top + radius * 2), 180, 270, fill=TEAL, width=stroke)
    d.arc((left, bottom - radius * 2, left + radius * 2, bottom), 90, 180, fill=TEAL, width=stroke)
    d.arc((right - radius * 2, bottom - radius * 2, right, bottom), 0, 90, fill=TEAL, width=stroke)

    inner_l = left + round(s * 0.18)
    inner_r = right - round(s * 0.18)
    bar_h = max(scale, round(s * 0.055))
    y1 = top + round(s * 0.28)
    y2 = y1 + round(s * 0.12)
    d.rounded_rectangle((inner_l, y1, inner_r, y1 + bar_h), radius=bar_h / 2, fill=TEAL)
    d.rounded_rectangle((inner_l, y2, inner_l + round((inner_r - inner_l) * 0.78), y2 + bar_h), radius=bar_h / 2, fill=TEAL)

    # Signature
    sig_y = top + round(s * 0.62)
    sig_x0 = inner_l
    sig_x1 = inner_r - scale
    sw = max(scale, round(s * 0.045))
    path = [
        (sig_x0, sig_y + round(s * 0.06)),
        (sig_x0 + round(s * 0.08), sig_y - round(s * 0.04)),
        (sig_x0 + round(s * 0.16), sig_y + round(s * 0.10)),
        (sig_x0 + round(s * 0.28), sig_y),
        (sig_x1, sig_y + round(s * 0.04)),
    ]
    d.line(path, fill=TEAL, width=sw, joint="curve")
    return im.resize((size, size), Image.Resampling.LANCZOS)


def draw_app_small(size: int) -> Image.Image:
    """Teal tile + white document glyph — readable on taskbar at 16–32px."""
    if size <= 16:
        im = Image.new("RGBA", (16, 16), CLEAR)
        _fill(im, 1, 1, 14, 14, TEAL)
        _fill(im, 5, 3, 11, 12, WHITE)
        _fill(im, 9, 3, 11, 5, TEAL)
        _hline(im, 6, 10, 7, TEAL)
        _hline(im, 6, 9, 9, TEAL)
        return im

    scale = 8
    s = size * scale
    im = Image.new("RGBA", (s, s), CLEAR)
    d = ImageDraw.Draw(im)
    pad = round(s * 0.06)
    radius = round(s * 0.22)
    d.rounded_rectangle((pad, pad, s - pad - 1, s - pad - 1), radius=radius, fill=TEAL)

    m = round(s * 0.26)
    left, top = m, round(s * 0.20)
    right, bottom = s - m - 1, s - round(s * 0.20)
    fold = round((right - left) * 0.32)
    d.polygon(
        [
            (left, top + fold // 3),
            (left, bottom),
            (right, bottom),
            (right, top + fold),
            (right - fold, top),
            (left + fold // 3, top),
        ],
        fill=WHITE,
    )
    d.polygon(
        [(right - fold, top), (right, top + fold), (right - fold, top + fold)],
        fill=TEAL_DARK,
    )
    bar_h = max(scale, round(s * 0.06))
    y1 = top + round(s * 0.22)
    d.rounded_rectangle((left + round(s * 0.08), y1, right - round(s * 0.10), y1 + bar_h), radius=bar_h / 2, fill=TEAL)
    d.rounded_rectangle(
        (left + round(s * 0.08), y1 + round(s * 0.12), right - round(s * 0.18), y1 + round(s * 0.12) + bar_h),
        radius=bar_h / 2,
        fill=TEAL,
    )
    return im.resize((size, size), Image.Resampling.LANCZOS)


def _downscale(src: Image.Image, size: int) -> Image.Image:
    im = src.convert("RGBA").resize((size, size), Image.Resampling.LANCZOS)
    if 32 <= size <= 96:
        im = im.filter(ImageFilter.UnsharpMask(radius=0.8, percent=90, threshold=2))
    return im


def build_file_images(src_small: Image.Image) -> dict[int, Image.Image]:
    out: dict[int, Image.Image] = {}
    for size in SIZES:
        if size <= 16:
            out[size] = draw_file_16()
        elif size <= 20:
            out[size] = draw_file_20()
        elif size <= 32:
            out[size] = _draw_file_vector(size)
        else:
            out[size] = _downscale(src_small, size)
    return out


def build_app_images(src_hi: Image.Image) -> dict[int, Image.Image]:
    out: dict[int, Image.Image] = {}
    for size in SIZES:
        if size <= 32:
            out[size] = draw_app_small(size)
        else:
            out[size] = _downscale(src_hi, size)
    return out


def save_preview(images: dict[int, Image.Image], name: str) -> None:
    PREVIEW_DIR.mkdir(parents=True, exist_ok=True)
    gap = 12
    height = 256
    width = gap + sum(max(size, 16) + gap for size in SIZES)
    sheet = Image.new("RGBA", (width, height + 24), (40, 44, 52, 255))
    x = gap
    for size in SIZES:
        im = images[size]
        y = (height - size) // 2
        sheet.alpha_composite(im, (x, y))
        x += max(size, 16) + gap
    sheet.save(PREVIEW_DIR / f"{name}.png")
    for size in (16, 32, 48, 256):
        images[size].save(PREVIEW_DIR / f"{name}-{size}.png")


def main() -> None:
    src_hi = Image.open(APP_DIR / "icon.png")
    src_small = Image.open(APP_DIR / "icon-small.png")
    files = build_file_images(src_small)
    apps = build_app_images(src_hi)
    write_ico(APP_DIR / "pdf.ico", files)
    write_ico(APP_DIR / "app.ico", apps)
    save_preview(files, "pdf")
    save_preview(apps, "app")
    print("wrote", APP_DIR / "pdf.ico", (APP_DIR / "pdf.ico").stat().st_size)
    print("wrote", APP_DIR / "app.ico", (APP_DIR / "app.ico").stat().st_size)


if __name__ == "__main__":
    main()
