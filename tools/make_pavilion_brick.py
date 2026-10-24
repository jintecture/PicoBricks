# The pavilion brick for the app (PavilionBuilder.cs), from Jin's logo mesh (logo.mesh.txt, exported from logo.3dm by
# render_logo_3dm.py; the logo IS the pavilion module of "Stacking 3d modules.gh": same 30 mm footprint).
# Unity frame: y up, centred on its three legs, one leg pointing along +z (the direction of the curve), bottom at
# y = 0, metres (1 model mm = 1 mm). Writes Assets/Models/pavilion_brick.txt ("v x y z nx ny nz" / "f a b c").
import math, os
import numpy as np

ROOT = os.path.join(os.path.dirname(__file__), "..")
v, f = [], []
for line in open(os.path.join(ROOT, "Assets", "Branding", "logo.mesh.txt")):
    p = line.split()
    if p[0] == "v":
        v.append([float(x) for x in p[1:]])
    elif p[0] == "f":
        f.append([int(x) for x in p[1:]])
v = np.array(v); pos, nrm = v[:, :3], v[:, 3:]

# Leg centres: the outermost vertices in three 120-degree sectors
xy = pos[:, :2]; c0 = xy.mean(0); d = xy - c0
ang = np.degrees(np.arctan2(d[:, 1], d[:, 0])); r = np.hypot(d[:, 0], d[:, 1])
far = r > np.percentile(r, 80)
first = -98.1                                         # one leg (measured for the logo render)
legs = []
for k in range(3):
    a = first + 120 * k
    diff = (ang - a + 180) % 360 - 180
    m = far & (np.abs(diff) < 40)
    legs.append(xy[m].mean(0))
legs = np.array(legs); centre = legs.mean(0)
leg_dir = math.degrees(math.atan2(*(legs[0] - centre)[::-1]))
rnd = [np.hypot(*(l - centre)) for l in legs]
print("leg directions:", [round(math.degrees(math.atan2(*(l - centre)[::-1])), 1) for l in legs])

# Rhino (x, y, z) -> Unity (x, z, y), turned so leg 0 points along +z, centred, bottom at 0, mm -> m
turn = math.radians(90 - leg_dir)
cs, sn = math.cos(turn), math.sin(turn)
def tf(x, y):
    x, y = x - centre[0], y - centre[1]
    return x * cs - y * sn, x * sn + y * cs
zmin = pos[:, 2].min()
out = []
for (x, y, z), (nx, ny, nz) in zip(pos, nrm):
    ux, uz = tf(x, y)
    nnx, nnz = nx * cs - ny * sn, nx * sn + ny * cs
    out.append("v %.5f %.5f %.5f %.4f %.4f %.4f" % (ux / 1000, (z - zmin) / 1000, uz / 1000, nnx, nz, nnz))
for a, b, c in f:
    out.append("f %d %d %d" % (a, c, b))              # swapping y/z mirrors, so flip the winding
dst = os.path.join(ROOT, "Assets", "Models", "pavilion_brick.txt")
open(dst, "w").write("\n".join(out) + "\n")
w = pos[:, 0].max() - pos[:, 0].min()
print("saved", dst, "| leg centre distance (mm):", [round(x, 2) for x in rnd], "| height (mm):", round(pos[:, 2].ptp(), 2))
