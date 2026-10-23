using UnityEngine;
using UnityEngine.XR;

// Puts the virtual table (and its bricks) on top of a real table by touching points with the right controller tip.
//  - Measured table (no robot): 4 points, front-left corner, front-right corner, back-left corner, then the floor.
//  - LAMA table (fixedSize, with the ghost robot): 2 points, the FRONT-RIGHT corner (next to the robot) and any point
//    further left on the front edge. The table always gets the real LAMA size, anchored at that right corner, so robot,
//    tray and drawing area sit where they will be on the real table, even when testing on a smaller table at home.
// STAFF ONLY: hold B (right controller) for 3 seconds to start, trigger = record a point. The result is saved on the
// headset and loaded every time the app starts, so visitors never calibrate.
public class TableCalibration : MonoBehaviour
{
    [Tooltip("Parent of Table, Bricks and Floor; its origin is the middle of the table's front edge, on the top surface")]
    public Transform workspace;
    public Transform table;
    public Transform floor;
    public Transform rightController;
    public BrickControls bricks;
    public Passthrough passthrough;
    [Tooltip("Pickup tray (with the bricks): put at the table's front-left corner after calibration")]
    public Transform tray;
    [Tooltip("Tray's front-left corner, measured from the table's front-left corner (or from the middle of the front edge, see trayFromOrigin), x right, y away, m")]
    public Vector2 trayOffset = Vector2.zero;
    [Tooltip("Measure trayOffset from the middle of the front edge (the table frame's origin) instead of the front-left " +
             "corner. On with the ghost robot: the robot is placed from that middle too, so the tray keeps its distance " +
             "to the robot whatever table width the calibration measures")]
    public bool trayFromOrigin;

    [Header("LAMA table: known size, 2-point calibration")]
    [Tooltip("On: the table has a known size (fixedTableSize) and is placed from its front-right corner + the front edge")]
    public bool fixedSize;
    [Tooltip("Width (along the front edge), height (floor to top), depth (front to back), m")]
    public Vector3 fixedTableSize = new Vector3(1.426f, 0.912f, 0.611f);

    [Tooltip("How far in front of the controller the measuring tip sits (m)")]
    public float tipOffset = 0.06f;

    static readonly string[] Prompts =
    {
        "1/4  Touch the FRONT-LEFT corner\nof the table, pull trigger",
        "2/4  Touch the FRONT-RIGHT corner,\npull trigger",
        "3/4  Touch the BACK-LEFT corner,\npull trigger",
        "4/4  Touch the FLOOR,\npull trigger",
    };

    static readonly string[] FixedPrompts =
    {
        "1/2  ORANGE BALL resting ON the corner at the ROBOT'S END,\non the long side with the LAMA logo. Pull trigger",
        "2/2  ORANGE BALL resting ON that SAME long edge (LAMA logo side),\nas far from the robot as you can. Pull trigger",
    };

    string[] CurrentPrompts => fixedSize ? FixedPrompts : Prompts;

    readonly Vector3[] points = new Vector3[4];
    int step = -1; // -1 = not calibrating
    bool bWasDown, triggerWasDown;
    float bDownSince;
    bool staffHoldUsed;
    const float StaffHoldSeconds = 3f;

    /// <summary>True while the user is touching calibration points (other tools should ignore the trigger).</summary>
    public bool IsCalibrating => step >= 0;

    /// <summary>Raised after the table has been placed on the real table.</summary>
    public event System.Action Placed;

    [Tooltip("Bright unlit material for the measuring ball and its line (easy to see in passthrough)")]
    public Material tipMaterial;

    Transform tip;
    LineRenderer tipLine;
    TextMesh label;

    Vector3 TipPosition => rightController.position + rightController.forward * tipOffset;
    const float TipRadius = 0.01f; // the orange ball is 2 cm wide

    void Start()
    {
        // Orange ball that shows exactly which point gets measured (its centre), joined to the controller by a short
        // line so it is easy to find. Only shown while calibrating. (The small white ball was hard to see, 21 Oct.)
        tip = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
        tip.name = "Calibration Tip";
        Destroy(tip.GetComponent<Collider>());
        tip.localScale = Vector3.one * (2f * TipRadius);
        if (tipMaterial != null) tip.GetComponent<Renderer>().sharedMaterial = tipMaterial;
        tipLine = new GameObject("Calibration Tip Line").AddComponent<LineRenderer>();
        tipLine.sharedMaterial = tipMaterial != null ? tipMaterial : tip.GetComponent<Renderer>().sharedMaterial;
        tipLine.widthMultiplier = 0.003f;
        tipLine.positionCount = 2;
        tipLine.useWorldSpace = true;
        tipLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var labelGo = new GameObject("Calibration Label");
        labelGo.transform.SetParent(rightController, false);
        labelGo.transform.localPosition = new Vector3(0f, 0.07f, 0.02f);
        label = labelGo.AddComponent<TextMesh>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        labelGo.GetComponent<MeshRenderer>().sharedMaterial = label.font.material;
        label.fontSize = 48;
        label.characterSize = 0.004f;
        label.anchor = TextAnchor.LowerCenter;
        label.alignment = TextAlignment.Center;
        label.text = "";

        // Visitors never calibrate: staff place the table once, it is saved on the headset and loaded here
        if (!LoadSaved())
            label.text = "STAFF: hold B for 3 seconds\nto place the table";
    }

    System.Collections.IEnumerator ClearLabelAfter(float seconds)
    {
        string shown = label.text;
        yield return new WaitForSeconds(seconds);
        if (label.text == shown && !IsCalibrating) label.text = "";
    }

    // ---------- saving the table position on the headset (staff calibrate once) ----------
    const string SaveKey = "picobricks.table.";

    void Save(Vector3 frontMiddle, Vector3 forward, float width, float depth, float height)
    {
        float[] v = { frontMiddle.x, frontMiddle.y, frontMiddle.z, forward.x, forward.y, forward.z, width, depth, height };
        for (int i = 0; i < v.Length; i++) PlayerPrefs.SetFloat(SaveKey + i, v[i]);
        PlayerPrefs.SetInt(SaveKey + "saved", 1);
        PlayerPrefs.Save();
    }

    bool LoadSaved()
    {
        if (PlayerPrefs.GetInt(SaveKey + "saved", 0) != 1) return false;
        var v = new float[9];
        for (int i = 0; i < v.Length; i++) v[i] = PlayerPrefs.GetFloat(SaveKey + i);
        Apply(new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]), v[6], v[7], v[8], "", save: false);
        Debug.Log("PicoBricks: table position loaded from the headset (staff calibration)");
        return true;
    }

    void Update()
    {
        if (rightController == null) return;
        tip.position = TipPosition;
        tip.gameObject.SetActive(IsCalibrating);
        tipLine.enabled = IsCalibrating;
        tipLine.SetPosition(0, rightController.position);
        tipLine.SetPosition(1, TipPosition);

        // Keep the label readable: face the headset
        var cam = Camera.main;
        if (cam != null)
            label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position);

        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!right.isValid) return;
        right.TryGetFeatureValue(CommonUsages.secondaryButton, out bool bDown);
        right.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerDown);

        // Staff mode: hold B for 3 seconds (visitors don't); a short press does nothing
        if (bDown && !bWasDown) bDownSince = Time.time;
        bool staffHold = bDown && Time.time - bDownSince >= StaffHoldSeconds && !staffHoldUsed;
        if (!bDown) staffHoldUsed = false;
        if (staffHold)
        {
            staffHoldUsed = true;
            step = 0;
            label.text = "STAFF: table calibration\n" + CurrentPrompts[0];
        }
        else if (triggerDown && !triggerWasDown && step >= 0)
        {
            points[step] = TipPosition;
            step++;
            if (step < CurrentPrompts.Length)
            {
                label.text = CurrentPrompts[step];
            }
            else
            {
                step = -1;
                if (fixedSize) PlaceFixed(points[0], points[1]);
                else Place(points[0], points[1], points[2], points[3]);
            }
        }

        bWasDown = bDown;
        triggerWasDown = triggerDown;
    }

    void Place(Vector3 frontLeft, Vector3 frontRight, Vector3 backLeft, Vector3 floorPoint)
    {
        Vector3 edge = frontRight - frontLeft;
        edge.y = 0f;
        if (edge.magnitude < 0.1f)
        {
            label.text = "Front corners too close together\nhold B (3 s) = try again";
            return;
        }

        Vector3 right = edge.normalized;
        Vector3 forward = Vector3.Cross(right, Vector3.up); // away from you, into the table

        float width = edge.magnitude;
        float depth = Vector3.Dot(backLeft - frontLeft, forward);
        float top = (frontLeft.y + frontRight.y + backLeft.y) / 3f;
        float height = top - floorPoint.y;
        if (depth < 0.1f || height < 0.1f)
        {
            label.text = "That didn't look like a table\n(check the corner order)  hold B (3 s) = try again";
            return;
        }

        Vector3 frontMiddle = (frontLeft + frontRight) / 2f;
        frontMiddle.y = top;
        Apply(frontMiddle, forward, width, depth, height,
              $"STAFF: table placed ({width * 100f:F0} x {depth * 100f:F0} cm, {height * 100f:F0} cm high)\nand saved on the headset. Hold B (3 s) = redo");
    }

    // LAMA table: known size; the front-right corner fixes the position and height, the second point the direction
    void PlaceFixed(Vector3 frontRight, Vector3 onFrontEdge)
    {
        Vector3 edge = frontRight - onFrontEdge;
        edge.y = 0f;
        if (edge.magnitude < 0.2f)
        {
            label.text = "Points too close together: touch the front edge\nat least 20 cm left of the corner.  hold B (3 s) = try again";
            return;
        }
        Vector3 right = edge.normalized;
        Vector3 forward = Vector3.Cross(right, Vector3.up);
        float width = fixedTableSize.x, height = fixedTableSize.y, depth = fixedTableSize.z;
        Vector3 frontMiddle = frontRight - right * (width / 2f);
        // The ball rests ON the table: the table top is one ball radius below the measured ball centre
        // (without this the whole table, and every brick, sat 1 cm too high, 22 Oct)
        frontMiddle.y = (frontRight.y + onFrontEdge.y) / 2f - TipRadius;
        Apply(frontMiddle, forward, width, depth, height,
              $"STAFF: table placed ({width * 100f:F0} x {depth * 100f:F0} cm)\nand saved on the headset. Hold B (3 s) = redo");
    }

    void Apply(Vector3 frontMiddle, Vector3 forward, float width, float depth, float height, string message, bool save = true)
    {
        workspace.SetPositionAndRotation(frontMiddle, Quaternion.LookRotation(forward, Vector3.up));

        // Make the virtual table the same size as the real one, and put the floor where you touched it
        table.localPosition = new Vector3(0f, -height / 2f, depth / 2f);
        table.localScale = new Vector3(width, height, depth);
        if (floor != null) floor.localPosition = new Vector3(0f, -height, 0f);
        // Tray: at the front-left corner, or next to the robot (trayFromOrigin); before the reset, so the bricks go back in
        float trayFrom = trayFromOrigin ? 0f : -width / 2f;
        if (tray != null) tray.localPosition = new Vector3(trayFrom + trayOffset.x, 0f, trayOffset.y);

        if (bricks != null) bricks.ResetBricks();

        // You can see the real table now, so only show the virtual one in VR mode
        if (passthrough != null) passthrough.HideWhenOn(table.GetComponent<Renderer>());

        if (save) Save(frontMiddle, forward, width, depth, height);
        // Staff message, then the controller is clean again for the visitor
        label.text = message;
        if (!string.IsNullOrEmpty(message)) StartCoroutine(ClearLabelAfter(6f));
        Debug.Log($"PicoBricks: table placed at {frontMiddle}, {width:F2} x {depth:F2} m, height {height:F2} m");
        Placed?.Invoke();
    }
}
