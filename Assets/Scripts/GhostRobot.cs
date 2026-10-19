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
    [Tooltip("Furthest a brick may be from the base, measured on the table (m). UR5 = 0.85, less pointing down")]
    public float maxReach = 0.70f;
    [Tooltip("Closest a brick may be to the base (m): the robot can't reach right next to itself")]
    public float minReach = 0.18f;

    [Tooltip("Joint angles (degrees) shown at start: a tool-down pose above the table")]
    public float[] homeDegrees = { 0f, -90f, -90f, -90f, 90f, 0f };

    void Start()
    {
        if (passthrough != null && tableRenderers != null)
            foreach (var r in tableRenderers) passthrough.HideWhenOn(r);
        SetJointsDegrees(homeDegrees);
    }

    /// <summary>Pose the arm. Angles in degrees, UR5 joint order (base, shoulder, elbow, wrist 1, 2, 3).</summary>
    public void SetJointsDegrees(float[] degrees)
    {
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
