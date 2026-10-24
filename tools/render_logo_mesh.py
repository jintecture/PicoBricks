# Renders Jin's logo (mesh exported from logo.3dm by render_logo_3dm.py) seen from the front (-Y) and a little above,
# soft white "Arctic" look with shadows, transparent background -> Assets/Branding/logo_brick_front.png.
# Run:  C:\RoboDK\Python-Embedded\python.exe tools\render_logo_mesh.py
import math, os
import numpy as np
from PIL import Image

B = os.path.join(os.path.dirname(__file__), "..", "Assets", "Branding")
SIZE, SS = 1200, 2                  # output size, supersampling
ELEVATION = 35.0                    # deg above the horizon (Jin: a 3D view, not an elevation)
FOV = 24.0                          # deg
TURN = 198.0                        # deg about the vertical: a flat side faces the viewer, turned 10 deg for depth (Jin, 24 Oct; legs at -98/22/142 deg in the model)
LIGHT = np.array([-0.5, -0.6, 0.75])  # towards the light: front-left, above


def load():
    v, f = [], []
    for line in open(os.path.join(B, "logo.mesh.txt")):
        p = line.split()
        if p[0] == "v":
            v.append([float(x) for x in p[1:]])
        elif p[0] == "f":
            f.append([int(x) for x in p[1:]])
    v = np.array(v)
    return v[:, :3], v[:, 3:], np.array(f)


def rasterize(xy, depth, faces, w, h, attrs):
    """z-buffer: returns depth buffer, triangle id and barycentrics per pixel (smaller depth = nearer)."""
    zbuf = np.full((h, w), np.inf); tri = np.full((h, w), -1); bary = np.zeros((h, w, 3))
    for i, (a, b, c) in enumerate(faces):
        p = xy[[a, b, c]]
        x0, y0 = np.floor(p.min(0)).astype(int); x1, y1 = np.ceil(p.max(0)).astype(int)
        x0, y0 = max(x0, 0), max(y0, 0); x1, y1 = min(x1, w - 1), min(y1, h - 1)
        if x1 < x0 or y1 < y0:
            continue
        X, Y = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        d = (p[1, 1] - p[2, 1]) * (p[0, 0] - p[2, 0]) + (p[2, 0] - p[1, 0]) * (p[0, 1] - p[2, 1])
        if abs(d) < 1e-12:
            continue
        l0 = ((p[1, 1] - p[2, 1]) * (X - p[2, 0]) + (p[2, 0] - p[1, 0]) * (Y - p[2, 1])) / d
        l1 = ((p[2, 1] - p[0, 1]) * (X - p[2, 0]) + (p[0, 0] - p[2, 0]) * (Y - p[2, 1])) / d
        l2 = 1 - l0 - l1
        inside = (l0 >= -1e-6) & (l1 >= -1e-6) & (l2 >= -1e-6)
        z = l0 * depth[a] + l1 * depth[b] + l2 * depth[c]
        sub = zbuf[y0:y1 + 1, x0:x1 + 1]
        win = inside & (z < sub)
        sub[win] = z[win]
        tri[y0:y1 + 1, x0:x1 + 1][win] = i
        bary[y0:y1 + 1, x0:x1 + 1][win] = np.stack([l0, l1, l2], -1)[win]
    return zbuf, tri, bary


def frame(forward):
    fwd = forward / np.linalg.norm(forward)
    right = np.cross(fwd, [0, 0, 1.0]); right /= np.linalg.norm(right)
    return fwd, right, np.cross(right, fwd)


def main():
    pos, nrm, faces = load()
    a = math.radians(TURN)
    rot = np.array([[math.cos(a), -math.sin(a), 0], [math.sin(a), math.cos(a), 0], [0, 0, 1]])
    mid = (pos.min(0) + pos.max(0)) / 2
    pos = (pos - mid) @ rot.T + mid; nrm = nrm @ rot.T
    lo, hi = pos.min(0), pos.max(0); c = (lo + hi) / 2; size = np.linalg.norm(hi - lo)
    e = math.radians(ELEVATION)
    fwd, right, up = frame(np.array([0, math.cos(e), -math.sin(e)]))
    cam = c - fwd * size * 3.2
    W = SIZE * SS
    rel = pos - cam
    zc = rel @ fwd
    f = W / 2 / math.tan(math.radians(FOV / 2))
    xy = np.stack([W / 2 + f * (rel @ right) / zc, W / 2 - f * (rel @ up) / zc], 1)
    zbuf, tri, bary = rasterize(xy, zc, faces, W, W, None)
    hit = tri >= 0

    # Shadow map: orthographic depth from the light
    L = LIGHT / np.linalg.norm(LIGHT)
    lf, lr, lu = frame(-L)
    S = 2048
    lxy_w = np.stack([pos @ lr, pos @ lu], 1)
    lmin, lmax = lxy_w.min(0), lxy_w.max(0)
    scale = (S - 8) / (lmax - lmin).max()
    lxy = (lxy_w - lmin) * scale + 4
    ldepth = pos @ lf
    lbuf, _, _ = rasterize(lxy, ldepth, faces, S, S, None)

    t = tri[hit]; bc = bary[hit]; fv = faces[t]
    P = (bc[:, :, None] * pos[fv]).sum(1)
    N = (bc[:, :, None] * nrm[fv]).sum(1); N /= np.linalg.norm(N, axis=1, keepdims=True)
    view = P - cam
    N[(N * view).sum(1) > 0] *= -1                             # face the camera
    sp = ((np.stack([P @ lr, P @ lu], 1) - lmin) * scale + 4)
    # 3x3 PCF soft shadows
    lit = np.zeros(len(P))
    for dx in (-1.5, 0, 1.5):
        for dy in (-1.5, 0, 1.5):
            ix = np.clip((sp[:, 0] + dx).astype(int), 0, S - 1); iy = np.clip((sp[:, 1] + dy).astype(int), 0, S - 1)
            lit += (P @ lf) <= lbuf[iy, ix] + size * 0.004
    lit /= 9

    diff = np.clip(N @ L, 0, 1) * lit
    sky = 0.55 + 0.45 * N[:, 2]
    shade = np.clip(0.42 * sky + 0.62 * diff, 0, 1) ** 0.8
    warm = np.array([246, 243, 238]) / 255.0
    img = np.zeros((W, W, 4))
    img[hit, :3] = shade[:, None] * warm
    img[hit, 3] = 1
    small = img.reshape(SIZE, SS, SIZE, SS, 4).mean((1, 3))     # supersampled anti-aliasing
    rgb = np.where(small[..., 3:] > 0, small[..., :3] / np.maximum(small[..., 3:], 1e-6), 0)
    out = np.concatenate([rgb, small[..., 3:]], -1)
    im = Image.fromarray((out * 255).round().astype(np.uint8), "RGBA")
    im = im.crop(im.getbbox())
    pad = int(max(im.size) * 0.03)
    canvas = Image.new("RGBA", (im.width + 2 * pad, im.height + 2 * pad)); canvas.paste(im, (pad, pad))
    canvas.save(os.path.join(B, "logo_brick_front.png"))
    print("saved logo_brick_front.png", canvas.size)


main()
