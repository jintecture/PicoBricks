#! python3
# Builds the brick pickup tray for the PicoBricks robot setup and exports an STL for printing.
# Run in Rhino 8:  RunPythonScript  (or headless: Rhino.exe /nosplash /runscript="_-RunPythonScript make_tray.py _-Exit")
# All sizes in millimetres. Change the numbers below and run again.
#
# Minimal design: a thin flat frame with open windows where the bricks stand directly on the table,
# and small L-shaped corner pieces that guide each brick into the same spot.

import os
import struct
import Rhino
import Rhino.Geometry as rg
from Rhino.FileIO import File3dm
from Rhino.DocObjects import DimensionStyle

HERE = os.path.dirname(os.path.abspath(__file__))
NAME = "pickup_tray_bricks_1-6"
LOG_PATH = os.path.join(HERE, "make_tray_log.txt")
TOL = 0.01

# --- Brick (lying flat): 50 wide (x) x 100 long (y) x 50 high ---
BRICK_W, BRICK_L = 50.0, 100.0
CLEARANCE = 1.0           # play per side (pocket 52 x 102 mm). Timber varies and prints come out
                          # slightly tight; the soft gripper tolerates the resulting +-1 mm easily
TEST_CLEARANCES = [0.5, 1.0, 1.5]   # one small test frame per value, to try real bricks before printing the tray

# --- Layout: 3 columns x 2 rows = 6 bricks ---
COLS, ROWS = 3, 2
GAP = 28.0                # space between bricks (room for gripper fingers)
MARGIN = 10.0             # frame beyond the pockets (also holds the front-row numbers)

# --- Frame and corner pieces ---
PLATE = 1.6               # frame thickness
CORNER_WALL = 4.0         # thickness of the L-shaped corner pieces
CORNER_HEIGHT = 12.0      # height of the corner pieces above the table
LEAD_IN = 3.0             # 45 degree chamfer on the inside top edge, guides the brick in
LEG_X = 15.0              # corner leg length along the short side
LEG_Y = 20.0              # corner leg length along the long side
CORNER_RELIEF_D = 3.0     # round relief in each inside corner so brick corners don't catch

# --- Markings ---
ENGRAVE = 0.6
NUMBER_HEIGHT = 7.0
CROSS_ARM, CROSS_WIDTH = 8.0, 1.0

PW = PL = 0.0  # pocket size, set by set_clearance()

log_lines = []


def set_clearance(clearance):
    global PW, PL
    PW = BRICK_W + 2 * clearance
    PL = BRICK_L + 2 * clearance


def log(msg):
    log_lines.append(str(msg))


def outward(brep):
    """Booleans need closed solids with normals pointing out; lofts and offsets sometimes point in."""
    if brep is not None and brep.SolidOrientation == rg.BrepSolidOrientation.Inward:
        brep.Flip()
    return brep


def box(x0, y0, z0, x1, y1, z1):
    return rg.Box(rg.BoundingBox(rg.Point3d(min(x0, x1), min(y0, y1), z0),
                                 rg.Point3d(max(x0, x1), max(y0, y1), z1))).ToBrep()


def rect(cx, cy, z, w, l):
    pts = [rg.Point3d(cx - w / 2, cy - l / 2, z), rg.Point3d(cx + w / 2, cy - l / 2, z),
           rg.Point3d(cx + w / 2, cy + l / 2, z), rg.Point3d(cx - w / 2, cy + l / 2, z),
           rg.Point3d(cx - w / 2, cy - l / 2, z)]
    return rg.PolylineCurve(pts)


def frustum(cx, cy, z0, w0, l0, z1, w1, l1):
    loft = rg.Brep.CreateFromLoft([rect(cx, cy, z0, w0, l0), rect(cx, cy, z1, w1, l1)],
                                  rg.Point3d.Unset, rg.Point3d.Unset, rg.LoftType.Straight, False)[0]
    return loft.CapPlanarHoles(TOL)


def corner_pieces(cx, cy):
    """Four L-shaped corner pieces just outside the pocket outline."""
    parts = []
    for sx in (-1, 1):
        for sy in (-1, 1):
            ex, ey = cx + sx * PW / 2, cy + sy * PL / 2          # pocket corner
            ox, oy = ex + sx * CORNER_WALL, ey + sy * CORNER_WALL  # outer corner of the L
            # leg along x (short side of the brick)
            parts.append(box(ex - sx * LEG_X, ey, 0, ox, oy, CORNER_HEIGHT))
            # leg along y (long side of the brick)
            parts.append(box(ex, ey - sy * LEG_Y, 0, ox, oy, CORNER_HEIGHT))
    return parts


def pocket_cutters(cx, cy):
    cutters = []
    # window through the frame: the brick stands on the table, not on plastic
    cutters.append(box(cx - PW / 2, cy - PL / 2, -1, cx + PW / 2, cy + PL / 2, PLATE + 1))
    # 45 degree lead-in on the corner pieces, continued 1 mm above the top so it cuts cleanly
    z0 = CORNER_HEIGHT - LEAD_IN
    grow = LEAD_IN + 1.0
    cutters.append(frustum(cx, cy, z0, PW, PL, CORNER_HEIGHT + 1, PW + 2 * grow, PL + 2 * grow))
    cutters.append(box(cx - PW / 2, cy - PL / 2, -1, cx + PW / 2, cy + PL / 2, z0 + 0.01))
    # inside-corner reliefs
    for sx in (-1, 1):
        for sy in (-1, 1):
            c = rg.Circle(rg.Plane(rg.Point3d(cx + sx * PW / 2, cy + sy * PL / 2, -1), rg.Vector3d.ZAxis),
                          CORNER_RELIEF_D / 2)
            cutters.append(rg.Cylinder(c, CORNER_HEIGHT + 2).ToBrep(True, True))
    return cutters


def text_cutters(text, cx, cy, height):
    """Letters engraved into the top of the frame, centred on (cx, cy)."""
    style = DimensionStyle()
    style.TextHeight = height
    z = PLATE
    plane = rg.Plane(rg.Point3d(0, 0, z + 0.5), rg.Vector3d.ZAxis)
    te = rg.TextEntity.Create(text, plane, style, False, 0, 0)
    curves = te.CreateCurves(style, False)
    # centre the letters on (cx, cy) by their actual outline (text justification is unreliable here)
    bb = rg.BoundingBox.Empty
    for crv in curves:
        bb.Union(crv.GetBoundingBox(True))
    shift = rg.Transform.Translation(cx - bb.Center.X, cy - bb.Center.Y, 0)
    for crv in curves:
        crv.Transform(shift)
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
    z0, z1 = PLATE - ENGRAVE, PLATE + 1
    return [box(cx - CROSS_ARM / 2, cy - CROSS_WIDTH / 2, z0, cx + CROSS_ARM / 2, cy + CROSS_WIDTH / 2, z1),
            box(cx - CROSS_WIDTH / 2, cy - CROSS_ARM / 2, z0, cx + CROSS_WIDTH / 2, cy + CROSS_ARM / 2, z1)]


def build_tray():
    width = 2 * MARGIN + COLS * PW + (COLS - 1) * GAP
    depth = 2 * MARGIN + ROWS * PL + (ROWS - 1) * GAP

    solids = [box(0, 0, 0, width, depth, PLATE)]
    cutters = []
    centres = []
    n = 1
    for r in range(ROWS):          # row 0 = front (y = 0 side)
        for c in range(COLS):      # column 0 = left
            cx = MARGIN + PW / 2 + c * (PW + GAP)
            cy = MARGIN + PL / 2 + r * (PL + GAP)
            centres.append((n, cx, cy))
            solids += corner_pieces(cx, cy)
            cutters += pocket_cutters(cx, cy)
            # number on the frame just in front of its pocket
            ny = cy - PL / 2 - (MARGIN / 2 if r == 0 else GAP / 2)
            try:
                cutters += text_cutters(str(n), cx, ny, NUMBER_HEIGHT)
            except Exception as e:
                log("  number %d not engraved: %s" % (n, e))
            n += 1

    gx1 = MARGIN + PW + GAP / 2                  # between columns 1 and 2
    gx2 = MARGIN + 2 * PW + GAP + GAP / 2        # between columns 2 and 3
    gy = MARGIN + PL + GAP / 2                   # between the rows
    back = depth - MARGIN / 2                    # back edge strip
    cross_points = [(gx1, gy), (gx2, gy), (gx1, back)]
    for p in cross_points:
        cutters += cross_cutters(*p)

    body = rg.Brep.CreateBooleanUnion([outward(s) for s in solids], TOL)
    if not body:
        raise RuntimeError("union of frame and corner pieces failed")
    log("  frame + %d corner legs merged into %d solid(s)" % (len(solids) - 1, len(body)))

    cutters = [outward(c) for c in cutters if c is not None and c.IsSolid]
    merged = rg.Brep.CreateBooleanUnion(cutters, TOL)
    if merged:
        cutters = [outward(m) for m in merged]

    result = rg.Brep.CreateBooleanDifference(list(body), cutters, TOL)
    if not result:
        log("  one-step boolean failed, cutting one by one")
        result = list(body)
        for k, cut in enumerate(cutters):
            r2 = rg.Brep.CreateBooleanDifference(result, [cut], TOL)
            if r2:
                result = list(r2)
            else:
                log("  cutter %d failed (skipped)" % k)
    return list(result), width, depth, centres, cross_points


def subtract(bodies, cutters):
    cutters = [outward(c) for c in cutters if c is not None and c.IsSolid]
    merged = rg.Brep.CreateBooleanUnion(cutters, TOL)
    if merged:
        cutters = [outward(m) for m in merged]
    result = rg.Brep.CreateBooleanDifference(list(bodies), cutters, TOL)
    if not result:
        raise RuntimeError("boolean difference failed")
    return list(result)


def build_test_frame(clearance, x_offset):
    """Small frame for one brick, holding it at two diagonal corners, labelled with its clearance."""
    set_clearance(clearance)
    ring = CORNER_WALL + 6.0
    w, l = PW + 2 * ring, PL + 2 * ring
    cx, cy = x_offset + w / 2, l / 2
    solids = [box(x_offset, 0, 0, x_offset + w, l, PLATE)]
    # two diagonal corners locate the brick in both directions
    for part in corner_pieces(cx, cy)[0:2] + corner_pieces(cx, cy)[6:8]:
        solids.append(part)
    body = rg.Brep.CreateBooleanUnion([outward(s) for s in solids], TOL)
    cutters = pocket_cutters(cx, cy)
    cutters += text_cutters("%.1f" % clearance, cx, ring / 2 + 1.0, 6.0)
    return subtract(body, cutters), w, l


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
    # Test frames first: print these, try real bricks, then pick CLEARANCE
    tests, x = [], 0.0
    for c in TEST_CLEARANCES:
        frame, w, l = build_test_frame(c, x)
        tests += frame
        x += w + 8.0
    write_stl(tests, os.path.join(HERE, "test_fit_clearance_0.5-1.0-1.5.stl"))
    vol = sum(b.GetVolume() for b in tests)
    log("Test frames (clearance %s mm): %.0f x %.0f mm in total, %.0f cm3 (~%.0f g PLA if solid)"
        % (" / ".join("%.1f" % c for c in TEST_CLEARANCES), x - 8.0, l, vol / 1000.0, vol / 1000.0 * 1.24))

    set_clearance(CLEARANCE)
    log("Tray: clearance %.1f mm per side -> pocket %.1f x %.1f mm, frame %.1f mm, corner pieces %.1f mm high"
        % (CLEARANCE, PW, PL, PLATE, CORNER_HEIGHT))
    breps, w, d, centres, cross_points = build_tray()
    tris = write_stl(breps, os.path.join(HERE, NAME + ".stl"))
    vol = sum(b.GetVolume() for b in breps)
    log("%s: %.1f x %.1f x %.1f mm, %d solid(s), closed=%s, volume %.0f cm3 (~%.0f g PLA if solid), %d triangles"
        % (NAME, w, d, CORNER_HEIGHT, len(breps), all(b.IsSolid for b in breps), vol / 1000.0,
           vol / 1000.0 * 1.24, tris))
    log("Pocket centres (brick number: x, y from the tray's front-left corner, mm):")
    for n, cx, cy in centres:
        log("  %d: %.1f, %.1f" % (n, cx, cy))
    for k, (px, py) in enumerate(cross_points):
        log("Calibration cross %d: %.1f, %.1f (top of frame, z = %.1f)" % (k + 1, px, py, PLATE))

    model = File3dm()
    model.Settings.ModelUnitSystem = Rhino.UnitSystem.Millimeters
    for b in breps:
        model.Objects.AddBrep(b)
    model.Write(os.path.join(HERE, NAME + ".3dm"), 8)
    log("Done.")


try:
    main()
except Exception as e:
    import traceback
    log("ERROR: %s" % e)
    log(traceback.format_exc())
finally:
    with open(LOG_PATH, "w") as f:
        f.write("\n".join(log_lines))
