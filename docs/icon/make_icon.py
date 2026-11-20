"""Dyn 应用图标生成：一个「D」，右侧笔画是波形（dynamic + 壁纸的起伏）。

用法：python docs/icon/make_icon.py
产物：docs/icon/app.ico、docs/icon/review.png、app/Assets/*（WinUI 资源）
"""
from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
S = 4096  # 超采样画布

VIOLET = (124, 92, 255)
BLUE = (38, 143, 255)
INK = (255, 255, 255)


def _lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def _gradient(w: int, h: int, c0, c1) -> Image.Image:
    """竖向线性渐变。"""
    strip = Image.new("RGB", (1, h))
    for y in range(h):
        strip.putpixel((0, y), _lerp(c0, c1, y / max(1, h - 1)))
    return strip.resize((w, h))


def render(size: int = S) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    # 圆角方块（Fluent 观感：圆角约 22.5%）
    radius = int(size * 0.225)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)

    tile = _gradient(size, size, VIOLET, BLUE).convert("RGBA")
    # 左上柔光
    glow = Image.new("L", (size, size), 0)
    gd = ImageDraw.Draw(glow)
    gd.ellipse([-size * 0.35, -size * 0.55, size * 0.85, size * 0.65], fill=90)
    glow = glow.filter(ImageFilter.GaussianBlur(size * 0.05))
    tile.paste(Image.new("RGBA", (size, size), (255, 255, 255, 255)), (0, 0), glow)
    img.paste(tile, (0, 0), mask)

    # ── 主形：D ──────────────────────────────────────────────
    stroke = int(size * 0.088)
    bar_x0, bar_x1 = size * 0.265, size * 0.265 + stroke
    top, bottom = size * 0.255, size * 0.745

    glyph = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glyph)
    # 竖笔（带圆头）
    gd.rounded_rectangle([bar_x0, top - stroke / 2, bar_x1, bottom + stroke / 2], radius=stroke / 2, fill=INK)

    # 波形碗：半圆轮廓 + 1.5 个周期的起伏（读作 D，又能看出动）
    steps = 900
    r = stroke / 2
    cx = bar_x1 - stroke / 2
    cy = (top + bottom) / 2
    radius_y = (bottom - top - stroke) / 2
    amp = size * 0.040
    for i in range(steps + 1):
        t = i / steps
        base = cx + radius_y * math.sin(math.pi * t)
        x = base + amp * math.sin(2 * math.pi * 1.5 * t) * math.sin(math.pi * t)
        y = top + stroke / 2 + (bottom - top - stroke) * t
        gd.ellipse([x - r, y - r, x + r, y + r], fill=INK)

    # 按包围盒放大 64% 高并整体居中（手算坐标会偏左）
    box = glyph.getbbox()
    g = glyph.crop(box)
    target_h = int(size * 0.64)
    k = target_h / g.height
    g = g.resize((round(g.width * k), target_h), Image.LANCZOS)
    glyph = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    glyph.paste(g, ((size - g.width) // 2, (size - g.height) // 2))

    img.alpha_composite(glyph)

    # 内侧低透明描边，压住边缘
    border = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(border).rounded_rectangle(
        [1, 1, size - 2, size - 2], radius=radius, outline=(255, 255, 255, 46), width=max(2, size // 340))
    img.alpha_composite(border)
    return img


def downsample(img: Image.Image, px: int) -> Image.Image:
    return img.resize((px, px), Image.LANCZOS)


def main() -> None:
    icon_dir = ROOT / "docs" / "icon"
    assets = ROOT / "app" / "Assets"
    icon_dir.mkdir(parents=True, exist_ok=True)

    big = render()
    big.save(icon_dir / "app-1024.png")

    # Windows 多尺寸 ICO
    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    big.save(icon_dir / "app.ico", sizes=sizes)

    # WinUI 资源（模板占位图换成自己的）
    targets = {
        "Square44x44Logo.scale-200.png": 88,
        "Square44x44Logo.targetsize-24_altform-unplated.png": 24,
        "Square44x44Logo.targetsize-48_altform-lightunplated.png": 48,
        "Square150x150Logo.scale-200.png": 300,
        "StoreLogo.png": 50,
        "LockScreenLogo.scale-200.png": 48,
        "AppIcon.ico": 256,
    }
    for name, px in targets.items():
        if name.endswith(".ico"):
            downsample(big, px).save(assets / name, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (256, 256)])
        else:
            downsample(big, px).save(assets / name)

    # 宽幅与启动图：透明底 + 居中图标
    for name, (w, h), px in [
        ("SplashScreen.scale-200.png", (1240, 600), 300),
        ("Wide310x150Logo.scale-200.png", (620, 300), 150),
    ]:
        canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        canvas.alpha_composite(downsample(big, px), ((w - px) // 2, (h - px) // 2))
        canvas.save(assets / name)

    # 评审图：1024 大图 + 48/32/16 实尺寸，中性底
    sheet = Image.new("RGB", (1180, 1180), (242, 244, 246))
    sheet.paste(big.resize((1024, 1024), Image.LANCZOS), (78, 40), big.resize((1024, 1024), Image.LANCZOS))
    x = 78
    for px in (48, 32, 16):
        sheet.paste(downsample(big, px), (x, 1100), downsample(big, px))
        x += px + 40
    sheet.save(icon_dir / "review.png")
    print("icon ok:", icon_dir / "app.ico")


if __name__ == "__main__":
    main()
