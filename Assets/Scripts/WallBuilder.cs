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
//  4. walk along it in steps of one brick length: each step is the direction of one brick
//  5. neighbouring bricks always touch at one corner: the corner on the inside of the bend, so they never
//     overlap (a wedge-shaped gap opens on the outside of the bend)
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
    LineRenderer line, zone;
    TextMesh label;

    int BrickCount => bricks.Count;

    void Start()
    {
        foreach (var rb in bricksParent.GetComponentsInChildren<Rigidbody>())
            bricks.Add(rb);

        line = MakeLine("Wall Curve", lineMaterial, 0.006f, false);
        zone = MakeLine("Build Area", zoneMaterial != null ? zoneMaterial : lineMaterial, 0.003f, true);
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
            Say($"A {BrickCount / layers}-brick wall along that curve doesn't fit on the table.\n" +
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

        // 4. walk along it in brick-length steps -> one direction per brick
        var dirs = ChordDirections(wallCurve, L, perLayer);

        // The wall body sits on the side of the curve away from the viewer (decided at the start),
        // so the drawn curve stays visible along the wall's front
        float sideSign = 1f;
        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 viewer = workspace.InverseTransformPoint(cam.transform.position) - curve[0];
            if (Vector3.Dot(Vector3.Cross(Vector3.up, dirs[0]), viewer) > 0f) sideSign = -1f;
        }
        Vector3 Side(Vector3 d) => Vector3.Cross(Vector3.up, d) * sideSign;

        // 5. chain the bricks; each one touches the previous at the corner on the inside of the bend
        var chain = new List<(Vector3 frontStart, Vector3 dir)>();
        Vector3 frontStart = wallCurve[0];
        for (int i = 0; i < perLayer; i++)
        {
            Vector3 d = dirs[i];
            if (i > 0)
            {
                var (prevStart, prevDir) = chain[i - 1];
                Vector3 prevFrontEnd = prevStart + prevDir * L;
                bool bendsTowardBody = Vector3.Dot(d, Side(prevDir)) > 0f;
                frontStart = bendsTowardBody
                    // inside of the bend is the back: share the back corner
                    ? prevFrontEnd + Side(prevDir) * W - Side(d) * W
                    // inside of the bend is the front: share the front corner
                    : prevFrontEnd;
            }
            chain.Add((frontStart, d));
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
                    if (c.x < minX - tol || c.x > maxX + tol || c.z < minZ - tol || c.z > maxZ + tol)
                        return null;
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

    // Steps of straight length L along the polyline, starting at its first point. If the curve runs out
    // (chords are shorter than the arc), the remaining bricks continue in the last direction.
    static List<Vector3> ChordDirections(List<Vector3> pts, float L, int count)
    {
        var dirs = new List<Vector3>();
        Vector3 current = pts[0];
        int seg = 0;
        Vector3 lastDir = (pts[pts.Count - 1] - pts[0]).normalized;
        for (int i = 0; i < count; i++)
        {
            bool found = false;
            for (; seg < pts.Count - 1; seg++)
            {
                // first point on segment seg..seg+1 that is exactly L away from `current`
                if (IntersectSegmentCircle(pts[seg], pts[seg + 1], current, L, out var hit))
                {
                    Vector3 d = (hit - current).normalized;
                    dirs.Add(d);
                    lastDir = d;
                    current = hit;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                dirs.Add(lastDir);
                current += lastDir * L;
            }
        }
        return dirs;
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

    // The table minus a margin, and right of the pickup tray (which sits at the front-left corner)
    void GetBuildArea(out float minX, out float maxX, out float minZ, out float maxZ)
    {
        float halfWidth = table.localScale.x / 2f;
        minX = -halfWidth + edgeMargin;
        if (tray != null) minX = Mathf.Max(minX, tray.localPosition.x + traySize.x + edgeMargin);
        maxX = halfWidth - edgeMargin;
        minZ = edgeMargin;
        maxZ = table.localScale.z - edgeMargin;
    }

    void DrawBuildArea()
    {
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
