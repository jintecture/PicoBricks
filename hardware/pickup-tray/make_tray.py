#! python3
# Builds the brick pickup trays for the PicoBricks robot setup and exports STL files for printing.
# Run in Rhino 8:  RunPythonScript  (or headless: Rhino.exe /nosplash /runscript="_-RunPythonScript make_tray.py _-Exit")
# All sizes in millimetres. Change the numbers below and run again.

import os
import struct
import Rhino.Geometry as rg
from Rhino.FileIO import File3dm
from Rhino.DocObjects import DimensionStyle

HERE = os.path.dirname(os.path.abspath(__file__))
LOG_PATH = os.path.join(HERE, "make_tray_log.txt")
TOL = 0.01

# --- Brick (lying flat): 50 wide (x) x 100 long (y) x 50 high ---
BRICK_W, BRICK_L = 50.0, 100.0
CLEARANCE = 0.5           # play per side; use 1.0 if the timber varies by more than 1 mm

# --- Tray ---
FLOOR = 2.0               # pocket floor thickness
WALL = 8.0                # straight wall that holds the brick
CHAMFER = 6.0             # 45 degree lead-in above the wall, funnels the brick into place
GAP = 28.0                # material between neighbouring pockets
MARGIN = 8.0              # outer edge beyond the pockets
NOTCH_LONG = 60.0         # finger slot in the middle of each long side
NOTCH_SHORT = 25.0        # finger slot in the middle of each short side
CORNER_RELIEF_D = 4.0     # round relief at pocket corners so brick corners sit fully down
ENGRAVE = 0.6             # depth of numbers and calibration crosses
NUMBER_HEIGHT = 14.0
CROSS_ARM, CROSS_WIDTH = 10.0, 1.0

TRAYS = [
    # name, columns (x), rows (y), first brick number, calibration crosses
    ("tray_A_bricks_1-6", 3, 2, 1, True),
    ("tray_B_bricks_7-10", 2, 2, 7, False),
]

PW = BRICK_W + 2 * CLEARANCE
PL = BRICK_L + 2 * CLEARANCE
H = FLOOR + WALL + CHAMFER

log_lines = []


def log(msg):
    log_lines.append(str(msg))


def outward(brep):
    """Booleans need closed solids with normals pointing out; lofts and offsets sometimes point in."""
    if brep is not None and brep.SolidOrientation == rg.BrepSolidOrientation.Inward:
        brep.Flip()
    return brep


def box(x0, y0, z0, x1, y1, z1):
    return rg.Box(rg.BoundingBox(rg.Point3d(x0, y0, z0), rg.Point3d(x1, y1, z1))).ToBrep()


def rect(cx, cy, z, w, l):
    pts = [rg.Point3d(cx - w / 2, cy - l / 2, z), rg.Point3d(cx + w / 2, cy - l / 2, z),
           rg.Point3d(cx + w / 2, cy + l / 2, z), rg.Point3d(cx - w / 2, cy + l / 2, z),
           rg.Point3d(cx - w / 2, cy - l / 2, z)]
    return rg.PolylineCurve(pts)


def frustum(cx, cy, z0, w0, l0, z1, w1, l1):
    loft = rg.Brep.CreateFromLoft([rect(cx, cy, z0, w0, l0), rect(cx, cy, z1, w1, l1)],
                                  rg.Point3d.Unset, rg.Point3d.Unset, rg.LoftType.Straight, False)[0]
    return loft.CapPlanarHoles(TOL)


def pocket_cutters(cx, cy):
    cutters = []
    # straight-walled pocket
    cutters.append(box(cx - PW / 2, cy - PL / 2, FLOOR, cx + PW / 2, cy + PL / 2, FLOOR + WALL + 0.01))
    # 45 degree chamfer, continued 1 mm above the top so it cuts cleanly through
    z_top = H + 1.0
    grow = CHAMFER + 1.0
    cutters.append(frustum(cx, cy, FLOOR + WALL, PW, PL, z_top, PW + 2 * grow, PL + 2 * grow))
    # finger slots, down to the pocket floor; long enough to open into neighbours / the outside
    reach = GAP / 2 + 1.0
    cutters.append(box(cx - PW / 2 - reach, cy - NOTCH_LONG / 2, FLOOR, cx + PW / 2 + reach, cy + NOTCH_LONG / 2, H + 1))
    cutters.append(box(cx - NOTCH_SHORT / 2, cy - PL / 2 - reach, FLOOR, cx + NOTCH_SHORT / 2, cy + PL / 2 + reach, H + 1))
    # corner reliefs
    for sx in (-1, 1):
        for sy in (-1, 1):
            c = rg.Circle(rg.Plane(rg.Point3d(cx + sx * PW / 2, cy + sy * PL / 2, FLOOR), rg.Vector3d.ZAxis),
                          CORNER_RELIEF_D / 2)
            cutters.append(rg.Cylinder(c, WALL + 1.0).ToBrep(True, True))
    return cutters


def text_cutters(text, cx, cy, z, height):
    """Solid letters from z - ENGRAVE to z + 0.5, centred on (cx, cy)."""
    style = DimensionStyle()
    style.TextHeight = height
    plane = rg.Plane(rg.Point3d(cx, cy, z + 0.5), rg.Vector3d.ZAxis)
    te = rg.TextEntity.Create(text, plane, style, False, 0, 0)
    te.Justification = rg.TextJustification.MiddleCenter
    curves = te.CreateCurves(style, False)
    faces = rg.Brep.CreatePlanarBreps(curves, TOL) or []
    solids = []
    for f in faces:
        for face in f.Faces:
            for d in (-(ENGRAVE + 0.5), ENGRAVE + 0.5):
                s = rg.Brep.CreateFromOffsetFace(face, d, TOL, False, True)
                if s and s.GetBoundingBox(True).Min.Z < z - ENGRAVE / 2:
                    solids.append(s)
                    break
    return solids


def cross_cutters(cx, cy):
    z0, z1 = H - ENGRAVE, H + 1
    return [box(cx - CROSS_ARM / 2, cy - CROSS_WIDTH / 2, z0, cx + CROSS_ARM / 2, cy + CROSS_WIDTH / 2, z1),
            box(cx - CROSS_WIDTH / 2, cy - CROSS_ARM / 2, z0, cx + CROSS_WIDTH / 2, cy + CROSS_ARM / 2, z1)]


def build_tray(cols, rows, first_number, crosses):
    width = 2 * MARGIN + cols * PW + (cols - 1) * GAP
    depth = 2 * MARGIN + rows * PL + (rows - 1) * GAP
    tray = box(0, 0, 0, width, depth, H)

    centres = []
    cutters = []
    n = first_number
    for r in range(rows):          # row 0 = front (y = 0 side)
        for c in range(cols):      # column 0 = left
            cx = MARGIN + PW / 2 + c * (PW + GAP)
            cy = MARGIN + PL / 2 + r * (PL + GAP)
            centres.append((n, cx, cy))
            cutters += pocket_cutters(cx, cy)
            try:
                cutters += text_cutters(str(n), cx, cy, FLOOR, NUMBER_HEIGHT)
            except Exception as e:
                log("  number %d not engraved: %s" % (n, e))
            n += 1

    cross_points = []
    if crosses and cols >= 3 and rows >= 2:
        gx1 = MARGIN + PW + GAP / 2                  # between columns 1 and 2
        gx2 = MARGIN + 2 * PW + GAP + GAP / 2        # between columns 2 and 3
        gy = MARGIN + PL + GAP / 2                   # between the rows
        front = MARGIN + 12.0                        # solid corner zone near the front edge
        cross_points = [(gx1, front), (gx1, gy), (gx2, gy)]
        for p in cross_points:
            cutters += cross_cutters(*p)

    cutters = [outward(c) for c in cutters if c is not None]
    bad = [k for k, c in enumerate(cutters) if not c.IsSolid]
    if bad:
        log("  open (non-solid) cutters skipped: %s" % bad)
        cutters = [c for c in cutters if c.IsSolid]

    # Merge all cutters into one shape first; a cutter lying fully inside the tray
    # (e.g. the straight pocket box) only works as part of the merged shape.
    merged = rg.Brep.CreateBooleanUnion(cutters, TOL)
    if merged:
        log("  %d cutters merged into %d" % (len(cutters), len(merged)))
        cutters = [outward(m) for m in merged]

    result = rg.Brep.CreateBooleanDifference([tray], cutters, TOL)
    if not result:
        log("  one-step boolean failed, cutting one by one")
        result = [tray]
        for k, cut in enumerate(cutters):
            r2 = rg.Brep.CreateBooleanDifference(result, [cut], TOL)
            if r2:
                result = list(r2)
            else:
                log("  cutter %d failed (skipped)" % k)
    return list(result), width, depth, centres, cross_points


def write_stl(breps, path):
    mesh = rg.Mesh()
    for b in breps:
        for m in rg.Mesh.CreateFromBrep(b, rg.MeshingParameters.QualityRenderMesh):
            mesh.Append(m)
    mesh.Faces.ConvertQuadsToTriangles()
    mesh.Normals.ComputeNormals()
    mesh.FaceNormals.ComputeFaceNormals()
    with open(path, "wb") as f:
        f.write(b"PicoBricks pickup tray".ljust(80, b" "))
        f.write(struct.pack("<I", mesh.Faces.Count))
        for i in range(mesh.Faces.Count):
            face = mesh.Faces[i]
            nrm = mesh.FaceNormals[i]
            f.write(struct.pack("<3f", nrm.X, nrm.Y, nrm.Z))
            for vi in (face.A, face.B, face.C):
                v = mesh.Vertices[vi]
                f.write(struct.pack("<3f", v.X, v.Y, v.Z))
            f.write(b"\x00\x00")
    return mesh.Faces.Count


def main():
    model = File3dm()
    model.Settings.ModelUnitSystem = Rhino.UnitSystem.Millimeters
    x_offset = 0.0
    log("Pocket %.1f x %.1f mm, tray height %.1f mm" % (PW, PL, H))
    expected = []
    for name, cols, rows, first, crosses in TRAYS:
        w = 2 * MARGIN + cols * PW + (cols - 1) * GAP
        d = 2 * MARGIN + rows * PL + (rows - 1) * GAP
        expected.append(w * d * H / 1000.0)
    log("Solid block volumes before cutting: %s cm3" % ", ".join("%.0f" % v for v in expected))
    for name, cols, rows, first, crosses in TRAYS:
        log(name)
        breps, w, d, centres, cross_points = build_tray(cols, rows, first, crosses)
        stl = os.path.join(HERE, name + ".stl")
        tris = write_stl(breps, stl)
        vol = sum(b.GetVolume() for b in breps)
        log("  size %.1f x %.1f x %.1f mm, %d solid(s), closed=%s, volume %.0f cm3, %d triangles"
            % (w, d, H, len(breps), all(b.IsSolid for b in breps), vol / 1000.0, tris))
        log("  pocket centres (brick number: x, y from tray front-left corner, mm):")
        for n, cx, cy in centres:
            log("    %d: %.1f, %.1f" % (n, cx, cy))
        for k, (px, py) in enumerate(cross_points):
            log("  calibration cross %d: %.1f, %.1f (top surface, z = %.1f)" % (k + 1, px, py, H))
        for b in breps:
            b.Translate(rg.Vector3d(x_offset, 0, 0))
            model.Objects.AddBrep(b)
        x_offset += w + 30.0
    model.Write(os.path.join(HERE, "pickup_trays.3dm"), 8)
    log("Done.")


try:
    import Rhino
    main()
except Exception as e:
    import traceback
    log("ERROR: %s" % e)
    log(traceback.format_exc())
finally:
    with open(LOG_PATH, "w") as f:
        f.write("\n".join(log_lines))
