"""ALambda TTS 应用图标生成脚本（零依赖，只用标准库）。

设计：绿松石 #20808D 直角底 + 白色声波竖条。
输出：assets/app.ico（16/24/32/48/64/128/256 七种尺寸，PNG 压缩条目）

用法：python tools/make-icon.py
"""

import os
import struct
import zlib

ACCENT = (0x20, 0x80, 0x8D)
BAR = (0xFF, 0xFF, 0xFF)
MASTER = 256
SIZES = [16, 24, 32, 48, 64, 128, 256]


def make_master():
    """画 256x256 的 RGBA 图：底色 + 五根不同高度的声波条。"""
    pixels = [[(0, 0, 0, 0)] * MASTER for _ in range(MASTER)]
    for y in range(MASTER):
        for x in range(MASTER):
            pixels[y][x] = (*ACCENT, 255)

    # 声波条：(中心x, 半高) 做成对称的柱状波形
    bars = [(66, 26), (99, 52), (132, 84), (165, 52), (198, 26)]
    width = 20
    for cx, half in bars:
        top = MASTER // 2 - half
        bottom = MASTER // 2 + half
        for y in range(top, bottom):
            for x in range(cx - width // 2, cx + width // 2):
                if 0 <= x < MASTER and 0 <= y < MASTER:
                    pixels[y][x] = (*BAR, 255)

    # 圆角处理：手工裁掉四角各 12 px 的小三角，视觉上更像方形图标
    radius = 14
    for y in range(radius):
        for x in range(radius):
            dx, dy = radius - x, radius - y
            if dx * dx + dy * dy > radius * radius:
                for px, py in ((x, y), (MASTER - 1 - x, y), (x, MASTER - 1 - y), (MASTER - 1 - x, MASTER - 1 - y)):
                    pixels[py][px] = (0, 0, 0, 0)

    return pixels


def downscale(pixels, size):
    """盒式滤波缩放，够用且不需要第三方库。"""
    if size == MASTER:
        return pixels

    scale = MASTER / size
    out = [[(0, 0, 0, 0)] * size for _ in range(size)]
    for y in range(size):
        y0 = int(y * scale)
        y1 = max(y0 + 1, int((y + 1) * scale))
        for x in range(size):
            x0 = int(x * scale)
            x1 = max(x0 + 1, int((x + 1) * scale))
            r = g = b = a = 0
            count = 0
            for sy in range(y0, y1):
                for sx in range(x0, x1):
                    pr, pg, pb, pa = pixels[sy][sx]
                    r += pr * pa
                    g += pg * pa
                    b += pb * pa
                    a += pa
                    count += 1
            if a > 0:
                out[y][x] = (r // a, g // a, b // a, a // count)
            else:
                out[y][x] = (0, 0, 0, 0)
    return out


def encode_png(pixels, size):
    raw = bytearray()
    for y in range(size):
        raw.append(0)  # filter: none
        for x in range(size):
            r, g, b, a = pixels[y][x]
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(root, "assets")
    os.makedirs(assets, exist_ok=True)

    master = make_master()
    images = []
    for size in SIZES:
        images.append((size, encode_png(downscale(master, size), size)))

    out = bytearray()
    out += struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for size, data in images:
        out += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,
            0 if size >= 256 else size,
            0,
            0,
            1,
            32,
            len(data),
            offset,
        )
        offset += len(data)

    for _, data in images:
        out += data

    target = os.path.join(assets, "app.ico")
    with open(target, "wb") as handle:
        handle.write(out)
    print("wrote", target, len(out), "bytes")


if __name__ == "__main__":
    main()
