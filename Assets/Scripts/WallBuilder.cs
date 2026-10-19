using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// Version 2, iteration 1 (rectangle): draw a CURVE on the table, choose 1-3 layers, and the 6 bricks
// build a wall that mimics the curve (6 x 1, 3 x 2 or 2 x 3).
//
// Right controller:  point at the table + hold TRIGGER = draw the curve
//                    THUMBSTICK left/right            = number of layers (1-3)
//                    A                                = OK, build the wall
//
// How a curve becomes bricks (see docs/DEVLOG.md, "curve logic"):
//  1. record a point every ~1 cm while the trigger is held
//  2. smooth out hand wobble (start and end stay put)
//  3. scale the curve about its start point so it is exactly as long as one layer of bricks:
//     a short scribble and a long sweep give the same shape, at wall size ("mimic the curve")
//  4. "dividers": from where a brick starts, the point on the curve one brick length further on sets its direction
//  5. "folding ruler": neighbouring bricks are hinged at the corner on the inside of the bend, so they never
//     overlap (a wedge-shaped gap opens on the outside). Every brick is aimed from where it really starts, so its
//     front end lands back on the curve and the wall can't drift off it (fixed 2026-10-18, found by Jin)
//  6. rectangle: every layer repeats the same chain, stacked straight up
public class WallBuilder : MonoBehaviour
{
    public Transform workspace;          // table frame: origin = middle of the front edge, on the top surface
    public Transform table;              // its scale = real table size after calibration
    public Transform bricksParent;
    public Transform rightController;
    public TableCalibration calibration;
    public BrickControls controls;
    public Material lineMaterial;
    public Material zoneMaterial;
    [Tooltip("Pickup tray at the table's front-left corner; the wall can't be built on top of it")]
    public Transform tray;
    public Vector2 traySize = new Vector2(0.229f, 0.250f); // x (width) and z (depth), m
    [Tooltip("Ghost UR5: walls it can't reach are refused, and its reach is drawn on the table")]
    public GhostRobot robot;
    public Material reachMaterial;

    [Header("Bricks")]
    public Vector3 brickSize = new Vector3(0.05f, 0.05f, 0.10f); // width, height, length (m)
    public int maxLayers = 3;

    [Header("Drawing")]
    [Tooltip("The wall must stay this far inside the table edges and away from the tray (m)")]
    public float edgeMargin = 0.03f;
    [Tooltip("Curves shorter than this are ignored (m)")]
    public float minCurveLength = 0.05f;
    [Tooltip("A new point is recorded every this many metres while drawing")]
    public float sampleSpacing = 0.01f;
    [Tooltip("How many times the curve is smoothed (0 = use it exactly as drawn)")]
    public int smoothingPasses = 3;

    enum Step { Draw, Drawing, ChooseLayers, Built }
    Step step = Step.Draw;

    readonly List<Vector3> curve = new List<Vector3>();   // table space, y = 0
    int layers = 1;
    bool triggerWasDown, aWasDown, stickWasPushed;

    readonly List<Rigidbody> bricks = new List<Rigidbody>();
    LineRenderer line, zone, reach;
    string misfit; // why the last plan was refused
    TextMesh label;

    int BrickCount => bricks.Count;

    void Start()
    {
        foreach (var rb in bricksParent.GetComponentsInChildren<Rigidbody>())
            bricks.Add(rb);

        line = MakeLine("Wall Curve", lineMaterial, 0.006f, false);
        zone = MakeLine("Build Area", zoneMaterial != null ? zoneMaterial : lineMaterial, 0.003f, true);
        reach = MakeLine("Robot Reach", reachMaterial != null ? reachMaterial : lineMaterial, 0.004f, false);
        label = MakeLabel();

        if (calibration != null) calibration.Placed += Restart;
        if (controls != null) controls.BricksReset += Restart;
        Restart();
    }

    void OnDestroy()
    {
        if (calibration != null) calibration.Placed -= Restart;
        if (controls != null) controls.BricksReset -= Restart;
    }

    void Restart()
    {
        step = Step.Draw;
        curve.Clear();
        line.positionCount = 0;
        DrawBuildArea();
        Say("Point at the table and hold the TRIGGER\nto draw where the wall goes (straight or curved)");
    }

    void Update()
    {
        if (rightController == null) return;
        PlaceLabel();

        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!right.isValid) return;
        right.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger);
        right.TryGetFeatureValue(CommonUsages.primaryButton, out bool a);
        right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick);
        bool triggerPressed = trigger && !triggerWasDown;
        bool triggerReleased = !trigger && triggerWasDown;
        bool aPressed = a && !aWasDown;
        // Remember button states every frame, so a trigger still held from calibration doesn't start a curve
        triggerWasDown = trigger;
        aWasDown = a;

        // While the table is being calibrated, the trigger belongs to the calibration
        if (calibration != null && calibration.IsCalibrating)
        {
            label.gameObject.SetActive(false);
            return;
        }
        label.gameObject.SetActive(true);

        switch (step)
        {
            case Step.Draw:
            case Step.ChooseLayers:
            case Step.Built:
                if (triggerPressed && PointOnTable(out var first))
                {
                    if (step == Step.Built && controls != null) controls.ResetBricks(); // bricks back into the tray
                    curve.Clear();
                    curve.Add(first);
                    step = Step.Drawing;
                    Say("Drawing... release the trigger at the end of the wall");
                }
                else if (step == Step.ChooseLayers)
                {
                    ChooseLayers(stick);
                    if (aPressed) Build();
                }
                break;

            case Step.Drawing:
                if (PointOnTable(out var p) && Vector3.Distance(p, curve[curve.Count - 1]) >= sampleSpacing)
                    curve.Add(p);
                ShowCurve(curve);
                if (triggerReleased) FinishCurve();
                break;
        }
    }

    // ---------- drawing ----------

    // Where the controller's pointing ray hits the table top, clamped to the build area (table space)
    bool PointOnTable(out Vector3 local)
    {
        local = Vector3.zero;
        var plane = new Plane(workspace.up, workspace.position);
        var ray = new Ray(rightController.position, rightController.forward);
        if (!plane.Raycast(ray, out float distance) || distance > 5f) return false;

        local = workspace.InverseTransformPoint(ray.GetPoint(distance));
        GetBuildArea(out float minX, out float maxX, out float minZ, out float maxZ);
        local.x = Mathf.Clamp(local.x, minX, maxX);
        local.z = Mathf.Clamp(local.z, minZ, maxZ);
        local.y = 0f;
        return true;
    }

    void FinishCurve()
    {
        if (curve.Count < 2 || Length(curve) < minCurveLength)
        {
            step = Step.Draw;
            line.positionCount = 0;
            Say("That was too short, draw again\n(hold the TRIGGER while pointing at the table)");
            return;
        }
        Smooth(curve, smoothingPasses);
        ShowCurve(curve);
        step = Step.ChooseLayers;
        ShowLayerChoice();
    }

    // ---------- layers ----------

    void ChooseLayers(Vector2 stick)
    {
        bool pushed = Mathf.Abs(stick.x) > 0.6f;
        if (pushed && !stickWasPushed)
        {
            layers = Mathf.Clamp(layers + (stick.x > 0 ? 1 : -1), 1, maxLayers);
            ShowLayerChoice();
        }
        stickWasPushed = pushed;
    }

    void ShowLayerChoice()
    {
        int perLayer = BrickCount / layers;
        Say($"Layers:  <  {layers}  >   (thumbstick left/right)\n" +
            $"{perLayer} bricks along your curve x {layers} high\n" +
            "A = OK      TRIGGER = draw again");
    }

    // ---------- building ----------

    struct BrickPose
    {
        public int layer, index;
        public Vector3 position;   // centre, table space (m)
        public Quaternion rotation;
        public float yawDeg;
        public Vector3[] footprint; // 4 corners on the table, for the fit check
    }

    void Build()
    {
        var plan = PlanRectangle(layers, out var wallCurve);
        if (plan == null)
        {
            Say($"A {BrickCount / layers}-brick wall along that curve {misfit}.\n" +
                "Draw again (TRIGGER) or choose more layers (shorter wall)");
            return;
        }

        for (int i = 0; i < plan.Count; i++)
        {
            var rb = bricks[i];
            var grab = rb.GetComponent<XRGrabInteractable>();
            if (grab != null) grab.enabled = false; // the wall is generated, not hand-placed
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            var pos = workspace.TransformPoint(plan[i].position);
            var rot = workspace.rotation * plan[i].rotation;
            rb.transform.SetPositionAndRotation(pos, rot);
            rb.position = pos;
            rb.rotation = rot;
        }

        ShowCurve(wallCurve); // the curve at wall size, as the bricks follow it
        step = Step.Built;
        int perLayer = BrickCount / layers;
        Say($"Wall built: {perLayer} x {layers}\nTRIGGER = draw a new wall      X = bricks back");
        SavePlan(plan, perLayer);
    }

    // Rectangle: every layer has the same chain of bricks along the curve, stacked straight up.
    // Returns null if the wall would stick out of the build area.
    List<BrickPose> PlanRectangle(int layerCount, out List<Vector3> wallCurve)
    {
        int perLayer = BrickCount / layerCount;
        float L = brickSize.z, W = brickSize.x;

        // 3. scale the drawn curve about its start so its length = one layer of bricks
        wallCurve = new List<Vector3>(curve.Count);
        float scale = perLayer * L / Length(curve);
        foreach (var p in curve) wallCurve.Add(curve[0] + (p - curve[0]) * scale);

        // 4. first brick: from the start, step one brick length along the curve ("dividers")
        int progress = 0;
        Vector3 firstDir = (wallCurve[wallCurve.Count - 1] - wallCurve[0]).normalized;
        if (StepAlong(wallCurve, wallCurve[0], L, ref progress, out var firstHit))
            firstDir = (firstHit - wallCurve[0]).normalized;

        // The wall body sits on the side of the curve away from the viewer (decided at the start),
        // so the drawn curve stays visible along the wall's front
        float sideSign = 1f;
        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 viewer = workspace.InverseTransformPoint(cam.transform.position) - curve[0];
            if (Vector3.Dot(Vector3.Cross(Vector3.up, firstDir), viewer) > 0f) sideSign = -1f;
        }
        Vector3 Side(Vector3 d) => Vector3.Cross(Vector3.up, d) * sideSign;

        // 5. chain the bricks. Each brick is hinged to the previous one at the corner on the inside of the
        //    bend (never overlap), and aimed so its front end lands back ON the curve one brick length later.
        //    Aiming from where the brick really starts (not from fixed points) means a shift caused by a
        //    back-corner hinge is corrected by the next brick instead of carried along the whole wall.
        var chain = new List<(Vector3 frontStart, Vector3 dir)> { (wallCurve[0], firstDir) };
        for (int i = 1; i < perLayer; i++)
        {
            var (prevStart, prevDir) = chain[i - 1];
            Vector3 prevEnd = prevStart + prevDir * L;
            Vector3 prevSide = Side(prevDir);

            int tryProgress = progress;
            Vector3 d = StepAlong(wallCurve, prevEnd, L, ref tryProgress, out var hit) ? (hit - prevEnd).normalized : prevDir;
            Vector3 start = prevEnd;

            if (Vector3.Dot(d, prevSide) > 0f)
            {
                // bends towards the wall body: hinge on the BACK corner; the brick then starts slightly off the
                // curve, so re-aim from that real start (a few rounds, start and direction depend on each other)
                Vector3 backCorner = prevEnd + prevSide * W;
                for (int k = 0; k < 5; k++)
                {
                    start = backCorner - Side(d) * W;
                    tryProgress = progress;
                    if (!StepAlong(wallCurve, start, L, ref tryProgress, out hit)) break;
                    d = (hit - start).normalized;
                }
            }
            // else: bends away from the body (or straight): hinge on the FRONT corner, which is on the curve

            progress = tryProgress;
            chain.Add((start, d));
        }

        GetBuildArea(out float minX, out float maxX, out float minZ, out float maxZ);
        const float tol = 0.001f;
        var plan = new List<BrickPose>();
        for (int layer = 0; layer < layerCount; layer++)
            for (int i = 0; i < perLayer; i++)
            {
                var (start, d) = chain[i];
                Vector3 side = Side(d);
                Vector3 centre = start + d * (L / 2f) + side * (W / 2f);
                var corners = new[] { start, start + d * L, start + d * L + side * W, start + side * W };
                foreach (var c in corners)
                {
                    if (c.x < minX - tol || c.x > maxX + tol || c.z < minZ - tol || c.z > maxZ + tol)
                    { misfit = "doesn't fit on the table"; return null; }
                    if (OnTray(c)) { misfit = "would land on the pickup tray"; return null; }
                    if (robot != null && !robot.InReach(c)) { misfit = "is out of the robot's reach (orange line)"; return null; }
                }
                centre.y = brickSize.y * (layer + 0.5f) + 0.0005f;
                var rotation = Quaternion.LookRotation(d, Vector3.up); // brick length (local z) along d
                plan.Add(new BrickPose
                {
                    layer = layer, index = i, position = centre, rotation = rotation,
                    yawDeg = rotation.eulerAngles.y, footprint = corners
                });
            }
        return plan;
    }

    // "Dividers": the first point on the curve, from segment `progress` onwards, that is exactly L away from
    // `from`. Only looks forward along the curve (never back to where we came from). Returns false if the
    // curve runs out; the caller then carries on in the last direction.
    static bool StepAlong(List<Vector3> pts, Vector3 from, float L, ref int progress, out Vector3 hit)
    {
        hit = Vector3.zero;
        for (int seg = progress; seg < pts.Count - 1; seg++)
        {
            if (IntersectSegmentCircle(pts[seg], pts[seg + 1], from, L, out hit))
            {
                progress = seg;
                return true;
            }
        }
        return false;
    }

    // Point on segment a-b (in the xz plane) at distance r from c, the furthest along the segment
    static bool IntersectSegmentCircle(Vector3 a, Vector3 b, Vector3 c, float r, out Vector3 hit)
    {
        hit = Vector3.zero;
        Vector3 ab = b - a, ac = a - c;
        float qa = Vector3.Dot(ab, ab);
        if (qa < 1e-10f) return false;
        float qb = 2f * Vector3.Dot(ab, ac);
        float qc = Vector3.Dot(ac, ac) - r * r;
        float disc = qb * qb - 4f * qa * qc;
        if (disc < 0f) return false;
        float t = (-qb + Mathf.Sqrt(disc)) / (2f * qa); // the exit point (further along)
        if (t < 0f || t > 1f) return false;
        hit = a + ab * t;
        return true;
    }

    static float Length(List<Vector3> pts)
    {
        float sum = 0f;
        for (int i = 1; i < pts.Count; i++) sum += Vector3.Distance(pts[i - 1], pts[i]);
        return sum;
    }

    // Moving average over neighbours; the first and last point stay where they were drawn
    static void Smooth(List<Vector3> pts, int passes)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            var copy = new List<Vector3>(pts);
            for (int i = 1; i < pts.Count - 1; i++)
                pts[i] = (copy[i - 1] + copy[i] * 2f + copy[i + 1]) / 4f;
        }
    }

    // The brick list for the robot: build order (bottom layer first, along the curve), table-space coordinates
    void SavePlan(List<BrickPose> plan, int perLayer)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var json = new StringBuilder();
        json.Append("{\n  \"pattern\": \"rectangle\",\n");
        json.AppendFormat(inv, "  \"layers\": {0},\n  \"bricksPerLayer\": {1},\n", layers, perLayer);
        json.Append("  \"frame\": \"table: origin = middle of the front edge on the table top, x right, y up, z away from the front edge, metres\",\n");
        json.Append("  \"bricks\": [\n");
        for (int i = 0; i < plan.Count; i++)
        {
            var b = plan[i];
            json.AppendFormat(inv,
                "    {{ \"order\": {0}, \"layer\": {1}, \"x\": {2:F4}, \"y\": {3:F4}, \"z\": {4:F4}, \"yaw_deg\": {5:F1} }}{6}\n",
                i + 1, b.layer + 1, b.position.x, b.position.y, b.position.z, b.yawDeg, i < plan.Count - 1 ? "," : "");
        }
        json.Append("  ]\n}\n");

        string path = Path.Combine(Application.persistentDataPath, "wall_plan.json");
        File.WriteAllText(path, json.ToString());
        Debug.Log($"PicoBricks: wall {perLayer} x {layers} built, plan saved to {path}\n{json}");
    }

    // ---------- build area, curve and label ----------

    // The table minus a margin. Without a ghost robot the tray (front-left corner) is cut off on the left as before;
    // with one, the tray sits next to the robot and OnTray / the reach check refuse walls there instead.
    void GetBuildArea(out float minX, out float maxX, out float minZ, out float maxZ)
    {
        float halfWidth = table.localScale.x / 2f;
        minX = -halfWidth + edgeMargin;
        if (tray != null && robot == null) minX = Mathf.Max(minX, tray.localPosition.x + traySize.x + edgeMargin);
        maxX = halfWidth - edgeMargin;
        minZ = edgeMargin;
        maxZ = table.localScale.z - edgeMargin;
    }

    // Is this table point on (or too close to) the pickup tray?
    bool OnTray(Vector3 p)
    {
        if (tray == null) return false;
        Vector3 t = tray.localPosition;
        return p.x > t.x - edgeMargin && p.x < t.x + traySize.x + edgeMargin &&
               p.z > t.z - edgeMargin && p.z < t.z + traySize.y + edgeMargin;
    }

    // Orange arc on the table: how far the robot can reach (the part of that circle that lies on the table)
    void DrawReach()
    {
        reach.positionCount = 0;
        if (robot == null) return;
        GetBuildArea(out float minX, out float maxX, out float minZ, out float maxZ);
        bool Inside(Vector3 p) => p.x >= minX && p.x <= maxX && p.z >= minZ && p.z <= maxZ;
        Vector3 Arc(float deg) => robot.baseInWorkspace +
            new Vector3(Mathf.Cos(deg * Mathf.Deg2Rad), 0f, Mathf.Sin(deg * Mathf.Deg2Rad)) * robot.maxReach;

        // Start at a point of the circle that is off the table, so the part on the table comes out in one piece
        float start = 0f;
        for (float a = 0f; a < 360f; a += 2f) if (!Inside(Arc(a))) { start = a; break; }
        var pts = new List<Vector3>();
        for (float a = start; a <= start + 360f; a += 2f)
        {
            var p = Arc(a);
            if (Inside(p)) pts.Add(workspace.TransformPoint(new Vector3(p.x, 0.002f, p.z)));
            else if (pts.Count > 0) break;
        }
        reach.positionCount = pts.Count;
        reach.SetPositions(pts.ToArray());
    }

    void DrawBuildArea()
    {
        DrawReach();
        GetBuildArea(out float minX, out float maxX, out float minZ, out float maxZ);
        float y = 0.002f;
        zone.positionCount = 4;
        zone.SetPosition(0, workspace.TransformPoint(new Vector3(minX, y, minZ)));
        zone.SetPosition(1, workspace.TransformPoint(new Vector3(maxX, y, minZ)));
        zone.SetPosition(2, workspace.TransformPoint(new Vector3(maxX, y, maxZ)));
        zone.SetPosition(3, workspace.TransformPoint(new Vector3(minX, y, maxZ)));
    }

    void ShowCurve(List<Vector3> pts)
    {
        line.positionCount = pts.Count;
        for (int i = 0; i < pts.Count; i++)
            line.SetPosition(i, workspace.TransformPoint(pts[i] + Vector3.up * 0.003f));
    }

    LineRenderer MakeLine(string name, Material material, float width, bool loop)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = material;
        lr.widthMultiplier = width;
        lr.loop = loop;
        lr.useWorldSpace = true;
        lr.positionCount = 0;
        lr.numCornerVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    TextMesh MakeLabel()
    {
        var go = new GameObject("Wall Instructions");
        go.transform.SetParent(transform, false);
        var tm = go.AddComponent<TextMesh>();
        tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
        tm.fontSize = 48;
        tm.characterSize = 0.005f;
        tm.anchor = TextAnchor.LowerCenter;
        tm.alignment = TextAlignment.Center;
        return tm;
    }

    // Floats above the front edge of the table, facing you
    void PlaceLabel()
    {
        label.transform.position = workspace.TransformPoint(new Vector3(0f, 0.25f, 0f));
        var cam = Camera.main;
        if (cam != null)
            label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position);
    }

    void Say(string text)
    {
        if (label != null) label.text = text;
    }
}
