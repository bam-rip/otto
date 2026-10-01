# Draws Otto's icons. Run: python assets/make_icons.py  (needs Pillow)
#   otto.ico      app icon: dark tile, glowing blue ring (the "O"), small cursor dot
#   otto-tray.ico tray icon: plain white ring + dot, readable at 16 px on the dark taskbar
from PIL import Image, ImageDraw, ImageFilter
import os

HERE = os.path.dirname(os.path.abspath(__file__))
S = 1024  # draw big, scale down for smooth edges
BLUE = (0, 150, 255)
CYAN = (110, 215, 255)

def ring(draw, cx, cy, r, w, fill):
    draw.ellipse([cx - r, cy - r, cx + r, cy + r], outline=fill, width=w)

def app_icon():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # tile
    tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(tile).rounded_rectangle([40, 40, S - 40, S - 40], radius=200, fill=(26, 26, 30, 255))
    img.alpha_composite(tile)
    # glow behind the ring
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ring(ImageDraw.Draw(glow), S // 2, S // 2, 300, 120, BLUE + (200,))
    glow = glow.filter(ImageFilter.GaussianBlur(60))
    img.alpha_composite(glow)
    # the ring itself, bright edge on a blue body
    d = ImageDraw.Draw(img)
    ring(d, S // 2, S // 2, 300, 104, BLUE + (255,))
    ring(d, S // 2, S // 2, 300, 36, CYAN + (255,))
    # cursor dot, top right of the ring
    d.ellipse([700, 190, 820, 310], fill=(255, 255, 255, 255))
    return img

def tray_icon():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    ring(d, S // 2, S // 2 + 30, 340, 150, (255, 255, 255, 255))
    d.ellipse([740, 60, 940, 260], fill=(255, 255, 255, 255))
    return img

sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
app_icon().resize((256, 256), Image.LANCZOS).save(os.path.join(HERE, "otto.ico"), sizes=sizes)
tray_icon().resize((256, 256), Image.LANCZOS).save(os.path.join(HERE, "otto-tray.ico"), sizes=sizes[:6])
app_icon().resize((256, 256), Image.LANCZOS).save(os.path.join(HERE, "otto.png"))
print("ok")
