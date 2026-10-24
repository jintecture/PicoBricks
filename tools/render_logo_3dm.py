# Rhino 8 script: meshes Jin's logo model (logo.3dm, opened by Rhino from the command line) and writes the mesh to
# Assets/Branding/logo.mesh.txt for tools/render_logo_mesh.py, which makes the shaded picture (headless Rhino can only
# draw wireframes). Writes a log so a headless run can be checked, then closes Rhino.
#   Rhino.exe /nosplash /runscript="_-RunPythonScript <this file>" "<copy of logo.3dm>"
import Rhino, scriptcontext as sc
import Rhino.Geometry as G

MESH = r"C:\Users\Jin\PicoBricks\Assets\Branding\logo.mesh.txt"
LOG = r"C:\Users\Jin\Downloads\HelloEarth_intro\work\rhino\render_log.txt"


def log(msg):
    with open(LOG, "a") as f:
        f.write(msg + "\n")


try:
    log("start, doc: %s" % sc.doc.Path)
    m = G.Mesh()
    for o in sc.doc.Objects:
        if not o.Visible:
            continue
        g = o.Geometry
        brep = g.ToBrep() if hasattr(g, "ToBrep") else g
        if isinstance(brep, G.Brep):
            for part in G.Mesh.CreateFromBrep(brep, G.MeshingParameters.QualityRenderMesh):
                m.Append(part)                         # parts stay unwelded, so hard edges keep sharp normals
        elif isinstance(g, G.Mesh):
            m.Append(g)
    m.Normals.ComputeNormals()
    m.Compact()
    with open(MESH, "w") as f:
        for v, n in zip(m.Vertices, m.Normals):
            f.write("v %f %f %f %f %f %f\n" % (v.X, v.Y, v.Z, n.X, n.Y, n.Z))
        for fc in m.Faces:
            f.write("f %d %d %d\n" % (fc.A, fc.B, fc.C))
            if fc.IsQuad:
                f.write("f %d %d %d\n" % (fc.A, fc.C, fc.D))
    log("mesh: %d vertices, %d faces" % (m.Vertices.Count, m.Faces.Count))
except Exception as e:
    log("ERROR: %r" % e)
sc.doc.Modified = False
log("exit")
Rhino.RhinoApp.Exit()
