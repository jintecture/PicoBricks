using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

// Plays the robot movements that RoboDK worked out for the wall, on the ghost UR5 (route B, see docs/DEVLOG.md):
//   headset (A) --bricks--> laptop: RoboDK plans every joint angle --movements, UDP 5006--> here
// The bricks first go back into the tray, then the ghost picks them up one by one and puts them in the wall,
// exactly as the real robot would. Added by GhostRobot at startup.
public class GhostPlayer : MonoBehaviour
{
    public int port = 5006;
    [Tooltip("How fast the ghost turns its joints (degrees per second)")]
    public float speed = 90f;
    [Tooltip("Pause when the gripper closes or opens (s)")]
    public float gripPause = 0.3f;

    GhostRobot robot;
    BrickSender sender;
    UdpClient udp;
    TextMesh label;

    Frame[] frames;
    int next;            // frame we are moving towards
    float t, duration;   // time into the current move
    float[] from = new float[6];
    float pause;

    readonly Dictionary<string, Transform> bricks = new Dictionary<string, Transform>();
    readonly Dictionary<string, (Vector3 pos, Quaternion rot)> wallPoses = new Dictionary<string, (Vector3, Quaternion)>();
    Transform carried;
    Matrix4x4 carryOffset;   // carried brick relative to the flange
    int bricksDone, bricksTotal;

    void Start()
    {
        robot = GetComponent<GhostRobot>();
        sender = FindAnyObjectByType<BrickSender>();
        var controls = FindAnyObjectByType<BrickControls>();
        if (controls != null) controls.BricksReset += Abandon;
        foreach (var rb in transform.parent.GetComponentsInChildren<Rigidbody>(true))
            if (rb.name.StartsWith("Brick")) bricks[rb.name] = rb.transform;
        label = MakeLabel();
        try
        {
            udp = new UdpClient(port);
            Debug.Log($"PicoBricks: GhostPlayer listening for robot movements on UDP {port}");
        }
        catch (Exception e)
        {
            Debug.LogError("PicoBricks: GhostPlayer can't listen: " + e.Message);
        }
    }

    void OnDestroy() => udp?.Close();

    void Update()
    {
        Receive();
        PlaceLabel();
        if (frames == null) return;

        if (pause > 0f) { pause -= Time.deltaTime; return; }
        t += Time.deltaTime;
        float k = duration > 0f ? Mathf.Clamp01(t / duration) : 1f;
        var to = frames[next].j;
        var q = new float[6];
        for (int i = 0; i < 6; i++) q[i] = Mathf.Lerp(from[i], to[i], k);
        robot.SetJointsDegrees(q);
        if (k < 1f) return;

        Arrived(frames[next]);
        next++;
        if (next >= frames.Length) { Finish(); return; }
        BeginMove();
    }

    // Bricks follow the gripper after the arm has moved this frame
    void LateUpdate()
    {
        if (carried == null) return;
        var m = robot.Flange.localToWorldMatrix * carryOffset;
        carried.SetPositionAndRotation(m.GetColumn(3), m.rotation);
    }

    // ---------- receiving ----------

    void Receive()
    {
        if (udp == null) return;
        while (udp.Available > 0)
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            byte[] data;
            try { data = udp.Receive(ref ep); }
            catch (Exception e) { Debug.LogWarning("PicoBricks: GhostPlayer receive: " + e.Message); return; }
            PreviewMsg msg;
            try { msg = JsonUtility.FromJson<PreviewMsg>(Encoding.UTF8.GetString(data)); }
            catch (Exception) { continue; }
            if (msg == null) continue;
            // From the laptop bridge: a voice line (e.g. "real_start" just before the real UR5 moves), or R in the
            // bridge window = back to the opening for the next visitor
            if (msg.type == "say") { VoiceGuide.Play(msg.key); continue; }
            if (msg.type == "restart") { HomeMenu.RestartFromIntro(); continue; }
            if (msg.type != "preview") continue;
            if (!string.IsNullOrEmpty(msg.error))
            {
                Stop();
                Debug.LogWarning("PicoBricks: robot preview failed: " + msg.error); // details for staff (log / bridge window)
                label.text = "Something went wrong\nTRIGGER = draw your wall again";
                VoiceGuide.Play("error");
                continue;
            }
            if (msg.frames != null && msg.frames.Length > 0) Play(msg);
        }
    }

    // ---------- playing ----------

    void Play(PreviewMsg msg)
    {
        Stop();
        frames = msg.frames;
        Debug.Log($"PicoBricks: playing RoboDK preview seq {msg.seq}: {frames.Length} poses");

        // Bricks the robot will move: remember their wall spot, then put them back in the tray
        bricksDone = 0;
        bricksTotal = 0;
        foreach (var f in frames)
        {
            if (f.grip != "close" || string.IsNullOrEmpty(f.brick) || !bricks.TryGetValue(f.brick, out var b)) continue;
            if (wallPoses.ContainsKey(f.brick)) continue;
            bricksTotal++;
            wallPoses[f.brick] = (b.position, b.rotation);
            var rb = b.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;
            if (sender != null && sender.TryGetPickPose(f.brick, out var p, out var r)) b.SetPositionAndRotation(p, r);
        }

        next = 0;
        BeginMove();
        label.text = "Rehearsal: this is how I will build your wall";
        VoiceGuide.Play("ghost_start");
    }

    void BeginMove()
    {
        Array.Copy(robot.Current, from, 6);
        float biggest = 0f;
        for (int i = 0; i < 6; i++) biggest = Mathf.Max(biggest, Mathf.Abs(frames[next].j[i] - from[i]));
        duration = Mathf.Max(0.05f, biggest / speed);
        t = 0f;
    }

    void Arrived(Frame f)
    {
        if (f.grip == "close" && !string.IsNullOrEmpty(f.brick) && bricks.TryGetValue(f.brick, out var b))
        {
            // Brick is in its pocket under the gripper: from now on it moves with the flange
            if (sender != null && sender.TryGetPickPose(f.brick, out var p, out var r)) b.SetPositionAndRotation(p, r);
            carried = b;
            carryOffset = robot.Flange.worldToLocalMatrix * b.localToWorldMatrix;
            label.text = $"Brick {bricksDone + 1} of {bricksTotal}";
            pause = gripPause;
        }
        else if (f.grip == "open")
        {
            // Released: the brick lands exactly on its spot in the wall
            if (carried != null && wallPoses.TryGetValue(carried.name, out var w)) carried.SetPositionAndRotation(w.pos, w.rot);
            carried = null;
            bricksDone++;
            label.text = $"Brick {bricksDone} of {bricksTotal} placed";
            pause = gripPause;
        }
    }

    void Finish()
    {
        frames = null;
        carried = null;
        label.text = "Rehearsal done\nOur team will now start the real robot";
        VoiceGuide.Play("ghost_done");
    }

    // Stop playing (new preview or error): bricks the preview moved go back to their wall spot
    void Stop()
    {
        if (frames != null)
            foreach (var kv in wallPoses)
                if (bricks.TryGetValue(kv.Key, out var b)) b.SetPositionAndRotation(kv.Value.pos, kv.Value.rot);
        Abandon();
    }

    // X pressed: the bricks are already back in the tray, just stop
    void Abandon()
    {
        frames = null;
        carried = null;
        wallPoses.Clear();
        if (label != null) label.text = "";
    }

    // ---------- label above the robot ----------

    TextMesh MakeLabel()
    {
        var go = new GameObject("Robot Preview Label");
        go.transform.SetParent(transform.parent, false);
        go.transform.localPosition = robot.baseInWorkspace + Vector3.up * 0.95f;
        var tm = go.AddComponent<TextMesh>();
        tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
        tm.fontSize = 48;
        tm.characterSize = 0.005f;
        tm.anchor = TextAnchor.LowerCenter;
        tm.alignment = TextAlignment.Center;
        LabelPanel.Add(tm);   // dark see-through panel behind the text (readability)
        tm.text = "";
        return tm;
    }

    void PlaceLabel()
    {
        var cam = Camera.main;
        if (cam != null && label != null)
            label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position);
    }

    // ---------- message from the bridge ----------

    [Serializable] public class Frame { public float[] j; public string grip; public string brick; }
    [Serializable] public class PreviewMsg { public string type; public int seq; public string error; public string key; public Frame[] frames; }
}
