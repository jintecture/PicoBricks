using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// Grid snap for one brick. When you let go of a brick over the table it:
//  1. settles into one of two poses: LYING (5 cm tall) or STANDING on its end (10 cm tall),
//     whichever is closest to how you held it, turned to the nearest 90°;
//  2. lines up: on another placed brick it sits centred on that brick (AlignToBelow = true),
//     on the table its edges snap to a 5 cm grid in the table's own frame (set by TableCalibration);
//  3. drops onto whatever is directly below it (table or brick) and locks in place (no physics).
// Let go off the table and it goes back to its start spot. Picking up a placed brick unlocks it,
// and any placed bricks that were resting on it drop straight down onto what's left (no floating).
[RequireComponent(typeof(Rigidbody), typeof(XRGrabInteractable))]
public class BrickSnap : MonoBehaviour
{
    [Tooltip("Table + bricks group; its origin is the middle of the table's front edge, on the top surface")]
    public Transform workspace;
    [Tooltip("The table cube inside Workspace (its scale is the table size)")]
    public Transform table;

    [Tooltip("Grid spacing on the table (m)")]
    public float grid = 0.05f;

    // true: a brick released on another brick sits centred on it (clean towers / crossings).
    // false: it snaps to the table grid instead, so half-brick offsets (brick-wall pattern) are possible.
    // Shared by all bricks; meant to be flipped by a controller button later.
    public static bool AlignToBelow = true;

    public bool IsPlaced { get; private set; }
    public bool IsStanding { get; private set; }

    // Placed pose in table coordinates, for the robot later: x right, y up (brick centre), z away from you (m); yaw in degrees
    public Vector3 PlacedLocalPosition { get; private set; }
    public float PlacedYaw { get; private set; }

    static readonly List<BrickSnap> all = new();

    Rigidbody rb;
    XRGrabInteractable grab;
    Collider col;
    Vector3 startLocalPos;
    Quaternion startLocalRot;
    Vector3 size; // brick's own dimensions: x = width, y = height, z = length (5 x 5 x 10 cm)

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        grab = GetComponent<XRGrabInteractable>();
        col = GetComponent<Collider>();
        startLocalPos = transform.localPosition;
        startLocalRot = transform.localRotation;
        size = transform.localScale; // bricks sit under Workspace/Bricks, both unscaled
    }

    void OnEnable()
    {
        all.Add(this);
        grab.selectEntered.AddListener(OnGrabbed);
        grab.selectExited.AddListener(OnReleased);
    }

    void OnDisable()
    {
        all.Remove(this);
        grab.selectEntered.RemoveListener(OnGrabbed);
        grab.selectExited.RemoveListener(OnReleased);
    }

    void OnGrabbed(SelectEnterEventArgs args)
    {
        bool wasPlaced = IsPlaced;
        IsPlaced = false;
        // Whatever was resting on this brick must not float
        if (wasPlaced) SettleAll();
    }

    // Runs after XRGrabInteractable has finished dropping the brick, so we get the last word on its pose
    void OnReleased(SelectExitEventArgs args)
    {
        if (grab.isSelected || workspace == null || table == null) return; // still held by the other hand
        if (!TrySnap()) ReturnToStart();
    }

    bool TrySnap()
    {
        // 1. Pose: is the long axis (local Z) closer to vertical than the short ones? Then it stands.
        Vector3 longAxis = workspace.InverseTransformDirection(transform.forward);
        IsStanding = Mathf.Abs(longAxis.y) > 0.7071f; // tilted more than 45° towards upright

        // Yaw from a horizontal axis of the brick: its long axis when lying, its width axis when standing
        Vector3 flat = IsStanding ? workspace.InverseTransformDirection(transform.right) : longAxis;
        flat.y = 0f;
        float yaw = flat.sqrMagnitude > 1e-4f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : 0f;
        if (IsStanding) yaw -= 90f; // width axis points along +x at yaw 0
        yaw = Mathf.Repeat(Mathf.Round(yaw / 90f) * 90f, 360f);
        bool sideways = Mathf.Approximately(yaw, 90f) || Mathf.Approximately(yaw, 270f);

        // Footprint on the table (x = left-right, z = front-back) and height, in table coordinates
        Vector2 foot = IsStanding ? new Vector2(size.x, size.y)
                     : sideways ? new Vector2(size.z, size.x) : new Vector2(size.x, size.z);
        float height = IsStanding ? size.z : size.y;
        Quaternion rot = workspace.rotation * Quaternion.Euler(0f, yaw, 0f) *
                         (IsStanding ? Quaternion.Euler(-90f, 0f, 0f) : Quaternion.identity);

        // 2. Line up: centred on the placed brick below, or on the table grid
        Vector3 local = workspace.InverseTransformPoint(transform.position);
        float x, z;
        BrickSnap below = AlignToBelow ? PlacedBrickBelow(local.x, local.z, foot) : null;
        if (below != null)
        {
            x = below.PlacedLocalPosition.x;
            z = below.PlacedLocalPosition.z;
        }
        else
        {
            // Snap the front-left corner to the grid so all edges land on grid lines
            x = Mathf.Round((local.x - foot.x / 2f) / grid) * grid + foot.x / 2f;
            z = Mathf.Round((local.z - foot.y / 2f) / grid) * grid + foot.y / 2f;
        }

        // Whole footprint must be on the table
        float halfWidth = table.localScale.x / 2f, depth = table.localScale.z;
        const float eps = 0.001f;
        if (x - foot.x / 2f < -halfWidth - eps || x + foot.x / 2f > halfWidth + eps ||
            z - foot.y / 2f < -eps || z + foot.y / 2f > depth + eps)
            return false;

        // 3. Height: land on top of whatever is below, then lock
        float bottom = SurfaceBelow(x, 1f, z, foot, rot);
        Place(new Vector3(x, bottom + height / 2f, z), rot, yaw);
        Debug.Log($"PicoBricks: {name} placed {(IsStanding ? "standing" : "lying")} at x={x * 100f:F1} z={z * 100f:F1} cm, " +
                  $"bottom {bottom * 100f:F0} cm, yaw {yaw:F0}°{(below != null ? $", on {below.name}" : "")}");
        return true;
    }

    void Place(Vector3 localPos, Quaternion rot, float yaw)
    {
        Vector3 pos = workspace.TransformPoint(localPos);
        rb.isKinematic = true;
        transform.SetPositionAndRotation(pos, rot);
        rb.position = pos;
        rb.rotation = rot;
        Physics.SyncTransforms(); // so the next sweep sees this brick where it now is

        IsPlaced = true;
        PlacedLocalPosition = localPos;
        PlacedYaw = yaw;
    }

    // Placed brick directly under a footprint centred at (x, z), if any
    BrickSnap PlacedBrickBelow(float x, float z, Vector2 foot)
    {
        Vector3 from = workspace.TransformPoint(new Vector3(x, 1f, z));
        Vector3 half = new Vector3(foot.x, 0.001f, foot.y) * 0.45f;
        col.enabled = false;
        bool hit = Physics.BoxCast(from, half, -workspace.up, out RaycastHit info, workspace.rotation, 2f,
                                   Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        col.enabled = true;
        if (!hit) return null;
        var other = info.collider.GetComponent<BrickSnap>();
        return other != null && other.IsPlaced ? other : null;
    }

    // Height (table coordinates, rounded to 5 cm) of the first surface below a footprint, sweeping down from startY.
    // Ignores this brick and any brick that is currently held.
    float SurfaceBelow(float x, float startY, float z, Vector2 foot, Quaternion rot)
    {
        var disabled = new List<Collider> { col };
        foreach (var b in all)
            if (b != this && b.grab.isSelected && b.col.enabled) disabled.Add(b.col);
        foreach (var c in disabled) c.enabled = false;

        Vector3 from = workspace.TransformPoint(new Vector3(x, startY, z));
        Vector3 half = new Vector3(foot.x, 0.001f, foot.y) * 0.45f; // slightly smaller: don't catch neighbours
        // rot only matters for its yaw; the box is flat
        Quaternion flatRot = workspace.rotation * Quaternion.Euler(0f, (Quaternion.Inverse(workspace.rotation) * rot).eulerAngles.y, 0f);
        bool hit = Physics.BoxCast(from, half, -workspace.up, out RaycastHit info, flatRot, 3f,
                                   Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

        foreach (var c in disabled) c.enabled = true;
        float top = hit ? workspace.InverseTransformPoint(info.point).y : 0f;
        return Mathf.Max(0f, Mathf.Round(top / grid) * grid); // all brick sizes are multiples of 5 cm
    }

    // After a brick is taken away: every placed brick, lowest first, drops straight down onto what's left
    static void SettleAll()
    {
        var placed = all.FindAll(b => b.IsPlaced && b.workspace != null);
        placed.Sort((a, b) => a.PlacedLocalPosition.y.CompareTo(b.PlacedLocalPosition.y));
        foreach (var b in placed) b.Settle();
    }

    void Settle()
    {
        float height = IsStanding ? size.z : size.y;
        bool sideways = Mathf.Approximately(PlacedYaw, 90f) || Mathf.Approximately(PlacedYaw, 270f);
        Vector2 foot = IsStanding ? new Vector2(size.x, size.y)
                     : sideways ? new Vector2(size.z, size.x) : new Vector2(size.x, size.z);

        Vector3 p = PlacedLocalPosition;
        float bottom = SurfaceBelow(p.x, p.y, p.z, foot, transform.rotation); // sweep from own centre downwards
        float y = bottom + height / 2f;
        if (y < p.y - 0.001f)
        {
            Place(new Vector3(p.x, y, p.z), transform.rotation, PlacedYaw);
            Debug.Log($"PicoBricks: {name} dropped to {bottom * 100f:F0} cm (support was taken away)");
        }
    }

    // Called by BrickControls when it puts this brick back on its spare spot (X button, fell off the world)
    public void Unplace()
    {
        IsPlaced = false;
        IsStanding = false;
    }

    // Back to its spare spot (when released off the table)
    public void ReturnToStart()
    {
        rb.isKinematic = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        transform.localPosition = startLocalPos;
        transform.localRotation = startLocalRot;
        rb.position = transform.position;
        rb.rotation = transform.rotation;
        IsPlaced = false;
        IsStanding = false;
    }
}
