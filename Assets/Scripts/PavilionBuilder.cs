using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

// "Design our pavilion" (home screen option 2, Jin 24 Oct): a visual, scaled-down version of the real pavilion, on
// the whole table (no robot, so reach doesn't matter). Same logic as "Stacking 3d modules.gh" (Matthias Nahum /
// the team's Grasshopper file):
//  - the module is the three-legged brick of the logo (Assets/Models/pavilion_brick.txt, tools/make_pavilion_brick.py)
//  - the curve is walked in straight chords of 3X (X = distance from the brick's middle to a leg centre, 12.8 mm in
//    the model; "Py3" script: circles along the curve); on each chord one row-1 and one row-2 brick (see Rebuild)
//  - row 1: one leg pointing along the curve; row 2: turned 60 degrees, one brick height up, its legs exactly on the
//    legs below
//  - every next row repeats the row two below, minus its first and last brick ("Cull Index 0, -1"), so the
//    pavilion steps in at both ends; as many rows as the line allows (until the top row is 1 brick)
// Right controller:
//  hold TRIGGER on the table      = draw the line (a new pavilion)
//  TRIGGER on an orange ball      = drag it: the line and the pavilion follow (pull points)
//  THUMBSTICK up / down           = more / fewer rows
//  TRIGGER on a brick             = select it; THUMBSTICK left / right = printed gray clay / printed brown clay /
//                                   gray-to-red (by height) / rammed earth (solid piece, no arches); A = done
//  B                              = home screen (HomeMenu)
public class PavilionBuilder : MonoBehaviour
{
    public Transform workspace;          // table frame: origin = middle of the front edge, on the top surface
    public Transform table;              // its scale = real table size after calibration
    public Transform rightController;
    public TableCalibration calibration;
    [Tooltip("Assets/Models/pavilion_brick.txt (metres, y up, one leg along +z)")]
    public TextAsset brickMesh;
    public Material brickMaterial, lineMaterial, handleMaterial;
    [Tooltip("Model brick (30 mm wide, 15 mm high) x this = brick on the table")]
    public float brickScale = 2.5f;
    [Tooltip("Middle of the brick to the centre of a leg, in the model (m)")]
    public float legSpacing = 0.0128f;
    public float edgeMargin = 0.03f;

    // Colours from Jin's photos of the real pieces (24-25 Oct): printed light clay, printed brown clay, rammed earth
    static readonly Color Gray = new Color(0.80f, 0.76f, 0.68f);
    static readonly Color Brown = new Color(0.70f, 0.55f, 0.33f);
    static readonly Color Red = new Color(0.78f, 0.14f, 0.10f);
    static readonly string[] ColourNames = { "Gray clay", "Brown clay", "Gray to red", "Rammed earth" };
    const int Options = 4, RammedEarth = 3;

    enum Step { Draw, Drawing, Edit }
    Step step = Step.Draw;
    bool shown;
    int rows = 3;
    int rowLimit = 1;   // as many rows as the line allows: until the top row is a single brick (Jin, 25 Oct)

    class Brick { public GameObject go; public Renderer renderer; public MeshFilter filter; public int row, index; }

    readonly List<Vector3> drawn = new List<Vector3>();      // table space
    readonly List<Vector3> pulls = new List<Vector3>();      // pull points (table space)
    readonly List<Transform> pullBalls = new List<Transform>();
    readonly List<Brick> bricks = new List<Brick>();
    readonly Dictionary<Collider, Brick> brickOf = new Dictionary<Collider, Brick>();
    readonly Dictionary<Collider, int> pullOf = new Dictionary<Collider, int>();
    readonly Dictionary<(int, int), int> colours = new Dictionary<(int, int), int>();   // (row, index) -> option
    readonly List<Transform> swatches = new List<Transform>();
    int used;          // bricks in use (the rest of the pool is hidden)
    Brick selected;
    int dragging = -1;

    Mesh mesh, solidMesh;   // printed brick (arches, three legs) and the solid rammed-earth piece
    Texture2D earthLayers;
    Transform root;
    LineRenderer line, pointer;
    Transform pointerDot;
    TextMesh label;
    MaterialPropertyBlock block;
    bool triggerWasDown, aWasDown, stickXWasPushed, stickYWasPushed;

    float X => legSpacing * brickScale;                 // spacing of the points along the curve
    float RowHeight => mesh != null ? mesh.bounds.size.y * brickScale : 0.0375f;

    void Start()
    {
        mesh = LoadMesh(brickMesh);
        solidMesh = SolidPiece(legSpacing, 0.004f, mesh.bounds.size.y > 0f ? mesh.bounds.size.y : 0.015f);
        earthLayers = EarthLayers();
        block = new MaterialPropertyBlock();
        root = new GameObject("Pavilion").transform;
        root.SetParent(workspace, false);

        line = MakeLine("Pavilion Curve", 0.004f);
        line.transform.SetParent(root, false);
        pointer = MakeLine("Pavilion Pointer", 0.002f);
        pointer.positionCount = 2;
        pointerDot = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
        pointerDot.name = "Pavilion Pointer Dot";
        Destroy(pointerDot.GetComponent<Collider>());
        pointerDot.localScale = Vector3.one * 0.012f;
        pointerDot.GetComponent<Renderer>().sharedMaterial = lineMaterial;
        label = MakeLabel();

        for (int i = 0; i < Options; i++)   // choices shown above a selected brick
        {
            var s = new GameObject("Swatch " + ColourNames[i]);
            s.AddComponent<MeshFilter>().sharedMesh = i == RammedEarth ? solidMesh : mesh;
            s.AddComponent<MeshRenderer>().sharedMaterial = brickMaterial;
            s.transform.SetParent(root, false);
            swatches.Add(s.transform);
        }
        AskToDraw();
        SetShown(shown);
    }

    /// <summary>Shown only in "Design our pavilion" (HomeMenu). The design stays when you go home and come back.</summary>
    public void SetShown(bool on)
    {
        shown = on;
        if (root == null) return;
        root.gameObject.SetActive(on);
        if (!on)
        {
            label.gameObject.SetActive(false);
            ShowPointer(false, Vector3.zero);
            Deselect();
            dragging = -1;
            if (step == Step.Drawing) AskToDraw();
        }
        else
        {
            Say(label.text);
            VoiceGuide.Play("pavilion_start");
        }
    }

    /// <summary>Empty table for the next visitor (restart from the opening).</summary>
    public void Clear()
    {
        if (root == null) return;
        Deselect();
        dragging = -1;
        drawn.Clear();
        pulls.Clear();
        colours.Clear();
        rows = 3;
        used = 0;
        HideUnusedBricks();
        UpdatePullBalls();
        ShowLine(drawn);
        AskToDraw();
    }

    void Update()
    {
        if (rightController == null || root == null) return;
        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        right.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger);
        right.TryGetFeatureValue(CommonUsages.primaryButton, out bool a);
        right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick);
        bool triggerPressed = trigger && !triggerWasDown, triggerReleased = !trigger && triggerWasDown;
        bool aPressed = a && !aWasDown;
        bool pushedX = Mathf.Abs(stick.x) > 0.6f, pushedY = Mathf.Abs(stick.y) > 0.6f;
        bool stickX = pushedX && !stickXWasPushed, stickY = pushedY && !stickYWasPushed;
        triggerWasDown = trigger; aWasDown = a; stickXWasPushed = pushedX; stickYWasPushed = pushedY;

        if (!shown || (calibration != null && calibration.IsCalibrating)) return;
        label.gameObject.SetActive(true);
        PlaceLabel();

        // What the controller points at: a pull point, a brick, or the table
        var ray = new Ray(rightController.position, rightController.forward);
        Brick hitBrick = null;
        int hitPull = -1;
        float nearest = 5f;
        foreach (var hit in Physics.RaycastAll(ray, 5f))
        {
            if (hit.distance >= nearest) continue;
            if (pullOf.TryGetValue(hit.collider, out int p)) { hitPull = p; hitBrick = null; nearest = hit.distance; }
            else if (brickOf.TryGetValue(hit.collider, out var b)) { hitBrick = b; hitPull = -1; nearest = hit.distance; }
        }
        bool onTable = PointOnTable(out var tablePoint);
        Vector3 end = hitPull >= 0 || hitBrick != null ? ray.GetPoint(nearest)
                    : onTable ? workspace.TransformPoint(tablePoint) : ray.GetPoint(1f);
        ShowPointer(true, end);

        if (step == Step.Drawing)
        {
            if (onTable && Vector3.Distance(tablePoint, drawn[drawn.Count - 1]) >= 0.01f) drawn.Add(tablePoint);
            ShowLine(drawn);
            if (triggerReleased) FinishDrawing();
            return;
        }

        if (triggerPressed)
        {
            if (hitPull >= 0) { Deselect(); dragging = hitPull; }
            else if (hitBrick != null) Select(hitBrick);
            else if (onTable) StartDrawing(tablePoint);
        }
        if (dragging >= 0)
        {
            if (!trigger) dragging = -1;
            else if (onTable && Vector3.Distance(tablePoint, pulls[dragging]) > 0.003f)
            {
                pulls[dragging] = tablePoint;
                Rebuild();
            }
        }

        if (selected != null)
        {
            if (stickX)
            {
                var key = (selected.row, selected.index);
                colours.TryGetValue(key, out int option);
                colours[key] = (option + (stick.x > 0f ? 1 : Options - 1)) % Options;
                ShowSelection();
            }
            if (aPressed) Deselect();
            Recolour(selected, blink: true);
        }
        else if (step == Step.Edit && stickY)
        {
            int before = rows;
            rows = Mathf.Clamp(rows + (stick.y > 0f ? 1 : -1), 1, rowLimit);
            if (rows != before) Rebuild();
        }
    }

    // ---------- drawing and pull points ----------

    void AskToDraw()
    {
        step = Step.Draw;
        Say("Design our pavilion\nPoint at the table and HOLD the TRIGGER to draw its line\nB = home");
    }

    void StartDrawing(Vector3 first)
    {
        Deselect();
        drawn.Clear();
        drawn.Add(first);
        pulls.Clear();
        UpdatePullBalls();
        used = 0;
        HideUnusedBricks();
        step = Step.Drawing;
        Say("Keep holding the trigger...\nLet go at the end of your line");
    }

    void FinishDrawing()
    {
        float length = Length(drawn);
        if (drawn.Count < 2 || length < 0.08f)
        {
            drawn.Clear();
            ShowLine(drawn);
            step = Step.Draw;
            Say("That line was too short\nHOLD the TRIGGER and draw a longer line\nB = home");
            return;
        }
        Smooth(drawn, 3);
        // Pull points: evenly along the drawn line, about one every 15 cm (3 to 7)
        int n = Mathf.Clamp(Mathf.RoundToInt(length / 0.15f) + 1, 3, 7);
        pulls.Clear();
        for (int i = 0; i < n; i++) pulls.Add(PointAtDistance(drawn, length * i / (n - 1)));
        step = Step.Edit;
        Rebuild();
    }

    void UpdatePullBalls()
    {
        while (pullBalls.Count < pulls.Count)
        {
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
            ball.name = "Pull Point";
            ball.SetParent(root, false);
            ball.localScale = Vector3.one * 0.03f;
            if (handleMaterial != null) ball.GetComponent<Renderer>().sharedMaterial = handleMaterial;
            pullOf[ball.GetComponent<Collider>()] = pullBalls.Count;
            pullBalls.Add(ball);
        }
        for (int i = 0; i < pullBalls.Count; i++)
        {
            pullBalls[i].gameObject.SetActive(i < pulls.Count);
            if (i < pulls.Count) pullBalls[i].localPosition = pulls[i] + Vector3.up * 0.015f;
        }
    }

    // ---------- the pavilion ----------

    void Rebuild()
    {
        UpdatePullBalls();
        var curve = CatmullRom(pulls, 16);
        ShowLine(curve);

        // Like the GH "Py3" script: walk along the curve in straight CHORDS of 3X ("D = 2X + Y", X = Y), from joint to
        // joint (joints on the curve). On each chord, from its joint J along its direction u:
        //   row-2 brick at J + X  (one leg pointing back, standing on J)
        //   row-1 brick at J + 2X (one leg pointing forward, on the next joint)
        // Both stand on the same straight chord, so their side legs (at 1.5X) coincide exactly, and neighbouring chords
        // share a leg at their joint: every leg stands on a leg below, also where the line bends. (The first version
        // placed each brick along its own little step of the curve; on bends the legs missed, bricks floated, 25 Oct.)
        var joints = new List<Vector3> { curve[0] };
        int progress = 0;
        while (joints.Count < 1000 && StepAlong(curve, joints[joints.Count - 1], 3f * X, ref progress, out var next))
            joints.Add(next);
        var rowA = new List<(Vector3 pos, Quaternion rot)>();   // rows 1, 3, 5, 7
        var rowB = new List<(Vector3 pos, Quaternion rot)>();   // rows 2, 4, 6 (turned 60 degrees)
        for (int k = 0; k < joints.Count - 1; k++)
        {
            Vector3 u = joints[k + 1] - joints[k];
            u.y = 0f;
            u = u.sqrMagnitude > 1e-8f ? u.normalized : Vector3.forward;
            var forward = Quaternion.LookRotation(u, Vector3.up);
            if (k == 0) rowA.Add((joints[0] - u * X, forward));     // GH's extra first brick: carries row 2's first leg
            rowA.Add((joints[k] + u * (2f * X), forward));
            rowB.Add((joints[k] + u * X, forward * Quaternion.Euler(0f, 180f, 0f)));   // = 60 degrees for 3 legs
        }

        // Each next row = the row two below minus its first and last brick (GH "Cull Index 0, -1"). Highest possible:
        // up to the row that still has 1 brick, so a longer line can go higher (no fixed 7 rows any more)
        rowLimit = 1;
        while ((rowLimit % 2 == 0 ? rowA.Count : rowB.Count) - 2 * (rowLimit / 2) >= 1) rowLimit++;
        rows = Mathf.Clamp(rows, 1, rowLimit);
        used = 0;
        for (int r = 0; r < rows; r++)
        {
            var source = r % 2 == 0 ? rowA : rowB;
            int cull = r / 2;
            for (int k = cull; k < source.Count - cull; k++)
            {
                var brick = NextBrick();
                brick.row = r;
                brick.index = k;
                brick.go.transform.localPosition = source[k].pos + Vector3.up * (r * RowHeight + 0.0005f);
                brick.go.transform.localRotation = source[k].rot;
                Recolour(brick, false);
            }
        }
        HideUnusedBricks();
        if (selected != null && !selected.go.activeSelf) Deselect();
        if (selected == null)
            Say($"Your pavilion: {rows} rows, {used} bricks\n" +
                "THUMBSTICK up / down = higher / lower\n" +
                "TRIGGER on an orange ball = reshape the line\n" +
                "TRIGGER on a brick = change its colour\n" +
                "TRIGGER on the table = draw again     B = home");
    }

    Brick NextBrick()
    {
        if (used == bricks.Count)
        {
            var go = new GameObject("Pavilion Brick");
            go.transform.SetParent(root, false);
            go.transform.localScale = Vector3.one * brickScale;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = brickMaterial;
            var box = go.AddComponent<BoxCollider>();
            box.center = mesh.bounds.center;
            box.size = mesh.bounds.size;
            var b = new Brick { go = go, renderer = r, filter = mf };
            brickOf[box] = b;
            bricks.Add(b);
        }
        var brick = bricks[used++];
        brick.go.SetActive(true);
        return brick;
    }

    void HideUnusedBricks()
    {
        for (int i = used; i < bricks.Count; i++) bricks[i].go.SetActive(false);
    }

    // ---------- colours ----------

    Color ColourOf(int option, int row)
    {
        if (option == 1) return Brown;
        if (option == 2) return Color.Lerp(Gray, Red, rows > 1 ? row / (rows - 1f) : 1f);   // vertical gradient over the rows
        return Gray;
    }

    void Recolour(Brick b, bool blink)
    {
        colours.TryGetValue((b.row, b.index), out int option);
        b.filter.sharedMesh = option == RammedEarth ? solidMesh : mesh;
        var c = option == RammedEarth ? Color.white : ColourOf(option, b.row);
        if (blink) c = Color.Lerp(c, Color.white * 1.3f, 0.25f + 0.25f * Mathf.Sin(Time.time * 6f));
        SetLook(b.renderer, c, option == RammedEarth);
    }

    // Colour, plus the earth layers texture for rammed earth (white texture = plain colour)
    void SetLook(Renderer r, Color c, bool earth)
    {
        block.SetColor("_BaseColor", c);
        block.SetTexture("_BaseMap", earth ? earthLayers : Texture2D.whiteTexture);
        r.SetPropertyBlock(block);
    }

    void Select(Brick b)
    {
        Deselect();
        selected = b;
        ShowSelection();
    }

    void Deselect()
    {
        if (selected != null) Recolour(selected, false);
        selected = null;
        foreach (var s in swatches) s.gameObject.SetActive(false);
        if (step == Step.Edit && pulls.Count > 0 && root != null && root.gameObject.activeInHierarchy) Rebuild();
    }

    // Three small bricks above the selected one: gray, brown, gray-to-red; the current choice is bigger
    void ShowSelection()
    {
        colours.TryGetValue((selected.row, selected.index), out int option);
        var cam = Camera.main;
        Vector3 across = cam != null ? workspace.InverseTransformDirection(cam.transform.right) : Vector3.right;
        across.y = 0f;
        across = across.sqrMagnitude > 1e-6f ? across.normalized : Vector3.right;
        Vector3 above = selected.go.transform.localPosition + Vector3.up * (RowHeight * 2.5f);
        for (int i = 0; i < Options; i++)
        {
            var s = swatches[i];
            s.gameObject.SetActive(true);
            s.localPosition = above + across * ((i - (Options - 1) / 2f) * 0.07f);
            s.localScale = Vector3.one * brickScale * (i == option ? 0.8f : 0.5f);
            SetLook(s.GetComponent<Renderer>(), i == RammedEarth ? Color.white : i == 2 ? Red : ColourOf(i, 0), i == RammedEarth);
        }
        Say($"Brick: {ColourNames[option]}\nTHUMBSTICK left / right = gray clay / brown clay / gray to red / rammed earth\nA = done");
    }

    // ---------- helpers ----------

    // Where the controller's ray hits the table top, clamped to the table (table space)
    bool PointOnTable(out Vector3 local)
    {
        local = Vector3.zero;
        var plane = new Plane(workspace.up, workspace.position);
        var ray = new Ray(rightController.position, rightController.forward);
        if (!plane.Raycast(ray, out float distance) || distance > 5f) return false;
        local = workspace.InverseTransformPoint(ray.GetPoint(distance));
        float half = table.localScale.x / 2f - edgeMargin;
        local.x = Mathf.Clamp(local.x, -half, half);
        local.z = Mathf.Clamp(local.z, edgeMargin, table.localScale.z - edgeMargin);
        local.y = 0f;
        return true;
    }

    void ShowPointer(bool show, Vector3 end)
    {
        if (pointer == null) return;
        pointer.enabled = show;
        pointerDot.gameObject.SetActive(show);
        if (!show) return;
        pointer.SetPosition(0, rightController.position);
        pointer.SetPosition(1, end);
        pointerDot.position = end;
    }

    void ShowLine(List<Vector3> pts)
    {
        line.positionCount = pts.Count;
        for (int i = 0; i < pts.Count; i++) line.SetPosition(i, pts[i] + Vector3.up * 0.003f);
    }

    static List<Vector3> CatmullRom(List<Vector3> p, int perSegment)
    {
        var outPts = new List<Vector3>();
        for (int i = 0; i < p.Count - 1; i++)
        {
            Vector3 p0 = p[Mathf.Max(i - 1, 0)], p1 = p[i], p2 = p[i + 1], p3 = p[Mathf.Min(i + 2, p.Count - 1)];
            for (int s = 0; s < perSegment; s++)
            {
                float t = s / (float)perSegment, t2 = t * t, t3 = t2 * t;
                outPts.Add(0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                                   (-p0 + 3f * p1 - 3f * p2 + p3) * t3));
            }
        }
        outPts.Add(p[p.Count - 1]);
        return outPts;
    }

    static Vector3 PointAtDistance(List<Vector3> pts, float distance)
    {
        for (int i = 1; i < pts.Count; i++)
        {
            float seg = Vector3.Distance(pts[i - 1], pts[i]);
            if (distance <= seg && seg > 0f) return Vector3.Lerp(pts[i - 1], pts[i], distance / seg);
            distance -= seg;
        }
        return pts[pts.Count - 1];
    }

    // The first point further along the curve (from segment `progress` on) that is exactly r away from `from`
    static bool StepAlong(List<Vector3> pts, Vector3 from, float r, ref int progress, out Vector3 hit)
    {
        hit = Vector3.zero;
        for (int seg = progress; seg < pts.Count - 1; seg++)
        {
            Vector3 a = pts[seg], ab = pts[seg + 1] - a, ac = a - from;
            float qa = Vector3.Dot(ab, ab);
            if (qa < 1e-10f) continue;
            float qb = 2f * Vector3.Dot(ab, ac), qc = Vector3.Dot(ac, ac) - r * r;
            float disc = qb * qb - 4f * qa * qc;
            if (disc < 0f) continue;
            float t = (-qb + Mathf.Sqrt(disc)) / (2f * qa);
            if (t < 0f || t > 1f) continue;
            hit = a + ab * t;
            progress = seg;
            return true;
        }
        return false;
    }

    static float Length(List<Vector3> pts)
    {
        float sum = 0f;
        for (int i = 1; i < pts.Count; i++) sum += Vector3.Distance(pts[i - 1], pts[i]);
        return sum;
    }

    static void Smooth(List<Vector3> pts, int passes)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            var copy = new List<Vector3>(pts);
            for (int i = 1; i < pts.Count - 1; i++) pts[i] = (copy[i - 1] + copy[i] * 2f + copy[i + 1]) / 4f;
        }
    }

    // The rammed-earth piece (Jin's photo, 25 Oct): solid, same outline and height as the printed brick: the triangle
    // around the three legs with rounded corners (leg radius), no arches. One leg points along +z, like the mesh.
    static Mesh SolidPiece(float legDistance, float legRadius, float height)
    {
        const int perCorner = 10;
        var ring = new List<(Vector3 p, Vector3 n)>();
        for (int leg = 0; leg < 3; leg++)
        {
            float a0 = leg * 120f;
            Vector3 centre = Dir(a0) * legDistance;
            for (int k = 0; k <= perCorner; k++)
            {
                float a = a0 - 60f + 120f * k / perCorner;
                ring.Add((centre + Dir(a) * legRadius, Dir(a)));
            }
        }
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        int n = ring.Count;
        // sides: u around the piece, v = height (the earth layers run horizontally)
        for (int i = 0; i <= n; i++)
        {
            var (p, nr) = ring[i % n];
            verts.Add(p); normals.Add(nr); uvs.Add(new Vector2(i / (float)n, 0f));
            verts.Add(p + Vector3.up * height); normals.Add(nr); uvs.Add(new Vector2(i / (float)n, 1f));
        }
        for (int i = 0; i < n; i++)
        {
            int b0 = 2 * i, t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
            tris.AddRange(new[] { t1, t0, b0, t1, b0, b1 });
        }
        // top and bottom: fans from the middle (the outline is convex); the top shows the top layer's colour
        foreach (bool top in new[] { true, false })
        {
            int c = verts.Count;
            float y = top ? height : 0f;
            var up = top ? Vector3.up : Vector3.down;
            var uv = new Vector2(0.5f, top ? 0.99f : 0.01f);
            verts.Add(new Vector3(0f, y, 0f)); normals.Add(up); uvs.Add(uv);
            for (int i = 0; i < n; i++)
            {
                verts.Add(ring[i].p + Vector3.up * y); normals.Add(up); uvs.Add(uv);
            }
            for (int i = 0; i < n; i++)
            {
                int a = c + 1 + i, b = c + 1 + (i + 1) % n;
                if (top) tris.AddRange(new[] { c, a, b });
                else tris.AddRange(new[] { c, b, a });
            }
        }
        var m = new Mesh { name = "Rammed Earth Piece" };
        m.SetVertices(verts);
        m.SetNormals(normals);
        m.SetUVs(0, uvs);
        m.SetTriangles(tris, 0);
        m.RecalculateBounds();
        return m;
    }

    static Vector3 Dir(float degrees) => new Vector3(Mathf.Sin(degrees * Mathf.Deg2Rad), 0f, Mathf.Cos(degrees * Mathf.Deg2Rad));

    // Horizontal layers like the rammed-earth photo: ochre bands with paler, gravelly bands in between
    static Texture2D EarthLayers()
    {
        var ochre = new Color(0.76f, 0.58f, 0.32f);
        var pale = new Color(0.82f, 0.74f, 0.60f);
        float[] edges = { 0f, 0.14f, 0.30f, 0.46f, 0.62f, 0.78f, 1f };   // band edges, bottom to top
        const int h = 64;
        var tex = new Texture2D(4, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Earth Layers" };
        var rnd = new System.Random(5);
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / h;
            int band = 0;
            while (band < edges.Length - 2 && v > edges[band + 1]) band++;
            var c = band % 2 == 0 ? pale : ochre;
            if (band == edges.Length - 2) c = ochre;                      // the top layer is ochre
            float grain = 0.94f + 0.08f * (float)rnd.NextDouble();
            for (int x = 0; x < 4; x++) tex.SetPixel(x, y, c * grain);
        }
        tex.Apply();
        return tex;
    }

    static Mesh LoadMesh(TextAsset file)
    {
        var mesh = new Mesh { name = "Pavilion Brick" };
        if (file == null) return mesh;
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var raw in file.text.Split('\n'))
        {
            var p = raw.Trim().Split(' ');
            if (p[0] == "v" && p.Length >= 7)
            {
                verts.Add(new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv)));
                normals.Add(new Vector3(float.Parse(p[4], inv), float.Parse(p[5], inv), float.Parse(p[6], inv)));
            }
            else if (p[0] == "f" && p.Length >= 4)
            {
                tris.Add(int.Parse(p[1])); tris.Add(int.Parse(p[2])); tris.Add(int.Parse(p[3]));
            }
        }
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    LineRenderer MakeLine(string name, float width)
    {
        var go = new GameObject(name);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = lineMaterial;
        lr.widthMultiplier = width;
        lr.useWorldSpace = false;
        lr.positionCount = 0;
        lr.numCornerVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    TextMesh MakeLabel()
    {
        var go = new GameObject("Pavilion Instructions");
        go.transform.SetParent(transform, false);
        var tm = go.AddComponent<TextMesh>();
        tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
        tm.fontSize = 48;
        tm.characterSize = 0.005f;
        tm.anchor = TextAnchor.LowerCenter;
        tm.alignment = TextAlignment.Center;
        go.SetActive(false);
        return tm;
    }

    void PlaceLabel()
    {
        label.transform.position = workspace.TransformPoint(new Vector3(0f, 0.4f, table.localScale.z / 2f));
        var cam = Camera.main;
        if (cam != null)
            label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position);
    }

    void Say(string text)
    {
        if (label != null) label.text = text;
    }
}
