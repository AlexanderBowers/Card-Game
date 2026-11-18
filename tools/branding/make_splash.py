# Builds the boot splash (assets/branding/boot_splash.png) from the Cloudy Day Games logo.
#   python tools/branding/make_splash.py assets/branding/cloudy_day_games_logo.png assets/branding/boot_splash.png [preview.png]
# Input: the logo as dark ink on white. Output: transparent 1600x1600 PNG with the logo centred at
# 60% width - cloud = solid white fill + light grey outline, lettering = white. The blue behind it
# is application/boot_splash/bg_color in project.godot (keep BLUE below in step with it).
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

BLUE = (58, 123, 213)        # #3A7BD5, preview only - the real background comes from project.godot
OUTLINE = (205, 213, 222)    # light grey cloud outline
src = Image.open(sys.argv[1]).convert('RGB')
a = np.asarray(src).astype(float)
lum = a.mean(axis=2)
ink = np.clip((235 - lum) / (235 - 115), 0, 1)         # 1 = logo ink, 0 = paper (wide knee hides JPEG noise)

# Split cloud from lettering at the widest empty band of rows in the middle half.
rows = (ink > 0.5).sum(axis=1)
h = len(rows)
best, cut, run = 0, h // 2, 0
for y in range(h // 4, 3 * h // 4):
    run = run + 1 if rows[y] == 0 else 0
    if run > best:
        best, cut = run, y - run // 2
cloud_ink = ink.copy(); cloud_ink[cut:] = 0
text_ink = ink.copy(); text_ink[:cut] = 0

# Cloud interior: flood-fill the paper region from the cloud's centre.
ys, xs = np.nonzero(cloud_ink > 0.5)
cy, cx = int((ys.min() + ys.max()) / 2), int((xs.min() + xs.max()) / 2)
mask = Image.fromarray(((cloud_ink > 0.5) * 255).astype(np.uint8)).copy()
ImageDraw.floodfill(mask, (cx, cy), 128)
interior = (np.asarray(mask) == 128).astype(np.uint8) * 255
# grow the interior a pixel under the outline so no seam shows
interior = np.asarray(Image.fromarray(interior).filter(ImageFilter.MaxFilter(3))).astype(float) / 255

H, W = ink.shape
rgb = interior[..., None] * 255.0
alpha = interior.copy()
o = cloud_ink[..., None]
rgb = rgb * (1 - o) + np.array(OUTLINE) * o
alpha = np.maximum(alpha, cloud_ink)
t = text_ink[..., None]
rgb = rgb * (1 - t) + 255 * t
alpha = np.maximum(alpha, text_ink)

img = Image.fromarray(np.dstack([rgb.clip(0, 255), alpha[..., None] * 255]).astype(np.uint8), 'RGBA')
img = img.crop(img.getbbox())
S = 1600; lw = 960; lh = round(img.height * lw / img.width)
img = img.resize((lw, lh), Image.LANCZOS)
canvas = Image.new('RGBA', (S, S), (255, 255, 255, 0))
canvas.paste(img, ((S - lw) // 2, (S - lh) // 2))
canvas.save(sys.argv[2], optimize=True)
if len(sys.argv) > 3:
    bg = Image.new('RGB', (S, S), BLUE); bg.paste(canvas, (0, 0), canvas)
    bg.resize((800, 800), Image.LANCZOS).save(sys.argv[3])
print('ok', canvas.size, 'split row', cut)
