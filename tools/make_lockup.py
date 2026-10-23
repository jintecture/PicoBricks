# Combines the rendered brick (logo_brick_front.png) with the name (logo_hello_earth.png) into the lockup used at the
# start of the app, and redraws the app icon with the brick.  Run after render_logo_brick.py.
import os
from PIL import Image

B = os.path.join(os.path.dirname(__file__), "..", "Assets", "Branding")
brick = Image.open(os.path.join(B, "logo_brick_front.png")).convert("RGBA")
name = Image.open(os.path.join(B, "logo_hello_earth.png")).convert("RGBA")
name = name.crop(name.getbbox())

# Lockup: brick about 2.6x the height of the name, vertically centred, gap = half the name height
h = int(name.height * 2.6)
brick_s = brick.resize((round(brick.width * h / brick.height), h), Image.LANCZOS)
gap = name.height // 2
lock = Image.new("RGBA", (brick_s.width + gap + name.width, h))
lock.paste(brick_s, (0, 0))
lock.paste(name, (brick_s.width + gap, (h - name.height) // 2), name)
lock.save(os.path.join(B, "logo_lockup.png"))

# App icon: brick on the chosen theme colour #7C7367 (white brick reads better on the middle tone than on #CCC7C1)
S = 1024
icon = Image.new("RGBA", (S, S), (124, 115, 103, 255))
w = int(S * 0.74)
b = brick.resize((w, round(brick.height * w / brick.width)), Image.LANCZOS)
icon.alpha_composite(b, ((S - b.width) // 2, (S - b.height) // 2))
icon.convert("RGB").save(os.path.join(B, "app_icon.png"))
print("lockup", lock.size, "icon", icon.size)
