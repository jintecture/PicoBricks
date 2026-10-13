#! python3
# Opens pickup_tray.3dm and saves preview images (used to check the generated model).
import os
import Rhino
import scriptcontext as sc
import System

HERE = os.path.dirname(os.path.abspath(__file__))
model = Rhino.FileIO.File3dm.Read(os.path.join(HERE, "pickup_tray.3dm"))
doc = sc.doc
for obj in model.Objects:
    doc.Objects.Add(obj.Geometry)

shots = [("preview_perspective.png", Rhino.Display.DefinedViewportProjection.Perspective, "Shaded"),
         ("preview_top.png", Rhino.Display.DefinedViewportProjection.Top, "Shaded")]
view = doc.Views.ActiveView
for name, projection, mode in shots:
    view.ActiveViewport.SetProjection(projection, None, True)
    for dm in Rhino.Display.DisplayModeDescription.GetDisplayModes():
        if dm.EnglishName == mode:
            view.ActiveViewport.DisplayMode = dm
    view.ActiveViewport.ZoomExtents()
    view.Redraw()
    bmp = view.CaptureToBitmap(System.Drawing.Size(1600, 1000))
    bmp.Save(os.path.join(HERE, name))

doc.Modified = False
