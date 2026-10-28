"""Draws Notch's icon, the pill the C# tray icon draws (src/Notch.App/Shell/TrayIcon.cs), as a
multi-size .ico and a 512 px PNG. Uses only the standard library.

    python3 rust/tools/make-icon.py rust/crates/notch-app/icons
"""

import struct
import sys
import zlib
from pathlib import Path

SIZES = [16, 24, 32, 48, 64, 128, 256]
SUPERSAMPLE = 4


def pill(size):
    """RGBA rows of a black pill with a white outline, in the proportions of the 32 px tray icon:
    a 28 x 14 pill centred at y 16, outlined 2 px wide."""
    scale = size / 32
    left, right = 2 * scale, 30 * scale
    top, bottom = 9 * scale, 23 * scale
    radius = (bottom - top) / 2
    stroke = max(2 * scale, 1)
    cy = (top + bottom) / 2

    def distance(x, y):
        # Signed distance to the pill's edge: negative inside.
        cx = min(max(x, left + radius), right - radius)
        return ((x - cx) ** 2 + (y - cy) ** 2) ** 0.5 - radius

    rows = []
    n = SUPERSAMPLE
    for py in range(size):
        row = bytearray()
        for px in range(size):
            fill = outline = 0
            for sy in range(n):
                for sx in range(n):
                    d = distance(px + (sx + 0.5) / n, py + (sy + 0.5) / n)
                    if d <= stroke / 2:
                        if d >= -stroke / 2:
                            outline += 1
                        else:
                            fill += 1
            total = n * n
            alpha = (fill + outline) / total
            white = outline / (fill + outline) if fill + outline else 0
            v = round(255 * white)
            row += bytes((v, v, v, round(255 * alpha)))
        rows.append(bytes(row))
    return rows


def png(size):
    rows = pill(size)
    raw = b"".join(b"\0" + r for r in rows)

    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def ico(sizes):
    images = [png(s) for s in sizes]
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for s, data in zip(sizes, images):
        dim = 0 if s >= 256 else s
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    return out + b"".join(images)


def main():
    folder = Path(sys.argv[1] if len(sys.argv) > 1 else ".")
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "icon.ico").write_bytes(ico(SIZES))
    (folder / "icon.png").write_bytes(png(512))


if __name__ == "__main__":
    main()
