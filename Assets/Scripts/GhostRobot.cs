using UnityEngine;

// The UR5 + LAMA table from HANDZONe, shown as a "ghost" on top of the real ones (see docs/HANDZONE_NOTES.md).
// Posed with the same 6 joint angles the real UR5 / RoboDK use, so it can preview what the robot will do.
public class GhostRobot : MonoBehaviour
{
    [Tooltip("The 6 UR5 joints, base to wrist (from HANDZONe's RobotManager)")]
    public Transform[] joints = new Transform[6];
    [Tooltip("Per joint: which local axis it turns around, and which way (HANDZONe: z axis, -1/+1)")]
    public Vector3[] directions = new Vector3[6];
    [Tooltip("Per joint: the model's rotation at joint angle 0 (local euler angles)")]
    public Vector3[] restEuler = new Vector3[6];
    [Tooltip("Per joint: added to the real UR5 angle (degrees). The model's rest pose is the upright 'candle', " +
             "which is (0, -90, 0, -90, 0, 0) on the real robot, so shoulder and wrist 1 are shifted by 90")]
    public float[] zeroOffset = { 0f, 90f, 0f, 90f, 0f, 0f };

    [Tooltip("LAMA table renderers: hidden in passthrough, because there you see the real table")]
    public Renderer[] tableRenderers;
    public Passthrough passthrough;

    [Header("Robot base in the table frame (Workspace): x right, y up, z away from the front edge, m")]
    public Vector3 baseInWorkspace;
    [Tooltip("Furthest a brick may be from the base, measured on the table (m). UR5 maths: 0.81 with the gripper pointing down")]
    public float maxReach = 0.75f;
    [Tooltip("Closest a brick may be to the base (m): the robot can't reach right next to itself")]
    public float minReach = 0.20f;

    [Tooltip("Joint angles (degrees) shown at start: a tool-down pose above the table")]
    public float[] homeDegrees = { 0f, -90f, -90f, -90f, 90f, 0f };

    /// <summary>Current joint angles (degrees, real UR5 convention).</summary>
    public float[] Current { get; private set; } = new float[6];

    /// <summary>The last arm part (wrist 3 / flange): bricks being carried follow this.</summary>
    public Transform Flange => joints != null && joints.Length > 0 ? joints[joints.Length - 1] : transform;

    void Start()
    {
        if (passthrough != null && tableRenderers != null)
            foreach (var r in tableRenderers) passthrough.HideWhenOn(r);
        SetJointsDegrees(homeDegrees);
        // Plays the robot movements RoboDK sends back (see GhostPlayer.cs)
        if (GetComponent<GhostPlayer>() == null) gameObject.AddComponent<GhostPlayer>();
    }

    /// <summary>
    /// Which way the arm points (degrees, counter-clockwise from the table's x axis, seen from above) with base angle
    /// `baseDeg` and all other joints 0. Measured as the difference between "shoulder 0" and "shoulder -90", so the
    /// sideways offsets of the wrist cancel out. The robot bridge compares this with RoboDK's UR5 to line both up.
    /// </summary>
    public float ArmYaw(float baseDeg)
    {
        var keep = (float[])Current.Clone();
        var ws = transform.parent;
        SetJointsDegrees(new[] { baseDeg, 0f, 0f, 0f, 0f, 0f });
        Vector3 a = ws.InverseTransformPoint(Flange.position);
        SetJointsDegrees(new[] { baseDeg, -90f, 0f, 0f, 0f, 0f });
        Vector3 b = ws.InverseTransformPoint(Flange.position);
        SetJointsDegrees(keep);
        Vector3 d = a - b;
        return Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg; // table frame: x right, z (Unity) = away from the user
    }

    /// <summary>Test poses for the bridge's self-check (the same list is in picobricks_bridge.py).</summary>
    public static readonly float[][] CheckPoses =
    {
        new[] { 0f, -90f, -90f, -90f, 90f, 0f },
        new[] { 30f, -60f, -100f, -60f, 90f, 0f },
        new[] { -45f, -120f, 60f, -30f, -90f, 45f },
    };

    /// <summary>
    /// Where the hand (flange) is for each check pose, relative to the base, in the bridge's table axes
    /// (x right, y away, z up; metres). The bridge compares this with its UR5 maths: a joint turning the wrong way
    /// shows up as tens of centimetres difference.
    /// </summary>
    public float[] CheckPositions()
    {
        var keep = (float[])Current.Clone();
        var ws = transform.parent;
        var result = new float[CheckPoses.Length * 3];
        for (int i = 0; i < CheckPoses.Length; i++)
        {
            SetJointsDegrees(CheckPoses[i]);
            Vector3 p = ws.InverseTransformPoint(Flange.position) - baseInWorkspace;
            result[i * 3] = p.x;
            result[i * 3 + 1] = p.z;
            result[i * 3 + 2] = p.y;
        }
        SetJointsDegrees(keep);
        return result;
    }

    /// <summary>Pose the arm. Angles in degrees, UR5 joint order (base, shoulder, elbow, wrist 1, 2, 3).</summary>
    public void SetJointsDegrees(float[] degrees)
    {
        for (int i = 0; i < Current.Length && i < degrees.Length; i++) Current[i] = degrees[i];
        for (int i = 0; i < joints.Length && i < degrees.Length; i++)
            if (joints[i] != null)
            {
                float offset = zeroOffset != null && i < zeroOffset.Length ? zeroOffset[i] : 0f;
                joints[i].localRotation = Quaternion.Euler(restEuler[i] + directions[i] * (degrees[i] + offset));
            }
    }

    /// <summary>Same in radians (what the UR5 and HANDZONe's server use).</summary>
    public void SetJointsRadians(double[] radians)
    {
        var d = new float[radians.Length];
        for (int i = 0; i < radians.Length; i++) d[i] = (float)(radians[i] * Mathf.Rad2Deg);
        SetJointsDegrees(d);
    }

    /// <summary>Can the robot reach this point on the table? (table frame, metres)</summary>
    public bool InReach(Vector3 tablePoint)
    {
        var d = tablePoint - baseInWorkspace;
        d.y = 0f;
        float r = d.magnitude;
        return r <= maxReach && r >= minReach;
    }
}
