"""Build the HyperHarbor brand asset kit: SVG, PNG and ICO files."""
import io
import json
import os
import shutil

import cairosvg
import uharfbuzz as hb
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from PIL import Image

ROOT = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(ROOT, "brand")
FONT_DIR = os.path.join(ROOT, "fontwork", "node_modules", "@fontsource", "manrope", "files")

INK = "#121A26"
FOG = "#F3F1EC"
ACCENT = "#E39B2E"
SLEEP = "#8A93A1"

for sub in ("svg", "png", "icons"):
    os.makedirs(os.path.join(OUT, sub), exist_ok=True)


# ---------------------------------------------------------------------------
# Mark geometry (100 x 100 viewBox)
# ---------------------------------------------------------------------------
def mark_body(stroke, slip_top, slip_bottom_fill, slip_bottom_stroke, stroke_width=12,
              hollow_bottom=True, small=False):
    """Return the inner SVG elements of the Slip mark.

    stroke: color of the H strokes
    slip_top: fill color of the top slip, or None to draw it hollow in `stroke`
    slip_bottom_fill: fill color of the bottom slip, or None for hollow
    slip_bottom_stroke: stroke color used when the bottom slip is hollow
    small: thicker geometry tuned for 16 to 24 px rendering (drops the bottom slip)
    """
    if small:
        parts = [
            f'<path d="M28 16 V84 M72 16 V84 M28 50 H72" fill="none" stroke="{stroke}" '
            f'stroke-width="14" stroke-linecap="round" stroke-linejoin="round"/>'
        ]
        if slip_top:
            parts.append(f'<rect x="41" y="22" width="18" height="14" rx="4" fill="{slip_top}"/>')
        else:
            parts.append(f'<rect x="41" y="22" width="18" height="14" rx="4" fill="none" '
                         f'stroke="{stroke}" stroke-width="4"/>')
        return "\n  ".join(parts)

    parts = [
        f'<path d="M28 16 V84 M72 16 V84 M28 50 H72" fill="none" stroke="{stroke}" '
        f'stroke-width="{stroke_width}" stroke-linecap="round" stroke-linejoin="round"/>'
    ]
    if slip_top:
        parts.append(f'<rect x="42" y="23" width="16" height="12" rx="3.5" fill="{slip_top}"/>')
    else:
        parts.append(f'<rect x="42" y="23" width="16" height="12" rx="3.5" fill="none" '
                     f'stroke="{stroke}" stroke-width="3"/>')
    if slip_bottom_fill:
        parts.append(f'<rect x="42" y="65" width="16" height="12" rx="3.5" fill="{slip_bottom_fill}"/>')
    elif hollow_bottom:
        parts.append(f'<rect x="42" y="65" width="16" height="12" rx="3.5" fill="none" '
                     f'stroke="{slip_bottom_stroke}" stroke-width="3"/>')
    return "\n  ".join(parts)


def svg_doc(inner, size=100, viewbox="0 0 100 100", background=None, radius=0, title="HyperHarbor"):
    bg = ""
    if background:
        bg = f'<rect width="100" height="100" rx="{radius}" fill="{background}"/>\n  '
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{viewbox}" width="{size}" height="{size}" '
        f'role="img" aria-label="{title}">\n  {bg}{inner}\n</svg>\n'
    )


def write(path, text):
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)


marks = {
    "mark": svg_doc(mark_body(INK, ACCENT, None, INK)),
    "mark-on-dark": svg_doc(mark_body(FOG, ACCENT, None, FOG)),
    "mark-mono": svg_doc(mark_body("currentColor", "currentColor", None, "currentColor")),
    "mark-small": svg_doc(mark_body(INK, ACCENT, None, INK, small=True)),
    "mark-small-on-dark": svg_doc(mark_body(FOG, ACCENT, None, FOG, small=True)),
    "tray-awake": svg_doc(mark_body(FOG, ACCENT, None, FOG, small=True), title="HyperHarbor host awake"),
    "tray-asleep": svg_doc(mark_body(SLEEP, None, None, SLEEP, small=True), title="HyperHarbor host asleep"),
    "tray-connected": svg_doc(
        '<path d="M28 16 V84 M72 16 V84 M28 50 H72" fill="none" stroke="' + FOG + '" '
        'stroke-width="14" stroke-linecap="round" stroke-linejoin="round"/>\n  '
        '<rect x="41" y="22" width="18" height="14" rx="4" fill="' + ACCENT + '"/>\n  '
        '<rect x="41" y="64" width="18" height="14" rx="4" fill="' + ACCENT + '"/>',
        title="HyperHarbor connected"),
    "tray-awake-light-theme": svg_doc(mark_body(INK, ACCENT, None, INK, small=True), title="HyperHarbor host awake"),
    "tray-asleep-light-theme": svg_doc(mark_body(SLEEP, None, None, SLEEP, small=True), title="HyperHarbor host asleep"),
    "tray-connected-light-theme": svg_doc(
        '<path d="M28 16 V84 M72 16 V84 M28 50 H72" fill="none" stroke="' + INK + '" '
        'stroke-width="14" stroke-linecap="round" stroke-linejoin="round"/>\n  '
        '<rect x="41" y="22" width="18" height="14" rx="4" fill="' + ACCENT + '"/>\n  '
        '<rect x="41" y="64" width="18" height="14" rx="4" fill="' + ACCENT + '"/>',
        title="HyperHarbor connected"),
    "app-icon": svg_doc(mark_body(FOG, ACCENT, None, FOG, stroke_width=11), background=INK, radius=22),
    "app-icon-accent": svg_doc(mark_body(INK, INK, None, INK, stroke_width=11), background=ACCENT, radius=22),
    "favicon": svg_doc(mark_body(FOG, ACCENT, None, FOG, small=True), background=INK, radius=22),
}

for name, text in marks.items():
    write(os.path.join(OUT, "svg", f"{name}.svg"), text)


# ---------------------------------------------------------------------------
# Wordmark: Manrope text converted to vector paths with HarfBuzz shaping
# ---------------------------------------------------------------------------
def load_font(weight):
    path = os.path.join(FONT_DIR, f"manrope-latin-{weight}-normal.woff2")
    font = TTFont(path)
    # HarfBuzz cannot read WOFF2 directly, so hand it an uncompressed TTF image of the same font.
    font.flavor = None
    ttf_bytes = io.BytesIO()
    font.save(ttf_bytes)
    blob = hb.Blob(ttf_bytes.getvalue())
    face = hb.Face(blob)
    hb_font = hb.Font(face)
    return font, hb_font


def shape_run(text, weight, x_offset, font_size, letter_spacing_em):
    """Return (svg path elements, advance width) for a text run at the given origin."""
    font, hb_font = load_font(weight)
    upem = font["head"].unitsPerEm
    scale = font_size / upem
    glyph_set = font.getGlyphSet()
    glyph_order = font.getGlyphOrder()

    buf = hb.Buffer()
    buf.add_str(text)
    buf.guess_segment_properties()
    hb.shape(hb_font, buf, {"kern": True, "liga": True})

    paths = []
    cursor = x_offset
    tracking = letter_spacing_em * font_size
    for info, pos in zip(buf.glyph_infos, buf.glyph_positions):
        glyph_name = glyph_order[info.codepoint]
        pen = SVGPathPen(glyph_set)
        # Flip Y and scale from font units to pixels, then translate to the cursor.
        tpen = TransformPen(pen, (scale, 0, 0, -scale, cursor + pos.x_offset * scale, -pos.y_offset * scale))
        glyph_set[glyph_name].draw(tpen)
        d = pen.getCommands()
        if d:
            paths.append(d)
        cursor += pos.x_advance * scale + tracking
    return paths, cursor - x_offset


def build_wordmark(fill, font_size=100):
    """Hyper in weight 500, Harbor in weight 800, baseline at y = 0."""
    hyper_paths, hyper_width = shape_run("Hyper", "500", 0, font_size, -0.02)
    harbor_paths, harbor_width = shape_run("Harbor", "800", hyper_width, font_size, -0.02)
    d = " ".join(hyper_paths + harbor_paths)
    total = hyper_width + harbor_width
    return f'<path fill="{fill}" d="{d}"/>', total


def lockup_svg(ink_color, mark_inner, font_size=100):
    """Mark on the left, wordmark on the right, matching the canvas proportions.

    On the canvas the 64 px mark sat beside 34 px type, so the mark box is 1.88 em
    and is centered on the middle of the cap-height band.
    """
    word_path, word_width = build_wordmark(ink_color, font_size)
    cap_height = font_size * 0.72
    mark_px = font_size * 1.88
    gap = font_size * 0.40
    band_center = -cap_height / 2  # baseline is y = 0, text extends upward
    mark_top = band_center - mark_px / 2
    word_x = mark_px + gap
    width = word_x + word_width
    top = mark_top
    bottom = font_size * 0.28  # room for descenders of the p and y
    height = bottom - top
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 {top:.2f} {width:.2f} {height:.2f}" '
        f'width="{width:.0f}" height="{height:.0f}" role="img" aria-label="HyperHarbor">\n'
        f'  <g transform="translate(0 {mark_top:.2f}) scale({mark_px / 100:.4f})">\n'
        f'  {mark_inner}\n  </g>\n'
        f'  <g transform="translate({word_x:.2f} 0)">\n    {word_path}\n  </g>\n'
        f'</svg>\n'
    ), width, height


lockup_dark_ink, lw, lh = lockup_svg(INK, mark_body(INK, ACCENT, None, INK))
lockup_on_dark, _, _ = lockup_svg(FOG, mark_body(FOG, ACCENT, None, FOG))
write(os.path.join(OUT, "svg", "lockup-horizontal.svg"), lockup_dark_ink)
write(os.path.join(OUT, "svg", "lockup-horizontal-on-dark.svg"), lockup_on_dark)

for fill, name in ((INK, "wordmark"), (FOG, "wordmark-on-dark")):
    path, width = build_wordmark(fill)
    write(os.path.join(OUT, "svg", f"{name}.svg"),
          f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 -86 {width:.2f} 110" width="{width:.0f}" height="110" '
          f'role="img" aria-label="HyperHarbor">\n  {path}\n</svg>\n')


# ---------------------------------------------------------------------------
# PNG and ICO rendering
# ---------------------------------------------------------------------------
def render_png(svg_text, size, path=None, width=None, height=None):
    kwargs = {"output_width": width or size, "output_height": height or size}
    data = cairosvg.svg2png(bytestring=svg_text.encode("utf-8"), **kwargs)
    if path:
        with open(path, "wb") as handle:
            handle.write(data)
    return Image.open(io.BytesIO(data)).convert("RGBA")


def render_ico(svg_small, svg_large, sizes, path):
    """Use the small-tuned geometry up to 24 px and the full mark above."""
    frames = []
    for size in sizes:
        source = svg_small if size <= 24 else svg_large
        frames.append(render_png(source, size))
    # Pillow drops sizes larger than the first frame, so write the largest first.
    frames.sort(key=lambda f: f.width, reverse=True)
    frames[0].save(path, format="ICO", sizes=[(f.width, f.height) for f in frames], append_images=frames[1:])


png_dir = os.path.join(OUT, "png")
icon_dir = os.path.join(OUT, "icons")

for size in (16, 24, 32, 48, 64, 128, 256, 512, 1024):
    source = marks["mark-small"] if size <= 24 else marks["mark"]
    render_png(source, size, os.path.join(png_dir, f"mark-{size}.png"))
    source_dark = marks["mark-small-on-dark"] if size <= 24 else marks["mark-on-dark"]
    render_png(source_dark, size, os.path.join(png_dir, f"mark-on-dark-{size}.png"))

for size in (32, 64, 128, 256, 512, 1024):
    render_png(marks["app-icon"], size, os.path.join(icon_dir, f"app-icon-{size}.png"))
render_png(marks["app-icon-accent"], 1024, os.path.join(icon_dir, "app-icon-accent-1024.png"))

render_png(lockup_dark_ink, None, os.path.join(png_dir, "lockup-horizontal@2x.png"), width=int(lw * 2), height=int(lh * 2))
render_png(lockup_on_dark, None, os.path.join(png_dir, "lockup-horizontal-on-dark@2x.png"), width=int(lw * 2), height=int(lh * 2))

# Windows icons
render_ico(marks["favicon"], marks["app-icon"], (16, 24, 32, 48, 64, 128, 256), os.path.join(icon_dir, "app.ico"))
render_ico(marks["favicon"], marks["app-icon"], (16, 32, 48), os.path.join(icon_dir, "favicon.ico"))
for state in ("awake", "asleep", "connected"):
    render_ico(marks[f"tray-{state}"], marks[f"tray-{state}"], (16, 20, 24, 32, 48),
               os.path.join(icon_dir, f"tray-{state}.ico"))
    render_ico(marks[f"tray-{state}-light-theme"], marks[f"tray-{state}-light-theme"], (16, 20, 24, 32, 48),
               os.path.join(icon_dir, f"tray-{state}-light-theme.ico"))
shutil.copy(os.path.join(OUT, "svg", "favicon.svg"), os.path.join(icon_dir, "favicon.svg"))

# Brand tokens
tokens = {
    "name": "HyperHarbor",
    "colors": {
        "ink": INK,
        "fog": FOG,
        "accent": ACCENT,
        "accentAlternates": {"sea": "#2E8B8B", "coral": "#D8694A", "signal": "#3B82A0"},
        "sleep": SLEEP,
        "textMuted": "#5B6470",
        "border": "#E2DED5",
    },
    "typography": {
        "family": "Manrope",
        "wordmark": {"Hyper": 500, "Harbor": 800, "letterSpacing": "-0.02em"},
        "source": "https://fonts.google.com/specimen/Manrope (SIL Open Font License 1.1)",
    },
    "mark": {
        "viewBox": "0 0 100 100",
        "strokes": "M28 16 V84 M72 16 V84 M28 50 H72",
        "strokeWidth": 12,
        "slipTop": {"x": 42, "y": 23, "w": 16, "h": 12, "rx": 3.5},
        "slipBottom": {"x": 42, "y": 65, "w": 16, "h": 12, "rx": 3.5},
        "smallSizeVariant": "below 32 px use stroke width 14 and drop the bottom slip",
    },
    "states": {
        "awake": "top slip filled with accent, bottom slip hollow",
        "asleep": "all strokes in sleep grey, top slip hollow, no bottom slip",
        "connected": "both slips filled with accent",
    },
}
write(os.path.join(OUT, "brand.json"), json.dumps(tokens, indent=2) + "\n")

print("done", lw, lh)
