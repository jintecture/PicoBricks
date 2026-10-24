# Combines the rendered logo (logo_brick_front.png) with the name (logo_hello_earth.png) and the line
# "Creative Robotics 2026" underneath into the lockup used at the start of the app, and redraws the app icon.
# Also writes the three layers separately at video size (work/v_*.png) for the intro preview, so they can appear
# one after another.  Run after render_logo_mesh.py.
import os
from PIL import Image, ImageDraw, ImageFont

B = os.path.join(os.path.dirname(__file__), "..", "Assets", "Branding")
WORK = os.path.join(os.path.expanduser("~"), "Downloads", "HelloEarth_intro", "work")
SUBTITLE = "Creative Robotics 2026"
SUB_COLOR = (178, 171, 162, 255)        # #B2ABA2, lighter than the name so the small text stays readable on black
LOGO_TO_NAME = 1.9                      # logo height / name height (Jin, 24 Oct: smaller than before)

brick = Image.open(os.path.join(B, "logo_brick_front.png")).convert("RGBA")
name = Image.open(os.path.join(B, "logo_hello_earth.png")).convert("RGBA")
name = name.crop(name.getbbox())


def subtitle(height):
    """'Creative Robotics 2026' in Cascadia Mono, cropped, `height` px tall."""
    font = ImageFont.truetype(r"C:\Windows\Fonts\CascadiaMono.ttf", int(height * 1.4))
    box = font.getbbox(SUBTITLE)
    img = Image.new("RGBA", (box[2] + 4, box[3] + 4))
    ImageDraw.Draw(img).text((0, 0), SUBTITLE, font=font, fill=SUB_COLOR)
    img = img.crop(img.getbbox())
    return img.resize((round(img.width * height / img.height), height), Image.LANCZOS)


def layers(name_h):
    """Logo, name and subtitle scaled for a name `name_h` px tall, plus their positions in the lockup."""
    n = name.resize((round(name.width * name_h / name.height), name_h), Image.LANCZOS)
    s = subtitle(round(name_h * 0.42))
    bh = round(name_h * LOGO_TO_NAME)
    b = brick.resize((round(brick.width * bh / brick.height), bh), Image.LANCZOS)
    gap_x, gap_y = round(name_h * 0.55), round(name_h * 0.38)
    text_h = n.height + gap_y + s.height
    h = max(b.height, text_h)
    x_text = b.width + gap_x
    y_text = (h - text_h) // 2
    pos = {"logo": (0, (h - b.height) // 2), "name": (x_text, y_text), "sub": (x_text, y_text + n.height + gap_y)}
    w = x_text + max(n.width, s.width)
    return {"logo": b, "name": n, "sub": s}, pos, (w, h)


def lockup(name_h):
    imgs, pos, size = layers(name_h)
    out = Image.new("RGBA", size)
    for k in ("logo", "name", "sub"):
        out.alpha_composite(imgs[k], pos[k])
    return out


lockup(name.height).save(os.path.join(B, "logo_lockup.png"))

# App icon: logo on the chosen theme colour #7C7367
S = 1024
icon = Image.new("RGBA", (S, S), (124, 115, 103, 255))
w = int(S * 0.72)
b = brick.resize((w, round(brick.height * w / brick.width)), Image.LANCZOS)
icon.alpha_composite(b, ((S - b.width) // 2, (S - b.height) // 2))
icon.convert("RGB").save(os.path.join(B, "app_icon.png"))

# Video layers (1280 x 640 preview), each on a full transparent frame so they all overlay at 0,0
if os.path.isdir(WORK):
    VW, VH = 1280, 640
    imgs, pos, (w, h) = layers(64)
    ox, oy = (VW - w) // 2, (VH - h) // 2
    for k, img in imgs.items():
        frame = Image.new("RGBA", (VW, VH))
        frame.alpha_composite(img, (ox + pos[k][0], oy + pos[k][1]))
        frame.save(os.path.join(WORK, "v_%s.png" % k))
    check = Image.new("RGBA", (VW, VH), (0, 0, 0, 255))
    for k in ("logo", "name", "sub"):
        check.alpha_composite(Image.open(os.path.join(WORK, "v_%s.png" % k)))
    check.convert("RGB").save(os.path.join(WORK, "lockup_check.png"))
print("lockup, icon and video layers saved")

# Layers for the headset intro (IntroSequence.cs): the three layers on canvases of the same size, so the app can
# stack three equal quads and fade each in on its own beat; plus the choices shown under the lockup.
INTRO = os.path.join(B, "Intro")
os.makedirs(INTRO, exist_ok=True)
imgs, pos, size = layers(name.height)
for k, img in imgs.items():
    canvas = Image.new("RGBA", size)
    canvas.alpha_composite(img, pos[k])
    canvas.save(os.path.join(INTRO, "intro_%s.png" % k))


def choices():
    font = ImageFont.truetype(r"C:\Windows\Fonts\CascadiaMono.ttf", 96)
    lines = [("A", "Build a wall with the robot", (204, 199, 193, 255)),
             (None, "How we built the pavilion  (coming soon)", (124, 115, 103, 255))]
    img = Image.new("RGBA", (2600, 420))
    d = ImageDraw.Draw(img)
    y = 20
    for key, text, col in lines:
        x = 20
        if key:
            d.rounded_rectangle((x, y, x + 130, y + 130), radius=28, outline=col, width=8)
            d.text((x + 65, y + 63), key, font=font, fill=col, anchor="mm")
        d.text((x + 190, y + 63), text, font=font, fill=col, anchor="lm")
        y += 220
    return img.crop(img.getbbox())


choices().save(os.path.join(INTRO, "intro_choices.png"))
print("intro layers saved", size)
