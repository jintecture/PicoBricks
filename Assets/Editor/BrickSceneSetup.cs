using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: PicoBricks > Setup Brick Scene
// Replaces the plain XR Origin with the XRI Starter Assets rig (controller ray + grab),
// keeps PXR_Manager, and adds a floor, a table and five grabbable 10 cm bricks.
public static class BrickSceneSetup
{
    const string RigPath =
        "Assets/Samples/XR Interaction Toolkit/3.4.0/Starter Assets/Prefabs/XR Origin (XR Rig).prefab";

    // Real bricks: 5 cm wide, 5 cm high, 10 cm deep (x, y, z in metres)
    static readonly Vector3 BrickSize = new Vector3(0.05f, 0.05f, 0.10f);
    const int BrickCount = 6;
    const int BricksPerRow = 3;
    // TODO(Jin): real weight to follow. Estimate: 0.25 L of timber at ~500 kg/m³ ≈ 0.13 kg
    const float BrickMass = 0.13f;
    // Default only; TableCalibration replaces it with the measured height
    const float TableHeight = 0.75f;

    [MenuItem("PicoBricks/Setup Brick Scene")]
    static void Setup()
    {
        var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPath);
        if (rigPrefab == null)
        {
            EditorUtility.DisplayDialog("PicoBricks",
                "XRI Starter Assets not found.\n\nWindow > Package Management > Package Manager > " +
                "XR Interaction Toolkit > Samples > Starter Assets > Import, then run this again.", "OK");
            return;
        }

        var xrOriginType = FindType("XROrigin");
        var pxrManagerType = FindType("PXR_Manager");
        var grabType = FindType("XRGrabInteractable");

        var rig = SetupRig(rigPrefab, xrOriginType, pxrManagerType);
        EnablePicoVideoSeeThrough();

        var wood = GetOrCreateMaterial("Assets/Materials/Wood.mat", new Color(0.78f, 0.62f, 0.42f));
        var grey = GetOrCreateMaterial("Assets/Materials/Table.mat", new Color(0.35f, 0.35f, 0.38f));
        var floorMat = GetOrCreateMaterial("Assets/Materials/Floor.mat", new Color(0.55f, 0.55f, 0.55f));

        // Remove the test cube and anything from a previous run
        foreach (var name in new[] { "Cube", "Floor", "Table", "Bricks", "Workspace" })
        {
            var old = GameObject.Find(name);
            if (old != null) Undo.DestroyObjectImmediate(old);
        }

        // Workspace = table + bricks + floor, moved together by TableCalibration.
        // Its origin is the middle of the table's front edge, on the top surface.
        var workspace = new GameObject("Workspace");
        workspace.transform.position = new Vector3(0f, TableHeight, 0.5f);
        Undo.RegisterCreatedObjectUndo(workspace, "Workspace");

        // The headset's own floor height is unreliable in passthrough, so the floor hangs off the table:
        // one table-height below the measured table top. Big enough to catch anything dropped.
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.SetParent(workspace.transform, false);
        floor.transform.localPosition = new Vector3(0f, -TableHeight, 0f);
        floor.transform.localScale = new Vector3(3f, 1f, 3f); // 30 x 30 m
        floor.GetComponent<Renderer>().sharedMaterial = floorMat;

        var table = GameObject.CreatePrimitive(PrimitiveType.Cube);
        table.name = "Table";
        table.transform.SetParent(workspace.transform, false);
        table.transform.localPosition = new Vector3(0f, -TableHeight / 2f, 0.3f);
        table.transform.localScale = new Vector3(1.0f, TableHeight, 0.6f);
        table.GetComponent<Renderer>().sharedMaterial = grey;

        var bricks = new GameObject("Bricks");
        bricks.transform.SetParent(workspace.transform, false);
        // Rows of bricks near the front edge, 10 cm apart side to side, long side pointing away from you
        for (int i = 0; i < BrickCount; i++)
        {
            int column = i % BricksPerRow, row = i / BricksPerRow;
            var brick = GameObject.CreatePrimitive(PrimitiveType.Cube);
            brick.name = $"Brick_{i}";
            brick.transform.SetParent(bricks.transform);
            brick.transform.localPosition = new Vector3(
                (column - (BricksPerRow - 1) / 2f) * 0.10f,
                BrickSize.y / 2f + 0.001f,
                0.10f + row * 0.15f);
            brick.transform.localScale = BrickSize;
            brick.GetComponent<Renderer>().sharedMaterial = wood;

            var rb = brick.AddComponent<Rigidbody>();
            rb.mass = BrickMass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            if (grabType != null)
            {
                var grab = brick.AddComponent(grabType);
                // Put bricks down gently instead of throwing them
                grabType.GetProperty("throwOnDetach")?.SetValue(grab, false);
                // Hold the brick where you grabbed it instead of snapping its centre to the hand
                grabType.GetProperty("useDynamicAttach")?.SetValue(grab, true);
            }
        }

        if (rig != null)
        {
            // Unity's fake-null objects break ??, so check explicitly
            var passthrough = rig.GetComponent<Passthrough>();
            if (passthrough == null) passthrough = Undo.AddComponent<Passthrough>(rig);
            passthrough.hideInPassthrough = new[] { floor.GetComponent<Renderer>() };

            var controls = rig.GetComponent<BrickControls>();
            if (controls == null) controls = Undo.AddComponent<BrickControls>(rig);
            controls.bricksParent = bricks.transform;
            controls.passthrough = passthrough;

            var calibration = rig.GetComponent<TableCalibration>();
            if (calibration == null) calibration = Undo.AddComponent<TableCalibration>(rig);
            calibration.workspace = workspace.transform;
            calibration.table = table.transform;
            calibration.floor = floor.transform;
            calibration.rightController = FindChild(rig.transform, "Right Controller");
            calibration.bricks = controls;
            calibration.passthrough = passthrough;
            if (calibration.rightController == null)
                Debug.LogWarning("PicoBricks: 'Right Controller' not found in the rig, table calibration won't work");
        }

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("PicoBricks: scene set up and saved.");
    }

    // Same as ticking "Video Seethrough" in the PICO project settings (adds the camera permission to the build)
    static void EnablePicoVideoSeeThrough()
    {
        var settings = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/Resources/PXR_ProjectSetting.asset");
        if (settings == null) return;
        var so = new SerializedObject(settings);
        var prop = so.FindProperty("videoSeeThrough");
        if (prop == null) return;
        prop.boolValue = true;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
    }

    static GameObject SetupRig(GameObject rigPrefab, Type xrOriginType, Type pxrManagerType)
    {
        if (xrOriginType == null) return null;

        var origins = UnityEngine.Object.FindObjectsByType(xrOriginType, FindObjectsSortMode.None)
            .Cast<Component>().ToList();
        var starterRig = origins.FirstOrDefault(o =>
            PrefabUtility.GetCorrespondingObjectFromSource(o.gameObject) == rigPrefab);

        if (starterRig == null)
        {
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
            Undo.RegisterCreatedObjectUndo(rig, "XR Rig");
            foreach (var o in origins) Undo.DestroyObjectImmediate(o.gameObject);
            starterRig = rig.GetComponent(xrOriginType);
        }

        // Floor tracking: y = 0 is the real floor, so the table really stands on it
        var originSo = new SerializedObject(starterRig);
        var originMode = originSo.FindProperty("m_RequestedTrackingOriginMode");
        if (originMode != null)
        {
            originMode.enumValueIndex = 2; // NotSpecified, Device, Floor
            originSo.ApplyModifiedProperties();
        }

        if (pxrManagerType != null && starterRig.GetComponent(pxrManagerType) == null)
            Undo.AddComponent(starterRig.gameObject, pxrManagerType);

        // AR: the virtual world must stay locked to the real room, so switch off all of the sample rig's
        // locomotion (gravity made the rig fall onto the moved floor, thumbsticks moved/turned the world)
        foreach (var t in starterRig.GetComponentsInChildren<Transform>(true))
            if (Array.IndexOf(new[] { "Move", "Gravity", "Turn", "Teleportation", "Climb", "Grab Move" }, t.name) >= 0)
            {
                Undo.RecordObject(t.gameObject, "Disable locomotion");
                t.gameObject.SetActive(false);
            }
        var characterController = starterRig.GetComponent<CharacterController>();
        if (characterController != null)
        {
            Undo.RecordObject(characterController, "Disable character controller");
            characterController.enabled = false;
        }

        // Any leftover non-XR camera would fight the headset camera
        foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            if (!cam.transform.IsChildOf(starterRig.transform))
                Undo.DestroyObjectImmediate(cam.gameObject);

        return starterRig.gameObject;
    }

    static Material GetOrCreateMaterial(string path, Color color)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");
        mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", color);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    static Transform FindChild(Transform parent, string name)
    {
        foreach (var t in parent.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    static Type FindType(string name) =>
        TypeCache.GetTypesDerivedFrom<Component>().FirstOrDefault(t => t.Name == name);
}
