using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// Version 2, iteration 1 (rectangle): draw a line on the table, choose 1-3 layers, and the 6 bricks
// build a straight wall along the line (6 x 1, 3 x 2 or 2 x 3).
//
// Right controller:  point at the table + hold TRIGGER = draw the line
//                    THUMBSTICK left/right            = number of layers (1-3)
//                    A                                = OK, build the wall
// The line's start point is a corner of the wall; the wall follows the line's angle and sits on the
// side of the line away from you, so the drawn line stays visible along the wall's front face.
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

    [Header("Bricks")]
    public Vector3 brickSize = new Vector3(0.05f, 0.05f, 0.10f); // width, height, length (m)
    public int maxLayers = 3;

    [Header("Build area")]
    [Tooltip("The wall must stay this far inside the table edges (m)")]
    public float edgeMargin = 0.03f;
    [Tooltip("Lines shorter than this are ignored (m)")]
    public float minLineLength = 0.05f;

    enum Step { Draw, Drawing, ChooseLayers, Built }
    Step step = Step.Draw;

    Vector3 lineStart, lineEnd;     // in workspace (table) space, y = 0
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

        line = MakeLine("Wall Line", lineMaterial, 0.006f, false);
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
        line.positionCount = 0;
        DrawBuildArea();
        Say("Point at the table and hold the TRIGGER\nto draw where the wall goes");
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
        // Remember button states every frame, so a trigger still held from calibration doesn't start a line
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
                if (triggerPressed && PointOnTable(out lineStart))
                {
                    if (step == Step.Built && controls != null) controls.ResetBricks(); // bricks back to the start row
                    lineEnd = lineStart;
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
                if (PointOnTable(out var p)) lineEnd = p;
                ShowLine();
                if (triggerReleased) FinishLine();
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

    void FinishLine()
    {
        if (Vector3.Distance(lineStart, lineEnd) < minLineLength)
        {
            step = Step.Draw;
            line.positionCount = 0;
            Say("That line was too short, draw again\n(hold the TRIGGER while pointing at the table)");
            return;
        }
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
            $"{perLayer} bricks long x {layers} high = {perLayer * brickSize.z * 100f:F0} cm wall\n" +
            "A = OK      TRIGGER = draw again");
    }

    // ---------- building ----------

    void Build()
    {
        var plan = PlanRectangle(layers);
        if (plan == null)
        {
            Say($"A {BrickCount / layers}-brick wall doesn't fit on the table there.\n" +
                "Draw again (TRIGGER) or fewer bricks per layer (more layers)");
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

        step = Step.Built;
        int perLayer = BrickCount / layers;
        Say($"Wall built: {perLayer} x {layers}\nTRIGGER = draw a new wall      X = bricks back");
        SavePlan(plan, perLayer);
    }

    struct BrickPose
    {
        public int layer, index;
        public Vector3 position;   // centre, table space (m)
        public Quaternion rotation;
        public float yawDeg;
    }

    // Rectangle: every layer has the same number of bricks, end to end along the line, stacked straight up.
    // Returns null if the wall would stick out of the build area.
    List<BrickPose> PlanRectangle(int layerCount)
    {
        int perLayer = BrickCount / layerCount;
        Vector3 dir = lineEnd - lineStart;
        dir.y = 0f;
        dir.Normalize();

        // Side of the line away from the viewer, so the line stays visible along the wall's front face
        Vector3 side = Vector3.Cross(Vector3.up, dir);
        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 viewer = workspace.InverseTransformPoint(cam.transform.position) - lineStart;
            if (Vector3.Dot(side, viewer) > 0f) side = -side;
        }

        var rotation = Quaternion.LookRotation(dir, Vector3.up); // brick length (local z) along the line
        float yaw = rotation.eulerAngles.y;

        GetBuildArea(out float minX, out float maxX, out float minZ, out float maxZ);
        float tol = 0.001f;
        Vector3 wallEnd = lineStart + dir * (perLayer * brickSize.z);
        foreach (var corner in new[] { lineStart, wallEnd, lineStart + side * brickSize.x, wallEnd + side * brickSize.x })
            if (corner.x < minX - tol || corner.x > maxX + tol || corner.z < minZ - tol || corner.z > maxZ + tol)
                return null;

        var plan = new List<BrickPose>();
        for (int layer = 0; layer < layerCount; layer++)
            for (int i = 0; i < perLayer; i++)
            {
                Vector3 centre = lineStart
                                 + dir * (brickSize.z * (i + 0.5f))
                                 + side * (brickSize.x * 0.5f);
                centre.y = brickSize.y * (layer + 0.5f) + 0.0005f;
                plan.Add(new BrickPose { layer = layer, index = i, position = centre, rotation = rotation, yawDeg = yaw });
            }
        return plan;
    }

    // The brick list for the robot: build order (bottom layer first, along the line), table-space coordinates
    void SavePlan(List<BrickPose> plan, int perLayer)
    {
        var json = new StringBuilder();
        json.Append("{\n  \"pattern\": \"rectangle\",\n");
        json.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
            "  \"layers\": {0},\n  \"bricksPerLayer\": {1},\n", layers, perLayer);
        json.Append("  \"frame\": \"table: origin = middle of the front edge on the table top, x right, y up, z away from the front edge, metres\",\n");
        json.Append("  \"bricks\": [\n");
        for (int i = 0; i < plan.Count; i++)
        {
            var b = plan[i];
            json.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "    {{ \"order\": {0}, \"layer\": {1}, \"x\": {2:F4}, \"y\": {3:F4}, \"z\": {4:F4}, \"yaw_deg\": {5:F1} }}{6}\n",
                i + 1, b.layer + 1, b.position.x, b.position.y, b.position.z, b.yawDeg, i < plan.Count - 1 ? "," : "");
        }
        json.Append("  ]\n}\n");

        string path = Path.Combine(Application.persistentDataPath, "wall_plan.json");
        File.WriteAllText(path, json.ToString());
        Debug.Log($"PicoBricks: wall {perLayer} x {layers} built, plan saved to {path}\n{json}");
    }

    // ---------- build area, line and label ----------

    void GetBuildArea(out float minX, out float maxX, out float minZ, out float maxZ)
    {
        float halfWidth = table.localScale.x / 2f - edgeMargin;
        minX = -halfWidth;
        maxX = halfWidth;
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

    void ShowLine()
    {
        line.positionCount = 2;
        line.SetPosition(0, workspace.TransformPoint(lineStart + Vector3.up * 0.003f));
        line.SetPosition(1, workspace.TransformPoint(lineEnd + Vector3.up * 0.003f));
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
