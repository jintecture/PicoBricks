using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

// Left controller buttons:  X = put all bricks back,  Y = passthrough on/off
public class BrickControls : MonoBehaviour
{
    public Transform bricksParent;
    public Passthrough passthrough;

    // Start poses relative to bricksParent, so a reset follows the table when it is moved
    readonly List<(Rigidbody rb, Vector3 localPos, Quaternion localRot)> startPoses = new();
    bool xWasDown, yWasDown;

    void Start()
    {
        if (bricksParent == null) return;
        foreach (var rb in bricksParent.GetComponentsInChildren<Rigidbody>())
            startPoses.Add((rb,
                bricksParent.InverseTransformPoint(rb.transform.position),
                Quaternion.Inverse(bricksParent.rotation) * rb.transform.rotation));
        Debug.Log($"PicoBricks: controls ready, {startPoses.Count} bricks");
    }

    void Update()
    {
        // Same button API the PICO SDK uses itself; X and Y are the left controller's primary/secondary buttons
        var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        if (!left.isValid) return;

        left.TryGetFeatureValue(CommonUsages.primaryButton, out bool xDown);
        left.TryGetFeatureValue(CommonUsages.secondaryButton, out bool yDown);

        RespawnFallenBricks();

        if (xDown && !xWasDown) ResetBricks();
        if (yDown && !yWasDown && passthrough != null) passthrough.Toggle();

        xWasDown = xDown;
        yWasDown = yDown;
    }

    /// <summary>Raised after X (or a calibration) put all bricks back on their start spots.</summary>
    public event System.Action BricksReset;

    public void ResetBricks()
    {
        foreach (var pose in startPoses) PutBack(pose);
        Debug.Log("PicoBricks: bricks reset");
        BricksReset?.Invoke();
    }

    // Safety net: a brick that falls far below the table goes back to its start spot
    void RespawnFallenBricks()
    {
        foreach (var pose in startPoses)
            if (pose.rb.position.y < bricksParent.position.y - 3f)
                PutBack(pose);
    }

    void PutBack((Rigidbody rb, Vector3 localPos, Quaternion localRot) pose)
    {
        var pos = bricksParent.TransformPoint(pose.localPos);
        var rot = bricksParent.rotation * pose.localRot;
        pose.rb.isKinematic = false; // placed (snapped) or built (wall) bricks are locked; free them again
        var grab = pose.rb.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        if (grab != null) grab.enabled = true; // the wall builder switches grabbing off
        var snap = pose.rb.GetComponent<BrickSnap>();
        if (snap != null) snap.Unplace();
        pose.rb.linearVelocity = Vector3.zero;
        pose.rb.angularVelocity = Vector3.zero;
        pose.rb.transform.SetPositionAndRotation(pos, rot);
        pose.rb.position = pos;
        pose.rb.rotation = rot;
    }
}
