#!/usr/bin/env python3
"""Renders the committed PNG/ICO brand assets from the SVG sources in assets/brand.

Requires: pip install resvg-py pillow
"""
import io
import re
from pathlib import Path

import resvg_py
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
BRAND = ROOT / "assets" / "brand"
IVORY = "#F0EEE6"


def render(svg, size):
    png = resvg_py.svg_to_bytes(svg_string=svg, width=size, height=size)
    return Image.open(io.BytesIO(bytes(png))).convert("RGBA")


def spark_path():
    source = (BRAND / "claude-spark.svg").read_text(encoding="utf-8")
    return re.search(r'<path[^>]*d="([^"]+)"', source).group(1)


def spark_tile(canvas, inset, radius, spark, background=IVORY, outline=None):
    """Ivory tile with the centred Claude spark, in `canvas` user units."""
    tile = canvas - inset * 2
    scale = spark / 148.2
    offset = (canvas - spark) / 2
    corner = f' rx="{radius}"' if radius else ""
    # A hairline keeps the ivory tile visible on ivory surfaces.
    stroke = (
        f'<rect x="{inset + 3}" y="{inset + 3}" width="{tile - 6}" height="{tile - 6}" rx="{radius - 3}" '
        f'fill="none" stroke="{outline}" stroke-width="6"/>'
        if outline else ""
    )
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {canvas} {canvas}">'
        f'<rect x="{inset}" y="{inset}" width="{tile}" height="{tile}"{corner} fill="{background}"/>{stroke}'
        f'<g transform="translate({offset},{offset}) scale({scale}) translate(-75.96,-223.53)">'
        f'<path fill="#D97757" d="{spark_path()}"/></g></svg>'
    )


def main():
    mark = (BRAND / "presence-mark.svg").read_text(encoding="utf-8")
    small = (BRAND / "presence-mark-small.svg").read_text(encoding="utf-8")

    # Same 544 px grid as codex-app-icon.png, so both render at one overscan.
    render(spark_tile(544, 66, 92, 250, outline="#D9D4C7"), 544).save(ROOT / "assets" / "claude-code-icon.png", optimize=True)
    # Discord crops large images itself: full bleed, generous margin.
    render(spark_tile(1024, 0, 0, 560), 1024).save(ROOT / "assets" / "discord" / "claude-code.png", optimize=True)

    render(mark, 1024).save(ROOT / "assets" / "discord-codex.png", optimize=True)
    render(mark, 128).save(ROOT / "assets" / "presence-mark.png", optimize=True)

    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [render(small if size <= 24 else mark, size) for size in sizes]
    frames[-1].save(
        ROOT / "assets" / "codex-presence.ico",
        format="ICO",
        sizes=[(size, size) for size in sizes],
        append_images=frames[:-1],
    )
    print("Rendered brand assets into", ROOT / "assets")


if __name__ == "__main__":
    main()
