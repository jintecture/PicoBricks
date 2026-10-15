#! python3
# Opens pickup_tray.3dm and saves preview images (used to check the generated model).
import os
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc
import System

HERE = os.path.dirname(os.path.abspath(__file__))
model = Rhino.FileIO.File3dm.Read(os.path.join(HERE, "pickup_tray.3dm"))
doc = sc.doc
bbox = rg.BoundingBox.Empty
for obj in model.Objects:
    doc.Objects.Add(obj.Geometry)
    bbox.Union(obj.Geometry.GetBoundingBox(True))

view = doc.Views.ActiveView
vp = view.ActiveViewport
for dm in Rhino.Display.DisplayModeDescription.GetDisplayModes():
    if dm.EnglishName == "Shaded":
        vp.DisplayMode = dm


def save(name):
    view.Redraw()
    view.CaptureToBitmap(System.Drawing.Size(1600, 1000)).Save(os.path.join(HERE, name))


vp.SetProjection(Rhino.Display.DefinedViewportProjection.Perspective, None, True)
vp.ZoomExtents()
save("preview_perspective.png")

vp.SetProjection(Rhino.Display.DefinedViewportProjection.Top, None, True)
vp.ZoomExtents()
save("preview_top.png")

# Close-up of the spot where pockets 1, 2, 4 and 5 meet (the slopes form ridges there)
vp.SetProjection(Rhino.Display.DefinedViewportProjection.Perspective, None, True)
corner = rg.Point3d(bbox.Min.X + 75.0, bbox.Min.Y + 125.0, 8.0)
vp.SetCameraLocations(corner, corner + rg.Vector3d(-60, -80, 70))
save("preview_corner.png")

doc.Modified = False
