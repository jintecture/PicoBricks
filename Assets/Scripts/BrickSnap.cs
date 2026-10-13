using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// Grid snap for one brick. When you let go of a brick over the table it:
//  1. turns to the nearest 90° (long side pointing away from you, or sideways),
//  2. moves so its edges sit on a 5 cm grid (in the table's own frame, set by TableCalibration),
//  3. drops onto whatever is directly below it (the table, or the top of another brick),
//  4. locks in place (no physics), so the layout is exact and stays put for the robot.
// Let go of a brick outside the table and it goes back to its start spot.
// Picking a locked brick up again unlocks it.
[RequireComponent(typeof(Rigidbody), typeof(XRGrabInteractable))]
public class BrickSnap : MonoBehaviour
{
    [Tooltip("Table + bricks group; its origin is the middle of the table's front edge, on the top surface")]
    public Transform workspace;
    [Tooltip("The table cube inside Workspace (its scale is the table size)")]
    public Transform table;

    [Tooltip("Grid spacing on the table (m)")]
    public float grid = 0.05f;

    public bool IsPlaced { get; private set; }

    // Snapped position in table coordinates, for the robot later: x right, y up, z away from you (m); yaw in degrees
    public Vector3 PlacedLocalPosition { get; private set; }
    public float PlacedYaw { get; private set; }

    Rigidbody rb;
    XRGrabInteractable grab;
    Collider col;
    Vector3 startLocalPos;
    Quaternion startLocalRot;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        grab = GetComponent<XRGrabInteractable>();
        col = GetComponent<Collider>();
        startLocalPos = transform.localPosition;
        startLocalRot = transform.localRotation;
    }

    void OnEnable()
    {
        grab.selectEntered.AddListener(OnGrabbed);
        grab.selectExited.AddListener(OnReleased);
    }

    void OnDisable()
    {
        grab.selectEntered.RemoveListener(OnGrabbed);
        grab.selectExited.RemoveListener(OnReleased);
    }

    void OnGrabbed(SelectEnterEventArgs args)
    {
        IsPlaced = false;
    }

    // Runs after XRGrabInteractable has finished dropping the brick, so we get the last word on its pose
    void OnReleased(SelectExitEventArgs args)
    {
        if (grab.isSelected || workspace == null || table == null) return; // still held by the other hand
        if (!TrySnap()) ReturnToStart();
    }

    bool TrySnap()
    {
        // 1. Yaw: project the brick's long axis (its local Z) onto the table and round to 90°
        Vector3 longAxis = workspace.InverseTransformDirection(transform.forward);
        longAxis.y = 0f;
        float yaw;
        if (longAxis.sqrMagnitude > 0.1f)
            yaw = Mathf.Atan2(longAxis.x, longAxis.z) * Mathf.Rad2Deg;
        else // held upright: use its short side instead
        {
            Vector3 side = workspace.InverseTransformDirection(transform.right);
            yaw = Mathf.Atan2(side.x, side.z) * Mathf.Rad2Deg - 90f;
        }
        yaw = Mathf.Repeat(Mathf.Round(yaw / 90f) * 90f, 360f);
        bool sideways = Mathf.Approximately(yaw, 90f) || Mathf.Approximately(yaw, 270f);

        // Footprint on the table (x = left-right, z = front-back)
        Vector3 size = transform.localScale; // bricks sit under Workspace/Bricks, both unscaled
        Vector2 foot = sideways ? new Vector2(size.z, size.x) : new Vector2(size.x, size.z);

        // 2. Grid: snap the brick's front-left corner to the grid so all edges land on grid lines
        Vector3 local = workspace.InverseTransformPoint(transform.position);
        float x = Mathf.Round((local.x - foot.x / 2f) / grid) * grid + foot.x / 2f;
        float z = Mathf.Round((local.z - foot.y / 2f) / grid) * grid + foot.y / 2f;

        // Whole footprint must be on the table
        float halfWidth = table.localScale.x / 2f, depth = table.localScale.z;
        const float eps = 0.001f;
        if (x - foot.x / 2f < -halfWidth - eps || x + foot.x / 2f > halfWidth + eps ||
            z - foot.y / 2f < -eps || z + foot.y / 2f > depth + eps)
            return false;

        // 3. Height: sweep the footprint straight down from 1 m above the table and land on the first thing hit
        Quaternion rot = workspace.rotation * Quaternion.Euler(0f, yaw, 0f);
        Vector3 halfExtents = new Vector3(size.x, 0.001f, size.z) * 0.45f; // slightly smaller: don't catch neighbours
        Vector3 from = workspace.TransformPoint(new Vector3(x, 1f, z));
        col.enabled = false; // don't hit ourselves
        bool hit = Physics.BoxCast(from, halfExtents, -workspace.up, out RaycastHit info, rot, 2f,
                                   Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        col.enabled = true;
        float top = hit ? workspace.InverseTransformPoint(info.point).y : 0f;
        // Round to whole brick layers so stacks stay exact
        float layer = Mathf.Max(0f, Mathf.Round(top / size.y));
        float y = layer * size.y + size.y / 2f;

        // 4. Lock in place
        Vector3 pos = workspace.TransformPoint(new Vector3(x, y, z));
        rb.isKinematic = true;
        transform.SetPositionAndRotation(pos, rot);
        rb.position = pos;
        rb.rotation = rot;

        IsPlaced = true;
        PlacedLocalPosition = new Vector3(x, y, z);
        PlacedYaw = yaw;
        Debug.Log($"PicoBricks: {name} placed at x={x * 100f:F1} z={z * 100f:F1} cm, layer {layer:F0}, yaw {yaw:F0}°");
        return true;
    }

    // Back to its spare spot (also used by the X reset button)
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
    }
}
