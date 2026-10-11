using System.Collections;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Shows the real room (PICO video see-through) behind the virtual objects.
public class Passthrough : MonoBehaviour
{
    [Tooltip("Hidden while passthrough is on; their colliders keep working")]
    public Renderer[] hideInPassthrough;

    public bool startOn = true;

    public bool IsOn { get; private set; }

    // The headset ignores the request if it arrives before the XR session is running,
    // so wait a moment after start-up and after the app resumes.
    IEnumerator Start()
    {
        yield return new WaitForSeconds(1f);
        Set(startOn);
    }

    void OnApplicationPause(bool paused)
    {
        if (!paused && IsOn) StartCoroutine(ReapplyAfterResume());
    }

    IEnumerator ReapplyAfterResume()
    {
        yield return new WaitForSeconds(0.5f);
        Set(true);
    }

    public void Toggle() => Set(!IsOn);

    public void HideWhenOn(Renderer r)
    {
        if (r == null || System.Array.IndexOf(hideInPassthrough, r) >= 0) return;
        System.Array.Resize(ref hideInPassthrough, hideInPassthrough.Length + 1);
        hideInPassthrough[^1] = r;
        r.enabled = !IsOn;
    }

    public void Set(bool on)
    {
        IsOn = on;

        var cam = Camera.main;
        if (cam != null)
        {
            // Transparent background lets the camera image show through
            cam.clearFlags = on ? CameraClearFlags.SolidColor : CameraClearFlags.Skybox;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            // HDR renders to a colour format without an alpha channel, which paints the background solid black
            cam.allowHDR = !on;
            var urp = cam.GetUniversalAdditionalCameraData();
            if (urp != null) urp.renderPostProcessing = !on;
        }
        else
        {
            Debug.LogWarning("PicoBricks: no camera tagged MainCamera");
        }

        foreach (var r in hideInPassthrough)
            if (r != null) r.enabled = !on;

        // Direct plugin calls exist in both PICO SDK 2.5 (matches headset firmware 5.9.9) and 3.x
        int result = PXR_Plugin.Boundary.UPxr_SetSeeThroughBackground(on);
        PXR_Plugin.Boundary.UPxr_SetSeeThroughState(on);
        // Note: switching see-through on this headset (firmware 5.9.9) shifts the tracking height by ~1.15 m.
        // Heights therefore come from TableCalibration, not from the headset's floor; re-place the table after toggling.
        Debug.Log($"PicoBricks: passthrough {(on ? "ON" : "OFF")}, SetSeeThroughBackground result={result}");
    }
}
