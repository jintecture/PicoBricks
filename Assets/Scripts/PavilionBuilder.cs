using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

// "Design our pavilion" (home screen option 2, Jin 24 Oct): a visual, scaled-down version of the real pavilion, on
// the whole table (no robot, so reach doesn't matter). Same logic as "Stacking 3d modules.gh" (Matthias Nahum /
// the team's Grasshopper file):
//  - the module is the three-legged brick of the logo (Assets/Models/pavilion_brick.txt, tools/make_pavilion_brick.py)
//  - along the drawn curve a point is set every X (= distance from the brick's middle to a leg centre, 12.8 mm in
//    the model), measured as a straight line ("Py3" script: circles along the curve)
//  - row 1: a brick on every 3rd point (0, 3, 6, ...), one leg pointing along the curve
//  - row 2: on points 2, 5, 8, ..., turned 60 degrees, one brick height up: its legs stand on the legs below
//  - every next row repeats the row two below, minus its first and last brick ("Cull Index 0, -1"), so the
//    pavilion steps in at both ends; up to 7 rows
// Right controller:
//  hold TRIGGER on the table      = draw the line (a new pavilion)
//  TRIGGER on an orange ball      = drag it: the line and the pavilion follow (pull points)
//  THUMBSTICK up / down           = more / fewer rows
//  TRIGGER on a brick             = select it; THUMBSTICK left / right = gray / brown / gray-to-red (by height); A = done
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
    public int maxRows = 7;
    public float edgeMargin = 0.03f;

    static readonly Color Gray = new Color(0.62f, 0.60f, 0.58f);
    static readonly Color Brown = new Color(0.55f, 0.36f, 0.20f);
    static readonly Color Red = new Color(0.78f, 0.14f, 0.10f);
    static readonly string[] ColourNames = { "Gray", "Brown", "Gray to red" };

    enum Step { Draw, Drawing, Edit }
    Step step = Step.Draw;
    bool shown;
    int rows = 3;

    class Brick { public GameObject go; public Renderer renderer; public int row, index; }

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

    Mesh mesh;
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

        for (int i = 0; i < 3; i++)   // colour swatches shown above a selected brick
        {
            var s = new GameObject("Swatch " + ColourNames[i]);
            s.AddComponent<MeshFilter>().sharedMesh = mesh;
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
        else Say(label.text);
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
                colours[key] = (option + (stick.x > 0f ? 1 : 2)) % 3;
                ShowSelection();
            }
            if (aPressed) Deselect();
            Recolour(selected, blink: true);
        }
        else if (step == Step.Edit && stickY)
        {
            int before = rows;
            rows = Mathf.Clamp(rows + (stick.y > 0f ? 1 : -1), 1, maxRows);
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

        // Points every X along the curve (straight-line distance, forward only), and the direction to the next one
        var points = new List<Vector3> { curve[0] };
        int progress = 0;
        while (points.Count < 2000 && StepAlong(curve, points[points.Count - 1], X, ref progress, out var next))
            points.Add(next);
        var dirs = new List<Vector3>();
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 d = i < points.Count - 1 ? points[i + 1] - points[i] : points[i] - points[Mathf.Max(0, i - 1)];
            d.y = 0f;
            dirs.Add(d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.forward);
        }

        // Row 1 on points 0, 3, 6 ...; row 2 on 2, 5, 8 ... turned; each next row = the row two below minus its ends
        var rowA = new List<int>();
        var rowB = new List<int>();
        for (int i = 0; i < points.Count; i++)
        {
            if (i % 3 == 0) rowA.Add(i);
            else if (i % 3 == 2) rowB.Add(i);
        }
        used = 0;
        for (int r = 0; r < rows; r++)
        {
            var source = r % 2 == 0 ? rowA : rowB;
            int cull = r / 2;
            for (int k = cull; k < source.Count - cull; k++)
            {
                int i = source[k];
                var rotation = Quaternion.LookRotation(dirs[i], Vector3.up);
                if (r % 2 == 1) rotation *= Quaternion.Euler(0f, 180f, 0f);   // = 60 degrees for a 3-legged brick
                var brick = NextBrick();
                brick.row = r;
                brick.index = k;
                brick.go.transform.localPosition = points[i] + Vector3.up * (r * RowHeight + 0.0005f);
                brick.go.transform.localRotation = rotation;
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
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = brickMaterial;
            var box = go.AddComponent<BoxCollider>();
            box.center = mesh.bounds.center;
            box.size = mesh.bounds.size;
            var b = new Brick { go = go, renderer = r };
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
        if (option == 2) return Color.Lerp(Gray, Red, rows > 1 ? row / (rows - 1f) : 1f);   // vertical gradient
        return Gray;
    }

    void Recolour(Brick b, bool blink)
    {
        colours.TryGetValue((b.row, b.index), out int option);
        var c = ColourOf(option, b.row);
        if (blink) c = Color.Lerp(c, Color.white, 0.25f + 0.25f * Mathf.Sin(Time.time * 6f));
        block.SetColor("_BaseColor", c);
        b.renderer.SetPropertyBlock(block);
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
        for (int i = 0; i < 3; i++)
        {
            var s = swatches[i];
            s.gameObject.SetActive(true);
            s.localPosition = above + across * ((i - 1) * 0.07f);
            s.localScale = Vector3.one * brickScale * (i == option ? 0.8f : 0.5f);
            block.SetColor("_BaseColor", i == 2 ? Red : ColourOf(i, 0));
            s.GetComponent<Renderer>().SetPropertyBlock(block);
        }
        Say($"Brick colour: {ColourNames[option]}\nTHUMBSTICK left / right = gray / brown / gray to red\nA = done");
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
