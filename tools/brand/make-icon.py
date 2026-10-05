#!/usr/bin/env python3
"""Растеризует логотип KHORS (src/Khors.App/Assets/logo.svg, из khors-site) в khors.ico.

Без зависимостей: геометрия SVG воспроизводится точно (viewBox 40x40):
  rect 40x40 rx=10 fill #101416; circle (20,20) r=14 stroke #e5bd70 width 2;
  path "M14 12v16m12-16-11 8 11 8" stroke #e5bd70 width 3.
Сглаживание — 4x4 подвыборки на пиксель. Запуск: python3 tools/brand/make-icon.py
"""
import math
import os
import struct
import zlib

BG = (0x10, 0x14, 0x16)
GOLD = (0xE5, 0xBD, 0x70)
SEGMENTS = [((14, 12), (14, 28)), ((26, 12), (15, 20)), ((15, 20), (26, 28))]
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "src", "Khors.App", "Assets", "khors.ico")


def in_rounded_rect(x, y, size=40.0, r=10.0):
    if not (0 <= x <= size and 0 <= y <= size):
        return False
    cx = min(max(x, r), size - r)
    cy = min(max(y, r), size - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def near_segment(x, y, a, b, half_width):
    (ax, ay), (bx, by) = a, b
    dx, dy = bx - ax, by - ay
    t = ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)
    if t < 0 or t > 1:  # stroke-linecap: butt
        return False
    px, py = ax + t * dx, ay + t * dy
    return (x - px) ** 2 + (y - py) ** 2 <= half_width ** 2


def color_at(x, y):
    """Цвет точки в координатах viewBox или None (прозрачно)."""
    if not in_rounded_rect(x, y):
        return None
    if abs(math.hypot(x - 20, y - 20) - 14) <= 1.0:
        return GOLD
    if any(near_segment(x, y, a, b, 1.5) for a, b in SEGMENTS):
        return GOLD
    return BG


def render(size, samples=4):
    rows = []
    scale = 40.0 / size
    for py in range(size):
        row = bytearray([0])  # PNG filter: none
        for px in range(size):
            r = g = b = a = 0
            for sy in range(samples):
                for sx in range(samples):
                    c = color_at((px + (sx + 0.5) / samples) * scale, (py + (sy + 0.5) / samples) * scale)
                    if c is not None:
                        r += c[0]
                        g += c[1]
                        b += c[2]
                        a += 1
            n = samples * samples
            row += bytes([r // a, g // a, b // a, 255 * a // n]) if a else bytes(4)
        rows.append(bytes(row))
    return png(size, b"".join(rows))


def png(size, raw):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)  # 8 бит, RGBA
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def ico(images):
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, data = b"", b""
    for size, image in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(image), offset)
        data += image
        offset += len(image)
    return header + entries + data


if __name__ == "__main__":
    images = [(s, render(s)) for s in SIZES]
    with open(OUT, "wb") as f:
        f.write(ico(images))
    print(f"{os.path.normpath(OUT)}: {len(images)} размеров")
