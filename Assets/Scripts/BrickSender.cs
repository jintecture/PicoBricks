using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// Sends the brick layout to the laptop (robot-bridge/picobricks_bridge.py) so the UR5 can build it.
//
// Right controller A: with WallBuilder, A = "OK, build the wall". A few frames after A, this script sends
// every brick that is no longer in its tray pocket. Pressing A again on a built wall sends it again.
// Also works without WallBuilder (hand-placed bricks): A sends whatever you moved.
// No headset? In Play mode press S on the keyboard, or right-click the component > Send now.
//
// Adds ITSELF to the scene at startup: no scene edit needed (no SampleScene merge conflicts).
// Finds "Workspace" and the "Brick_*" rigidbodies under it by the names BrickSceneSetup.cs gives them.
//
// Sent per brick, both in the TABLE frame set by the calibration:
//   pick  = its tray pocket (= where the REAL brick waits in the printed tray)
//   place = where it is now (in the wall)
//   origin = middle of the table's front edge, on the top surface
//   x = right along the front edge, y = away from you, z = up (metres)
//   yaw = degrees around z, counter-clockwise seen from above; 0 = long side pointing away from you
//   standing = brick on its end (the robot can't build those yet; the bridge skips them)
// Plus `robot`: where the ghost UR5 stands, so the RoboDK simulation can stand in the same place.
// The bridge answers with the robot's movements on port 5006 (GhostPlayer.cs plays them on the ghost).
public class BrickSender : MonoBehaviour
{
    [Tooltip("Laptop IP. Windows Mobile Hotspot gives the laptop 192.168.137.1")]
    public string laptopIp = "192.168.137.1";
    public int port = 5005;
    [Tooltip("Also broadcast, in case the IP above is wrong (same Wi-Fi network)")]
    public bool alsoBroadcast = true;
    [Tooltip("A brick closer than this to its tray pocket counts as 'still in the tray' (m)")]
    public float movedThreshold = 0.02f;
    [Tooltip("Frames to wait after A, so WallBuilder has placed the bricks first")]
    public int framesAfterA = 3;

    class Start0 { public Transform t; public Transform parent; public Vector3 localPos; public Quaternion localRot; }

    Transform workspace;
    readonly List<Start0> starts = new();
    UdpClient udp;
    bool aWasDown;
    int seq;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<BrickSender>() == null)
            new GameObject("BrickSender").AddComponent<BrickSender>();
    }

    void Start()
    {
        var ws = GameObject.Find("Workspace");
        if (ws == null)
        {
            Debug.LogError("PicoBricks: BrickSender can't find Workspace; run PicoBricks > Setup Brick Scene");
            enabled = false;
            return;
        }
        workspace = ws.transform;

        // Remember each brick's start spot relative to its PARENT (the tray on the wall-builder version).
        // The tray is moved after calibration, so the pick spot is worked out again at send time.
        foreach (var rb in workspace.GetComponentsInChildren<Rigidbody>(true))
            if (rb.name.StartsWith("Brick"))
                starts.Add(new Start0 { t = rb.transform, parent = rb.transform.parent,
                                        localPos = rb.transform.localPosition, localRot = rb.transform.localRotation });
        starts.Sort((a, b) => string.CompareOrdinal(a.t.name, b.t.name));

        udp = new UdpClient { EnableBroadcast = true };
        Debug.Log($"PicoBricks: BrickSender ready, {starts.Count} bricks, target {laptopIp}:{port}");
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.sKey.wasPressedThisFrame) SendNow();
#endif
        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!right.isValid) return;
        right.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool aDown); // A
        if (aDown && !aWasDown) StartCoroutine(SendSoon(right));
        aWasDown = aDown;
    }

    IEnumerator SendSoon(UnityEngine.XR.InputDevice hand) // XR controller (InputSystem also has an InputDevice)
    {
        for (int i = 0; i < framesAfterA; i++) yield return null;
        int n = Send();
        if (n > 0) hand.SendHapticImpulse(0, 0.6f, 0.15f);      // short buzz = sent
        else if (n < 0) hand.SendHapticImpulse(0, 1f, 0.6f);    // long buzz = failed
    }

    [ContextMenu("Send now")]
    public void SendNow() => Send();

    // Yaw of a brick in the table frame: angle of its long axis (local z), 0 = pointing away from you, CCW positive
    float TableYaw(Quaternion worldRot, out bool standing)
    {
        Vector3 longAxis = workspace.InverseTransformDirection(worldRot * Vector3.forward);
        standing = Mathf.Abs(longAxis.y) > 0.7071f;
        Vector3 flat = standing ? workspace.InverseTransformDirection(worldRot * Vector3.right) : longAxis;
        flat.y = 0f;
        float unityYaw = flat.sqrMagnitude > 1e-6f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : 0f; // clockwise from above
        if (standing) unityYaw -= 90f;
        return Wrap180(-unityYaw);
    }

    Pose3 ToTable(Vector3 worldPos, Quaternion worldRot, out bool standing)
    {
        Vector3 p = workspace.InverseTransformPoint(worldPos);  // Unity: x right, y up, z away
        float yaw = TableYaw(worldRot, out standing);
        return new Pose3 { x = p.x, y = p.z, z = p.y, yaw = yaw };
    }

    static float Wrap180(float a) { a %= 360f; if (a > 180f) a -= 360f; if (a < -180f) a += 360f; return a; }

    // Returns number of bricks sent, 0 if none moved, -1 on a network error
    int Send()
    {
        if (workspace == null) return -1;
        var list = new List<BrickMsg>();
        for (int i = 0; i < starts.Count; i++)
        {
            var s = starts[i];
            Vector3 pickPos = s.parent.TransformPoint(s.localPos);
            Quaternion pickRot = s.parent.rotation * s.localRot;
            if (Vector3.Distance(s.t.position, pickPos) < movedThreshold) continue;

            var pick = ToTable(pickPos, pickRot, out _);
            var place = ToTable(s.t.position, s.t.rotation, out bool standing);
            list.Add(new BrickMsg { id = i + 1, name = s.t.name, standing = standing, pick = pick, place = place });
        }
        if (list.Count == 0)
        {
            Debug.Log("PicoBricks: nothing to send (all bricks are in the tray)");
            return 0;
        }

        // Build bottom-up; within a layer keep WallBuilder's order (Brick_1, Brick_2, ... along the curve)
        list = list.OrderBy(b => Mathf.Round(b.place.z * 100f)).ThenBy(b => b.id).ToList();

        var msg = new LayoutMsg { type = "layout", seq = ++seq, frame = "table", units = "m_deg", bricks = list.ToArray(),
                                  robot = GhostInfo() };
        byte[] data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(msg));
        try
        {
            udp.Send(data, data.Length, laptopIp, port);
            if (alsoBroadcast) udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, port));
            Debug.Log($"PicoBricks: sent {list.Count} bricks to {laptopIp}:{port} (seq {seq})\n{JsonUtility.ToJson(msg, true)}");
            return list.Count;
        }
        catch (Exception e)
        {
            Debug.LogError("PicoBricks: send failed: " + e.Message);
            return -1;
        }
    }

    void OnDestroy() => udp?.Close();

    /// <summary>Where this brick waits in the tray right now (world space). Used by GhostPlayer.</summary>
    public bool TryGetPickPose(string brickName, out Vector3 pos, out Quaternion rot)
    {
        foreach (var s in starts)
            if (s.t.name == brickName)
            {
                pos = s.parent.TransformPoint(s.localPos);
                rot = s.parent.rotation * s.localRot;
                return true;
            }
        pos = Vector3.zero;
        rot = Quaternion.identity;
        return false;
    }

    // The ghost UR5's base in the table frame, so the RoboDK simulation can put its robot in the same place and the
    // movements it sends back fit the ghost (see GhostPlayer.cs and the bridge's "ghost frame")
    RobotMsg GhostInfo()
    {
        var ghost = FindAnyObjectByType<GhostRobot>();
        if (ghost == null) return new RobotMsg { valid = false };
        Vector3 b = ghost.baseInWorkspace; // Unity: x right, y up, z away
        return new RobotMsg { valid = true, x = b.x, y = b.z, z = b.y, armYaw0 = ghost.ArmYaw(0f), armYaw90 = ghost.ArmYaw(90f),
                              check = ghost.CheckPositions() };
    }

    [Serializable] public class Pose3 { public float x, y, z, yaw; }
    [Serializable] public class BrickMsg { public int id; public string name; public bool standing; public Pose3 pick; public Pose3 place; }
    // armYaw0 / armYaw90: direction the arm points with base joint 0 / 90 deg and all other joints 0 (deg, CCW from x)
    // check: the ghost's hand position for GhostRobot.CheckPoses (x, y, z per pose, m, relative to the base)
    [Serializable] public class RobotMsg { public bool valid; public float x, y, z, armYaw0, armYaw90; public float[] check; }
    [Serializable] public class LayoutMsg { public string type; public int seq; public string frame; public string units; public BrickMsg[] bricks; public RobotMsg robot; }
}
