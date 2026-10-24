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
# stack three equal quads and fade each in on its own beat; plus 'Press (A) to start' under the lockup.
INTRO = os.path.join(B, "Intro")
os.makedirs(INTRO, exist_ok=True)
imgs, pos, size = layers(name.height)
for k, img in imgs.items():
    canvas = Image.new("RGBA", size)
    canvas.alpha_composite(img, pos[k])
    canvas.save(os.path.join(INTRO, "intro_%s.png" % k))


def choices():
    """'Press (A) to start' (one option for now; the pavilion mode comes later). Two layers on the same canvas: the
    text, and the A in a circle, which blinks in the app."""
    font = ImageFont.truetype(r"C:\Windows\Fonts\CascadiaMono.ttf", 96)
    col = (204, 199, 193, 255)
    W, H, R = 1200, 170, 70
    text = Image.new("RGBA", (W, H)); button = Image.new("RGBA", (W, H))
    d = ImageDraw.Draw(text)
    x, cy = 10, H // 2
    d.text((x, cy), "Press", font=font, fill=col, anchor="lm")
    cx = x + int(d.textlength("Press ", font=font)) + R
    b = ImageDraw.Draw(button)
    b.ellipse((cx - R, cy - R, cx + R, cy + R), outline=col, width=8)
    b.text((cx, cy - 4), "A", font=font, fill=col, anchor="mm")
    d.text((cx + R + int(d.textlength(" ", font=font)), cy), "to start", font=font, fill=col, anchor="lm")
    box = text.getbbox(); bb = button.getbbox()
    box = (0, min(box[1], bb[1]), max(box[2], bb[2]), max(box[3], bb[3]))
    return text.crop(box), button.crop(box)


t, b = choices()
t.save(os.path.join(INTRO, "intro_choices.png"))
b.save(os.path.join(INTRO, "intro_button.png"))
check = Image.new("RGBA", t.size, (0, 0, 0, 255)); check.alpha_composite(t); check.alpha_composite(b)
if os.path.isdir(WORK):
    check.convert("RGB").save(os.path.join(WORK, "choices_check.png"))
print("intro layers saved", size)


# Home menu (HomeMenu.cs): one image per line of text, all rendered at the same height so they line up in the app
def line_image(text, col=(204, 199, 193, 255), size=96, button=None):
    """One line of Cascadia Mono, full line height (so every line scales the same); `button` = letter drawn in a
    circle where '()' stands in the text."""
    font = ImageFont.truetype(r"C:\Windows\Fonts\CascadiaMono.ttf", size)
    H = int(size * 1.5)
    img = Image.new("RGBA", (int(font.getlength(text)) + 40, H))
    d = ImageDraw.Draw(img)
    if button and "()" in text:
        before, after = text.split("()")
        x = 10
        d.text((x, H // 2), before, font=font, fill=col, anchor="lm")
        R = int(size * 0.55)
        cx = x + int(font.getlength(before)) + R
        d.ellipse((cx - R, H // 2 - R, cx + R, H // 2 + R), outline=col, width=max(3, size // 12))
        d.text((cx, H // 2 - 3), button, font=font, fill=col, anchor="mm")
        d.text((cx + R + int(font.getlength(" ")) // 2, H // 2), after, font=font, fill=col, anchor="lm")
    else:
        d.text((10, H // 2), text, font=font, fill=col, anchor="lm")
    box = img.getbbox()
    return img.crop((0, 0, box[2] + 10, H))


DIM = (124, 115, 103, 255)
MENU = os.path.join(B, "Menu")
os.makedirs(MENU, exist_ok=True)
for name, text in (("menu_robot", "Build a wall with the robot"),
                   ("menu_pavilion", "Design our pavilion"),
                   ("menu_settings", "Settings")):
    line_image(text).save(os.path.join(MENU, name + ".png"))
line_image("THUMBSTICK up / down  \u00b7  () = choose", DIM, button="A").save(os.path.join(MENU, "menu_hint.png"))
line_image("Table calibration", size=96).save(os.path.join(MENU, "settings_title.png"))
line_image("() = start   \u00b7   B = back", DIM, button="A").save(os.path.join(MENU, "settings_hint.png"))
print("menu images saved")
