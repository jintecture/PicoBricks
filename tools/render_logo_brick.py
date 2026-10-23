# Renders the logo brick without Rhino: a three-legged triangular block with an arch through every side and a
# triangular opening on top (same shape as make_logo_brick.py), ray-marched in numpy, seen from the front and a little
# above, soft white "Arctic" look, transparent background.
# Run:  C:\RoboDK\Python-Embedded\python.exe tools\render_logo_brick.py
import math, os
import numpy as np
from PIL import Image

OUT = os.path.join(os.path.dirname(__file__), "..", "Assets", "Branding", "logo_brick_front.png")
SIZE = 1200
SIDE, HEIGHT = 100.0, 62.0
ARCH_R, ARCH_SPRING = 19.0, 22.0
TOP_HOLE, TOP_DEPTH = 0.32, 26.0
CAM, TARGET, LENS = np.array([0.0, -330.0, 110.0]), np.array([0.0, 0.0, 26.0]), 70.0

INR = SIDE / (2 * math.sqrt(3))                                   # inradius of the triangle
NORMALS = [(math.cos(math.radians(a)), math.sin(math.radians(a))) for a in (-90, 30, 150)]


def tri(x, y, r):
    return np.max([nx * x + ny * y for nx, ny in NORMALS], axis=0) - r


def arch(x, y, z, angle):
    c, s = math.cos(math.radians(angle)), math.sin(math.radians(angle))
    u = c * x + s * y                                             # across the tunnel (tunnel runs along the rotated Y)
    low = np.abs(u) - ARCH_R
    round_ = np.sqrt(u * u + (z - ARCH_SPRING) ** 2) - ARCH_R
    w = -s * x + c * y                                            # along the tunnel; < 0 = towards its own side
    return np.maximum(np.where(z < ARCH_SPRING, low, round_), w)   # only from the side to the centre: corners stay


def sdf(p):
    x, y, z = p[..., 0], p[..., 1], p[..., 2]
    d = np.maximum(tri(x, y, INR), np.maximum(-z, z - HEIGHT))
    hole = np.maximum(tri(x, y, INR * TOP_HOLE), HEIGHT - TOP_DEPTH - z)
    cut = np.minimum.reduce([arch(x, y, z, a) for a in (0, 120, 240)] + [hole])
    return np.maximum(d, -cut)


def normal(p):
    e = 0.05
    n = np.stack([sdf(p + np.array(v)) - sdf(p - np.array(v)) for v in ([e, 0, 0], [0, e, 0], [0, 0, e])], -1)
    return n / np.linalg.norm(n, axis=-1, keepdims=True)


def main():
    fwd = TARGET - CAM; fwd /= np.linalg.norm(fwd)
    right = np.cross(fwd, [0, 0, 1.0]); right /= np.linalg.norm(right)
    up = np.cross(right, fwd)
    half = math.tan(math.atan(18 / LENS)) * 0.62                  # a bit tighter than the 35 mm frame
    s = np.linspace(-half, half, SIZE)
    u, v = np.meshgrid(s, -s)
    rays = fwd + u[..., None] * right + v[..., None] * up
    rays /= np.linalg.norm(rays, axis=-1, keepdims=True)

    t = np.full(u.shape, 200.0); hit = np.zeros(u.shape, bool); alive = np.ones(u.shape, bool)
    for _ in range(220):
        idx = alive & ~hit
        if not idx.any():
            break
        d = sdf(CAM + rays[idx] * t[idx][:, None])
        t[idx] += d
        h = d < 0.01
        tmp = hit[idx]; tmp[h] = True; hit[idx] = tmp
        tmp = alive[idx]; tmp[t[idx] > 700] = False; alive[idx] = tmp

    p = CAM + rays[hit] * t[hit][:, None]
    n = normal(p)
    # Ambient occlusion along the normal, plus a soft key light from above-left and a sky term (Arctic look)
    ao = np.ones(len(p))
    for i in range(1, 7):
        dist = 2.2 * i
        ao -= (dist - np.maximum(sdf(p + n * dist), 0)) / dist * 0.5 ** i
    ao = np.clip(ao, 0, 1)
    light = np.array([-0.45, -0.55, 0.70]); light /= np.linalg.norm(light)
    shadow = np.ones(len(p)); ts = np.full(len(p), 0.6)
    for _ in range(48):                                           # soft shadow march
        d = sdf(p + n * 0.05 + light * ts[:, None])
        shadow = np.minimum(shadow, 8 * d / ts)
        ts += np.clip(d, 0.4, 6)
    shadow = np.clip(shadow, 0, 1)
    diff = np.clip(n @ light, 0, 1)
    sky = 0.5 + 0.5 * n[:, 2]
    shade = 0.50 * ao * (0.55 + 0.45 * sky) + 0.50 * diff * (0.35 + 0.65 * shadow) * (0.6 + 0.4 * ao)
    shade = np.clip(shade * 1.08, 0, 1) ** 0.85
    warm = np.array([246, 243, 238]) / 255.0                      # warm white, matches #CCC7C1 family
    rgb = np.zeros(u.shape + (3,)); rgb[hit] = shade[:, None] * warm

    img = np.zeros(u.shape + (4,), np.uint8)
    img[..., :3] = (rgb * 255).astype(np.uint8); img[..., 3] = hit * 255
    im = Image.fromarray(img, "RGBA")
    im = im.crop(im.getbbox())
    pad = int(max(im.size) * 0.04)
    canvas = Image.new("RGBA", (im.width + 2 * pad, im.height + 2 * pad))
    canvas.paste(im, (pad, pad))
    canvas.save(OUT)
    print("saved", os.path.abspath(OUT), canvas.size)


main()
