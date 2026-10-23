# Rhino 8 script: models the logo brick (a three-legged triangular block with an arch through every side and a
# triangular opening on top, like the pavilion piece) and renders it straight from the front, slightly from above,
# in the soft white Arctic look, with a transparent background.
# Run headless:  Rhino.exe /nosplash /runscript="_-RunPythonScript <this file> _-Exit"
import math
import Rhino
import Rhino.Geometry as G
import scriptcontext as sc
import System

OUT_DIR = r"C:\Users\Jin\PicoBricks\Assets\Branding"
SIDE, HEIGHT = 100.0, 62.0          # mm: triangle side, block height
ARCH_R, ARCH_SPRING = 19.0, 22.0    # arch radius, height where the round part starts
TOP_HOLE = 0.32                     # top opening: this fraction of the triangle
TOL = sc.doc.ModelAbsoluteTolerance


def tri(side, z0, z1, scale=1.0):
    """Extruded equilateral triangle centred on the origin, one side facing -Y (the front)."""
    r = side / math.sqrt(3) * scale
    pts = [G.Point3d(r * math.cos(math.radians(a)), r * math.sin(math.radians(a)), z0) for a in (90, 210, 330)]
    pts.append(pts[0])
    crv = G.PolylineCurve(pts)
    return G.Extrusion.Create(crv, z1 - z0, True).ToBrep()


def arch_cutter(angle_deg):
    """U-shaped tunnel (box + half cylinder) through the block, perpendicular to one side."""
    length = SIDE * 2
    box = G.Box(G.Plane.WorldXY, G.Interval(-ARCH_R, ARCH_R), G.Interval(-length / 2, length / 2),
                G.Interval(-1, ARCH_SPRING)).ToBrep()
    cyl_plane = G.Plane(G.Point3d(0, -length / 2, ARCH_SPRING), G.Vector3d.YAxis)
    cyl = G.Cylinder(G.Circle(cyl_plane, ARCH_R), length).ToBrep(True, True)
    u = G.Brep.CreateBooleanUnion([box, cyl], TOL)[0]
    u.Rotate(math.radians(angle_deg), G.Vector3d.ZAxis, G.Point3d.Origin)
    return u


def fix(b):
    if b.SolidOrientation == G.BrepSolidOrientation.Inward:
        b.Flip()
    return b


block = fix(tri(SIDE, 0, HEIGHT))
# Arches: one through each side (the sides face -90, 30 and 150 degrees)
cutters = [fix(arch_cutter(a)) for a in (0, 120, 240)]
cutters.append(fix(tri(SIDE, HEIGHT - 26, HEIGHT + 5, TOP_HOLE)))      # triangular opening on top
cut = G.Brep.CreateBooleanUnion(cutters, TOL)
result = G.Brep.CreateBooleanDifference([block], cut, TOL)
brep = result[0] if result else block
G.Brep.CreateFilletEdges  # (kept simple: sharp edges read well in the Arctic look)
sc.doc.Objects.AddBrep(brep)
sc.doc.Views.Redraw()

# Camera: straight from the front (-Y), a little above, perspective like the reference picture
view = sc.doc.Views.Find("Perspective", False) or sc.doc.Views.ActiveView
vp = view.ActiveViewport
mode = Rhino.Display.DisplayModeDescription.FindByName("Arctic")
if mode:
    vp.DisplayMode = mode
target = G.Point3d(0, 0, HEIGHT * 0.42)
cam = G.Point3d(0, -330, HEIGHT * 0.42 + 120)
vp.SetCameraLocations(target, cam)
vp.Camera35mmLensLength = 70
view.Redraw()
bb = brep.GetBoundingBox(True)
vp.ZoomBoundingBox(bb)
vp.Magnify(0.82, False)
view.Redraw()

cap = Rhino.Display.ViewCapture()
cap.Width, cap.Height = 1600, 1600
cap.ScaleScreenItems = False
cap.DrawAxes = False
cap.DrawGrid = False
cap.DrawGridAxes = False
cap.TransparentBackground = True
bmp = cap.CaptureToBitmap(view)
bmp.Save(OUT_DIR + r"\logo_brick_front.png", System.Drawing.Imaging.ImageFormat.Png)
sc.doc.WriteFile(OUT_DIR + r"\logo_brick.3dm", Rhino.FileIO.FileWriteOptions())
print("logo brick rendered")
