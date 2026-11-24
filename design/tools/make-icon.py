#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
xPuTTY 应用图标生成器
=====================
输出：
  shell/assets/xputty.ico                       Windows 多尺寸图标（16/20/24/32/40/48/64/128/256）
  design/screenshots/logo-preview.png           800x400 平铺预览（实际尺寸 + 像素级放大检查）
  design/tools/_frames/size-XX.png              各尺寸单帧 PNG（便于检查，可删）

设计（256x256 设计网格）：
  底   : 深色圆角方块（22% 圆角），垂直轻渐变 #12151A → #1C222B
  符号 : “>_” 终端提示符，与 design/mockups/main.html 标题栏 logo 同一路径，
         按 16→256 等比放大 16 倍（chevron + 底部横线）
  着色 : 沿符号包围盒对角线线性渐变 #4C8DFF → #2DD4BF，圆角笔画
  小尺寸: <=24px 去边框/去辉光、加粗笔画、放大符号；<=16px 进一步简化

依赖：Python 3.12 + Pillow（本机已有）。无任何外链资源。
"""

import io
import os
import struct
import sys
from PIL import Image, ImageChops, ImageDraw, ImageFont

# ---------------------------------------------------------------- 设计常量
S = 256  # 设计网格
BG_TOP = (18, 21, 26)  # #12151A
BG_BOTTOM = (28, 34, 43)  # #1C222B
BORDER = (46, 55, 66)  # #2E3742
BLUE = (76, 141, 255)  # #4C8DFF
TEAL = (45, 212, 191)  # #2DD4BF

# 与 main.html 标题栏 logo 完全一致的路径（16 网格 -> 256 网格，x16）
CHEV = [(51.2, 70.4), (105.6, 128.0), (51.2, 185.6)]
BAR = [(134.4, 185.6), (201.6, 185.6)]
# 渐变方向：符号包围盒（含笔画外沿）的左上 -> 右下
GRAD_P0 = (37.6, 56.8)
GRAD_P1 = (215.2, 199.2)

ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
PREVIEW_SIZES = [16, 24, 32, 48, 64, 128, 256]

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ICO_PATH = os.path.join(ROOT, "shell", "assets", "xputty.ico")
PREVIEW_PATH = os.path.join(ROOT, "design", "screenshots", "logo-preview.png")
FRAME_DIR = os.path.join(ROOT, "design", "tools", "_frames")


# ---------------------------------------------------------------- 小工具
def lerp_rgb(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return m


def vgrad(size, top, bottom):
    """垂直渐变（先在 1 x size 上算，再拉伸，足够平滑且快）"""
    strip = Image.new("RGB", (1, size))
    for y in range(size):
        strip.putpixel((0, y), lerp_rgb(top, bottom, y / max(1, size - 1)))
    return strip.resize((size, size), Image.BILINEAR).convert("RGBA")


def glyph_gradient(size):
    """按设计网格坐标做对角线渐变，裁到 size x size（设计坐标固定，所有尺寸一致）"""
    x0, y0 = GRAD_P0
    dx = GRAD_P1[0] - x0
    dy = GRAD_P1[1] - y0
    den = dx * dx + dy * dy
    im = Image.new("RGBA", (size, size))
    px = im.load()
    k = S / size
    for y in range(size):
        py = (y + 0.5) * k
        for x in range(size):
            pxx = (x + 0.5) * k
            t = ((pxx - x0) * dx + (py - y0) * dy) / den
            t = 0.0 if t < 0 else (1.0 if t > 1 else t)
            r, g, b = lerp_rgb(BLUE, TEAL, t)
            px[x, y] = (r, g, b, 255)
    return im


def radial_glow(size):
    g = Image.radial_gradient("L").resize((size, size), Image.BILINEAR)
    return ImageChops.invert(g)  # 中心亮、边缘暗


# ---------------------------------------------------------------- 尺寸策略
def cfg_for(size):
    """各尺寸的简化策略（越小越简化、笔画越粗、符号相对更大）"""
    if size <= 16:
        return dict(stroke=33.5, scale=1.05, radius=44.0, border=0.0, glow=0.0,
                    sheen=0.0, bar_end=210.0, border_alpha=0)
    if size <= 24:
        return dict(stroke=31.0, scale=1.03, radius=50.0, border=0.0, glow=0.0,
                    sheen=0.0, bar_end=206.0, border_alpha=0)
    if size <= 40:
        return dict(stroke=28.6, scale=1.0, radius=56.0, border=1.0, border_alpha=130,
                    glow=0.055, sheen=0.0, bar_end=201.6)
    if size <= 64:
        return dict(stroke=27.6, scale=0.97, radius=56.0, border=1.0, border_alpha=185,
                    glow=0.10, sheen=1.0, bar_end=201.6)
    if size <= 128:
        return dict(stroke=27.2, scale=0.96, radius=56.0, border=1.0, border_alpha=200,
                    glow=0.12, sheen=1.0, bar_end=201.6)
    return dict(stroke=27.2, scale=0.95, radius=56.0, border=1.0, border_alpha=200,
                glow=0.13, sheen=1.0, bar_end=201.6)


def render_icon(size):
    """按目标尺寸原生绘制（超采样后 LANCZOS 缩小），非简单缩放"""
    cfg = cfg_for(size)
    ss = 8 if size <= 40 else (6 if size <= 64 else 4)
    W = size * ss
    k = W / float(S)
    scale = cfg["scale"]

    def tr(p):
        return (128.0 + (p[0] - 128.0) * scale) * k, (128.0 + (p[1] - 128.0) * scale) * k

    bar = [(BAR[0][0], BAR[0][1]), (cfg["bar_end"], BAR[1][1])]

    mask = rounded_mask(W, cfg["radius"] * k)

    # 底：深色垂直轻渐变
    tile = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    tile.paste(vgrad(W, BG_TOP, BG_BOTTOM), (0, 0), mask)

    # 顶部高光（大尺寸才画，避免小尺寸发灰）
    if cfg["sheen"] > 0:
        gloss = Image.new("RGBA", (W, W), (255, 255, 255, 0))
        gd = ImageDraw.Draw(gloss)
        h = int(W * 0.42)
        for y in range(h):
            a = int(22 * (1 - y / float(h)))
            gd.line([(0, y), (W, y)], fill=(255, 255, 255, a))
        ga = ImageChops.multiply(gloss.getchannel("A"), mask)
        gloss.putalpha(ga)
        tile.alpha_composite(gloss)

    # 符号背后的微弱辉光
    if cfg["glow"] > 0:
        glow = Image.new("RGBA", (W, W), (BLUE[0], BLUE[1], BLUE[2], 0))
        a = radial_glow(W).point(lambda v: int(v * cfg["glow"]))
        glow.putalpha(ImageChops.multiply(a, mask))
        tile.alpha_composite(glow)

    # 符号蒙版（超采样上画，圆角笔画 + 圆端点）
    gm = Image.new("L", (W, W), 0)
    gd = ImageDraw.Draw(gm)
    sw = max(2, int(round(cfg["stroke"] * k)))
    gd.line([tr(p) for p in CHEV], fill=255, width=sw, joint="curve")
    gd.line([tr(bar[0]), tr(bar[1])], fill=255, width=sw, joint="curve")
    r = sw / 2.0
    for p in (CHEV[0], CHEV[1], CHEV[2], bar[0], bar[1]):
        cx, cy = tr(p)
        gd.ellipse((cx - r, cy - r, cx + r, cy + r), fill=255)

    # 渐变符号
    grad = glyph_gradient(S).resize((W, W), Image.BILINEAR)
    tile.paste(grad, (0, 0), gm)

    # 1px 内描边（>=40px 才有，小尺寸留干净边缘）
    if cfg["border"] > 0:
        bw = max(1, int(round(cfg["border"] * ss)))
        bd = ImageDraw.Draw(tile)
        inset = bw / 2.0
        bd.rounded_rectangle(
            (inset, inset, W - 1 - inset, W - 1 - inset),
            radius=max(1.0, cfg["radius"] * k - inset),
            outline=(49, 58, 70, cfg["border_alpha"]),
            width=bw,
        )

    return tile.resize((size, size), Image.LANCZOS)


# ---------------------------------------------------------------- ICO 打包
def bmp_entry(im):
    """32bit BMP（BGRA + 全零 AND 掩码）—— <=64px 用，兼容性最好"""
    w, h = im.size
    px = im.load()
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            xor += bytes((b, g, r, a))
    xor = bytes(xor)
    and_stride = ((w + 31) // 32) * 4
    andmask = b"\x00" * (and_stride * h)
    hdr = struct.pack(
        "<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor), 0, 0, 0, 0
    )
    return hdr + xor + andmask


def png_entry(im):
    """PNG 帧（保留：若以后需要更小的 ICO 可对 256px 启用，但部分工具链不支持）"""
    buf = io.BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path, frames):
    """frames: dict size -> RGBA image"""
    sizes = sorted(frames)
    # 全部使用 32bit BMP 条目：GDI+ / csc /win32icon / WiX 等工具链对
    # PNG 压缩条目兼容性参差（实测 System.Drawing.Icon 无法解码 PNG 帧），
    # 全部 BMP 可保证 Explorer、安装包、编译器取图标时都能正确读取。
    payloads = [(s, bmp_entry(frames[s])) for s in sizes]
    count = len(payloads)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries = b""
    blobs = b""
    for s, data in payloads:
        w = 0 if s >= 256 else s
        h = 0 if s >= 256 else s
        entries += struct.pack(
            "<BBBBHHII", w, h, 0, 0, 1, 32, len(data), offset + len(blobs)
        )
        blobs += data
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(header + entries + blobs)


# ---------------------------------------------------------------- 预览图
def load_font(path, size, index=0):
    return ImageFont.truetype(path, size, index=index)


def build_preview(frames, out_path):
    W, H = 800, 400
    img = Image.new("RGB", (W, H), (11, 13, 17))  # #0B0D11
    d = ImageDraw.Draw(img)
    fonts = "C:/Windows/Fonts/"
    try:
        f_title = load_font(fonts + "segoeuib.ttf", 21)
        f_cn_title = load_font(fonts + "msyhbd.ttc", 17)
        f_cn = load_font(fonts + "msyh.ttc", 10.5)
        f_cn_sm = load_font(fonts + "msyh.ttc", 9.5)
        f_mono = load_font(fonts + "consola.ttf", 9.5)
        f_icon_lbl = load_font(fonts + "segoeui.ttf", 9)
        f_num = load_font(fonts + "segoeuib.ttf", 10)
    except OSError as e:
        print("FONT ERROR", e)
        raise

    # 标题（按基线对齐，避免中英混排错位）
    base = 45
    d.text((24, base), "xPuTTY", font=f_title, fill=(228, 232, 239), anchor="ls")
    tw = d.textlength("xPuTTY", font=f_title)
    d.text((24 + tw + 9, base), "应用图标", font=f_cn_title, fill=(155, 165, 180), anchor="ls")
    d.text(
        (24, 59),
        "深色圆角底 + #4C8DFF → #2DD4BF 渐变 “>_” 提示符  ·  与标题栏 logo 同一几何路径",
        font=f_cn,
        fill=(140, 151, 167),
        anchor="ls",
    )
    right = "ICO  ·  9 sizes  ·  16–256 px"
    d.text(
        (W - 24, 30),
        right,
        font=f_mono,
        fill=(124, 135, 152),
        anchor="rs",
    )
    d.line([(24, 68), (W - 24, 68)], fill=(30, 36, 45), width=1)

    # ---- 左：256 主图标（实际尺寸）
    hero = frames[256]
    img.paste(hero, (24, 76), hero)
    d.text((24 + 128, 340), "256 × 256", font=f_icon_lbl, fill=(155, 165, 180), anchor="ma")
    d.text(
        (24 + 128, 354),
        "主图标 · 安装包 / 关于窗口 / 大图标视图",
        font=f_cn_sm,
        fill=(95, 106, 123),
        anchor="ma",
    )

    # ---- 右上：实际尺寸平铺（底对齐）
    d.text((304, 75), "实际尺寸（ICO 另含 20 / 40 px）", font=f_cn_sm, fill=(95, 106, 123))
    baseline = 218
    xs = {128: 304, 64: 446, 48: 518, 32: 578, 24: 620, 16: 652}
    for s in (128, 64, 48, 32, 24, 16):
        im = frames[s]
        x = xs[s]
        y = baseline - s
        img.paste(im, (x, y), im)
        d.text((x + s // 2, baseline + 4), str(s), font=f_icon_lbl, fill=(124, 135, 152), anchor="ma")

    # ---- 右下：最近邻放大，检查像素对齐
    d.text((304, 238), "像素级检查 · 最近邻放大", font=f_cn_sm, fill=(95, 106, 123))
    zoom = [(16, 6, 304), (24, 4, 416), (32, 3, 528), (48, 2, 640)]
    for s, z, x in zoom:
        big = frames[s].resize((s * z, s * z), Image.NEAREST)
        img.paste(big, (x, 250))
        d.text(
            (x, 250 + s * z + 4),
            "%d px  ×%d" % (s, z),
            font=f_icon_lbl,
            fill=(124, 135, 152),
        )

    # ---- 底部：品牌色 + 版权
    d.line([(24, 368), (W - 24, 368)], fill=(30, 36, 45), width=1)
    d.text((24, 380), "品牌色", font=f_cn_sm, fill=(95, 106, 123), anchor="ls")
    chips = [((18, 21, 26), "#12151A"), ((76, 141, 255), "#4C8DFF"), ((45, 212, 191), "#2DD4BF")]
    x = 66
    for rgb, label in chips:
        d.rounded_rectangle((x, 374, x + 10, 386), radius=3, fill=rgb, outline=(46, 55, 66))
        d.text((x + 14, 386), label, font=f_mono, fill=(140, 151, 167), anchor="ls")
        x += 14 + d.textlength(label, font=f_mono) + 16
    d.text(
        (W - 24, 386),
        "原创矢量绘制 · 16–256 px · shell/assets/xputty.ico",
        font=f_cn_sm,
        fill=(95, 106, 123),
        anchor="rs",
    )

    img.save(out_path, "PNG", optimize=True)
    return img


# ---------------------------------------------------------------- main
def main():
    os.makedirs(FRAME_DIR, exist_ok=True)
    frames = {}
    for s in ICO_SIZES:
        im = render_icon(s)
        frames[s] = im
        im.save(os.path.join(FRAME_DIR, "size-%03d.png" % s))
        print("rendered %3dpx" % s)

    write_ico(ICO_PATH, frames)
    print("ICO ->", ICO_PATH, os.path.getsize(ICO_PATH), "bytes")

    build_preview(frames, PREVIEW_PATH)
    print("preview ->", PREVIEW_PATH)

    # 自检：重新读取 ICO 帧
    with Image.open(ICO_PATH) as ico:
        sizes = sorted(ico.ico.sizes())
        print("ICO entries:", sizes)
        for s in sizes:
            ico.size = s
            ico.load()
        print("ICO reload OK, largest:", ico.size)


if __name__ == "__main__":
    main()





