using System;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.XR;

// Puts the virtual table (and its bricks) on top of a real table by touching points with the right controller tip.
//  - Measured table (no robot): 4 points, front-left corner, front-right corner, back-left corner, then the floor.
//  - LAMA table (fixedSize, with the ghost robot): 2 points, the FRONT-RIGHT corner (next to the robot) and any point
//    further left on the front edge. The table always gets the real LAMA size, anchored at that right corner, so robot,
//    tray and drawing area sit where they will be on the real table, even when testing on a smaller table at home.
// STAFF ONLY: home screen > Settings > A (HomeMenu calls Begin), trigger = record a point, B = cancel. The result is
// saved on the headset and loaded every time the app starts, so visitors never calibrate (and never see it).
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
    bool triggerWasDown;

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
        LabelPanel.Add(label);   // dark see-through panel behind the text (readability)
        label.text = "";

        // Visitors never calibrate: staff place the table once (Settings), it is saved on the headset and loaded here
        LoadSaved();
        StartAnchors();
    }

    // ---------- spatial anchor: keeps the table in the same spot in the ROOM across app restarts (25 Oct) ----------
    // The saved numbers above are relative to the headset's tracking origin, which can move between sessions (Jin had
    // to recalibrate after restarting). A PICO spatial anchor is stored by the headset with its map of the room, so the
    // table is put back where it really is. The numbers stay as a fallback until the anchor is found.
    const string AnchorKey = SaveKey + "anchor";
    bool anchorsReady, justPlaced;
    ulong anchorHandle, oldAnchorHandle;
    float nextLocate;

    // Anchors are in tracking space (the XR rig's Camera Offset); the table is placed in world space
    Transform TrackingSpace => Camera.main != null ? Camera.main.transform.parent : null;

    async void StartAnchors()
    {
        try
        {
            var started = await PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor);
            anchorsReady = started == PxrResult.SUCCESS;
            Debug.Log("PicoBricks: spatial anchors " + started);
            if (!anchorsReady || !Guid.TryParse(PlayerPrefs.GetString(AnchorKey, ""), out var uuid)) return;
            var found = await PXR_MixedReality.QuerySpatialAnchorAsync(new[] { uuid });
            if (found.result == PxrResult.SUCCESS && found.anchorHandleList.Count > 0)
            {
                anchorHandle = found.anchorHandleList[0];
                Debug.Log("PicoBricks: table anchor loaded");
            }
            else Debug.LogWarning("PicoBricks: table anchor not found (" + found.result + "), using the saved numbers");
        }
        catch (Exception e) { Debug.LogWarning("PicoBricks: spatial anchors unavailable: " + e.Message); }
    }

    async void SaveAnchor(Vector3 worldPosition, Quaternion worldRotation)
    {
        // Until the new anchor is saved, the old one must not come back at the next start (it would undo this calibration)
        PlayerPrefs.DeleteKey(AnchorKey);
        PlayerPrefs.Save();
        if (!anchorsReady) return;
        try
        {
            var space = TrackingSpace;
            var pos = space != null ? space.InverseTransformPoint(worldPosition) : worldPosition;
            var rot = space != null ? Quaternion.Inverse(space.rotation) * worldRotation : worldRotation;
            var made = await PXR_MixedReality.CreateSpatialAnchorAsync(pos, rot);
            if (made.result != PxrResult.SUCCESS) { Debug.LogWarning("PicoBricks: anchor not created: " + made.result); return; }
            var kept = await PXR_MixedReality.PersistSpatialAnchorAsync(made.anchorHandle);
            if (kept != PxrResult.SUCCESS) { Debug.LogWarning("PicoBricks: anchor not saved: " + kept); return; }
            if (oldAnchorHandle != 0)   // forget the previous calibration's anchor
            {
                await PXR_MixedReality.UnPersistSpatialAnchorAsync(oldAnchorHandle);
                PXR_MixedReality.DestroyAnchor(oldAnchorHandle);
                oldAnchorHandle = 0;
            }
            anchorHandle = made.anchorHandle;
            PlayerPrefs.SetString(AnchorKey, made.uuid.ToString());
            PlayerPrefs.Save();
            Debug.Log("PicoBricks: table saved as a spatial anchor " + made.uuid);
        }
        catch (Exception e) { Debug.LogWarning("PicoBricks: anchor not saved: " + e.Message); }
    }

    void RestoreOldAnchor()
    {
        if (anchorHandle == 0 && oldAnchorHandle != 0) { anchorHandle = oldAnchorHandle; oldAnchorHandle = 0; }
    }

    // Twice a second: put the table where the anchor is now (also corrects a re-centred headset). Kept upright.
    void FollowAnchor()
    {
        if (anchorHandle == 0 || IsCalibrating || Time.time < nextLocate) return;
        nextLocate = Time.time + 0.5f;
        if (PXR_MixedReality.LocateAnchor(anchorHandle, out var pos, out var rot) != PxrResult.SUCCESS) return;
        if (pos == Vector3.zero && rot.w == 0f && rot.x == 0f && rot.y == 0f && rot.z == 0f) return;   // not tracked yet
        var space = TrackingSpace;
        var worldPos = space != null ? space.TransformPoint(pos) : pos;
        var forward = (space != null ? space.rotation * rot : rot) * Vector3.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-6f) return;
        var worldRot = Quaternion.LookRotation(forward.normalized, Vector3.up);
        if (Vector3.Distance(worldPos, workspace.position) > 0.003f || Quaternion.Angle(worldRot, workspace.rotation) > 0.3f)
            workspace.SetPositionAndRotation(worldPos, worldRot);
    }

    /// <summary>Start calibrating (Settings page, A).</summary>
    public void Begin()
    {
        // Stop following the old anchor while measuring (it is replaced once the new table is placed)
        if (anchorHandle != 0) { oldAnchorHandle = anchorHandle; anchorHandle = 0; }
        step = 0;
        label.text = "Table calibration   (B = cancel)\n" + CurrentPrompts[0];
    }

    /// <summary>Stop without changing the table (B, or leaving the settings).</summary>
    public void Cancel()
    {
        RestoreOldAnchor();
        step = -1;
        label.text = "";
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
        FollowAnchor();
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
        right.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerDown);

        if (triggerDown && !triggerWasDown && step >= 0)
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
                justPlaced = false;
                if (fixedSize) PlaceFixed(points[0], points[1]);
                else Place(points[0], points[1], points[2], points[3]);
                if (!justPlaced) RestoreOldAnchor();   // points rejected: keep following the old anchor (the message stays)
            }
        }

        triggerWasDown = triggerDown;
    }

    void Place(Vector3 frontLeft, Vector3 frontRight, Vector3 backLeft, Vector3 floorPoint)
    {
        Vector3 edge = frontRight - frontLeft;
        edge.y = 0f;
        if (edge.magnitude < 0.1f)
        {
            label.text = "Front corners too close together\nA = try again";
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
            label.text = "That didn't look like a table\n(check the corner order)  A = try again";
            return;
        }

        Vector3 frontMiddle = (frontLeft + frontRight) / 2f;
        frontMiddle.y = top;
        Apply(frontMiddle, forward, width, depth, height,
              $"Table placed ({width * 100f:F0} x {depth * 100f:F0} cm, {height * 100f:F0} cm high)\nand saved on the headset. A = redo, B = back");
    }

    // LAMA table: known size; the front-right corner fixes the position and height, the second point the direction
    void PlaceFixed(Vector3 frontRight, Vector3 onFrontEdge)
    {
        Vector3 edge = frontRight - onFrontEdge;
        edge.y = 0f;
        if (edge.magnitude < 0.2f)
        {
            label.text = "Points too close together: touch the front edge\nat least 20 cm left of the corner.  A = try again";
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
              $"Table placed ({width * 100f:F0} x {depth * 100f:F0} cm)\nand saved on the headset. A = redo, B = back");
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

        if (save)
        {
            Save(frontMiddle, forward, width, depth, height);
            SaveAnchor(workspace.position, workspace.rotation);
            justPlaced = true;
        }
        // Staff message, then the controller is clean again for the visitor
        label.text = message;
        if (!string.IsNullOrEmpty(message)) StartCoroutine(ClearLabelAfter(6f));
        Debug.Log($"PicoBricks: table placed at {frontMiddle}, {width:F2} x {depth:F2} m, height {height:F2} m");
        Placed?.Invoke();
    }
}
