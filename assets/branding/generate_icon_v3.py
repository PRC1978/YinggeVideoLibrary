"""Render the Yingge line-field mark to PNG and a multi-size Windows ICO."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
SIZE = 1024
image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((48, 48, 976, 976), radius=218, fill="#153FB2")

# A sparse vertical field becomes a clear play mark at small taskbar sizes.
# Four discrete stroke widths refer to the quantized-line-field visual idea.
for x in range(232, 790, 21):
    half_height = max(0, 1 - abs(x - 470) / 310)
    top, bottom = 214, 810
    width = (4, 7, 11, 17)[min(3, max(0, int(half_height * 4)))]
    for y in range(top, bottom, 31):
        center = 505
        inside = x >= 277 and x <= 745 and abs(y - center) < (760 - x) * 0.64
        if not inside:
            continue
        color = "#D8EDFF" if x < 540 else "#FFFFFF"
        draw.rounded_rectangle((x, y, x + width, min(y + 24, bottom)), radius=width // 2, fill=color)

# Film-frame corner marks remain visible in 24 and 32 pixel renderings.
for left, top, right, bottom in (
    (192, 194, 299, 218), (192, 194, 216, 303),
    (192, 798, 299, 822), (192, 713, 216, 822),
    (725, 194, 832, 218), (808, 194, 832, 303),
    (725, 798, 832, 822), (808, 713, 832, 822),
):
    draw.rounded_rectangle((left, top, right, bottom), radius=9, fill="#8EC8FF")

png_path = ROOT / "yingge-icon-v3.png"
ico_path = ROOT / "yingge-icon-v3.ico"
image.save(png_path)
image.save(ico_path, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48),
                                          (64, 64), (128, 128), (256, 256)])
image.resize((128, 128), Image.Resampling.LANCZOS).save(ROOT / "yingge-icon-v3-preview.png")
print(png_path)
print(ico_path)
