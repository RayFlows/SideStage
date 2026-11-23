"""
SideStage 应用图标的绘制源稿。

在 64 单位的方形画布上定义两块错位的屏幕，输出透明 PNG、Windows 多尺寸 ICO
和一张深浅底对照图。运行本文件即可在同目录重新生成素材，需要 Python 和 Pillow。
"""

from pathlib import Path

from PIL import Image, ImageDraw


OUTPUT = Path(__file__).resolve().parent
ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
INK = "#163F50"
BLUE = "#286579"
LIGHT_BLUE = "#36778B"
BRONZE = "#A67537"
GOLD = "#DFB66E"


def draw_icon(size: int) -> Image.Image:
    """按输出尺寸绘制透明图标。小尺寸加宽两屏间隙，避免相邻边缘缩成一个色块。"""
    scale = max(4, 256 // size) * size / 64
    edge = round(64 * scale)
    canvas = Image.new("RGBA", (edge, edge))
    pen = ImageDraw.Draw(canvas)

    def rounded(box, radius, color):
        """把设计坐标换算到采样画布，使用右缘与下缘不包含端点的矩形约定。"""
        left, top, right, bottom = (round(value * scale) for value in box)
        pen.rounded_rectangle(
            (left, top, right - 1, bottom - 1), radius=round(radius * scale), fill=color
        )

    def polygon(points, color):
        """在同一坐标系内填充多边形，用于屏幕支架和面板上的斜切色块。"""
        pen.polygon([(round(x * scale), round(y * scale)) for x, y in points], fill=color)

    # 分身在右后方。留白使用透明像素，在深、浅任务栏上都能露出实际背景。
    rounded((32, 23, 62, 48), 4, BRONZE)
    rounded((34.5, 25.5, 59.5, 44.5), 2, GOLD)
    gap = 3.5 if size <= 24 else 2
    rounded((3 - gap, 13 - gap, 44 + gap, 42 + gap), 4 + gap, (0, 0, 0, 0))

    # 一座略带斜度的短支架确立「主屏」的轮廓，与右侧较轻的副屏区分。
    polygon([(21, 40), (26, 40), (27.5, 49.5), (19.5, 49.5)], INK)
    rounded((14, 48.5, 33, 52), 1.75, INK)
    rounded((3, 13, 44, 42), 4, INK)
    rounded((6, 16, 41, 38), 1.5, BLUE)
    if size >= 32:
        polygon([(8, 16), (22.5, 16), (13.5, 38), (6, 38), (6, 21)], LIGHT_BLUE)

    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    """生成各尺寸独立栅格化的 ICO，并在两种底色下按实际像素展示三个代表尺寸。"""
    draw_icon(1024).save(OUTPUT / "SideStage.png")
    frames = {size: draw_icon(size) for size in ICON_SIZES}
    frames[256].save(
        OUTPUT / "SideStage.ico",
        sizes=[(size, size) for size in ICON_SIZES],
        append_images=[frame for size, frame in frames.items() if size != 256],
    )

    proof = Image.new("RGB", (500, 160), "#F3EEE4")
    ImageDraw.Draw(proof).rectangle((250, 0, 499, 159), fill="#272B30")
    for left in (0, 250):
        for size, center in ((128, 80), (32, 174), (16, 216)):
            frame = frames[size]
            proof.paste(frame, (left + center - size // 2, (160 - size) // 2), frame)
    proof.save(OUTPUT / "SideStage-preview.png")


if __name__ == "__main__":
    main()
