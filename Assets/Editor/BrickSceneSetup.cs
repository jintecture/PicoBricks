using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: PicoBricks > Setup Brick Scene
// Replaces the plain XR Origin with the XRI Starter Assets rig (controller ray + grab),
// keeps PXR_Manager, and adds a floor, a table, six 5 x 5 x 10 cm bricks and the wall builder
// (draw a line, choose 1-3 layers, the bricks build the wall).
public static class BrickSceneSetup
{
    const string RigPath =
        "Assets/Samples/XR Interaction Toolkit/3.4.0/Starter Assets/Prefabs/XR Origin (XR Rig).prefab";

    // Real bricks: 5 cm wide, 5 cm high, 10 cm deep (x, y, z in metres)
    static readonly Vector3 BrickSize = new Vector3(0.05f, 0.05f, 0.10f);
    const int BrickCount = 6;
    // Estimate 0.13 kg (0.25 L of timber at ~500 kg/m³); a real brick weighs 0.12 kg, close enough for the app
    const float BrickMass = 0.13f;
    // Default only; TableCalibration replaces it with the measured height
    const float TableHeight = 0.75f;
    // Grid snap (BrickSnap.cs, version 1) is off from version 2 on: in the headset placed bricks ignored gravity
    // and some floated, and in version 2 the wall is generated from a drawn line instead of placed by hand.
    static readonly bool AddGridSnap = false;

    // Pocket centres of the printed pickup tray (hardware/pickup-tray/README.md), mm from the tray's front-left
    // corner: x to the right, y away from the front edge. Brick 1-3 = front row.
    static readonly float[] PocketX = { 35.5f, 114.5f, 193.5f };
    static readonly float[] PocketY = { 60.5f, 189.5f };
    // Bricks: brown at 70 % opacity, with black edges so each brick stays readable in a built wall
    static readonly Color BrickColour = new Color(0.55f, 0.36f, 0.2f, 0.7f);
    const float OutlineThickness = 0.002f; // 2 mm black edges

    // Ghost robot (HANDZONe's UR5 + LAMA table, see docs/HANDZONE_NOTES.md)
    const string GhostPrefabPath = "Assets/ThirdParty/HANDZONe/Robot/LAMA_UR5.prefab";
    const string GhostJointsPath = "Assets/ThirdParty/HANDZONe/Robot/LAMA_UR5_joints.json";
    static readonly Color GhostColour = new Color(0.8f, 0.9f, 1f, 0.35f);  // pale blue, see-through
    // Pickup tray: in the back corner beside the robot (the real corners are taken by the robot's own base, the far
    // ones are out of reach). Its right edge this far left of the robot base centre, its back edge 3 cm from the back.
    const float TrayRightEdgeFromRobot = 0.16f;
    const float EdgeMargin = 0.03f;
    static readonly Vector2 TraySize = new Vector2(0.229f, 0.250f);
    // Robot reach for walls, measured on the table from the base (m). The UR5 maths says 0.11-0.81 with the gripper
    // pointing down; inside 20 cm the robot's own base is in the way, beyond 75 cm the arm is nearly stretched out
    const float MinReach = 0.20f, MaxReach = 0.75f;

    // Same as the menu, without opening the Unity window:
    // Unity.exe -batchmode -quit -projectPath <PicoBricks> -executeMethod BrickSceneSetup.SetupFromCommandLine
    public static void SetupFromCommandLine()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        Setup();
    }

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

        var wood = GetOrCreateTransparentMaterial("Assets/Materials/BrickTransparent.mat", BrickColour);
        var outlineMat = GetOrCreateMaterial("Assets/Materials/BrickOutline.mat", Color.black, unlit: true);
        outlineMat.SetFloat("_Cull", 0f); // render both sides of the thin edge boxes
        var trayMat = GetOrCreateMaterial("Assets/Materials/Tray.mat", new Color(0.82f, 0.84f, 0.86f));
        var outlineMesh = GetOrCreateOutlineMesh("Assets/Models/BrickOutline.asset", BrickSize, OutlineThickness);
        var trayMesh = TrayMeshImporter.ImportOrLoad();
        var grey = GetOrCreateMaterial("Assets/Materials/Table.mat", new Color(0.35f, 0.35f, 0.38f));
        var floorMat = GetOrCreateMaterial("Assets/Materials/Floor.mat", new Color(0.55f, 0.55f, 0.55f));

        // Remove the test cube and anything from a previous run
        foreach (var name in new[] { "Cube", "Floor", "Table", "Bricks", "Workspace", "GhostRobot" })
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

        // Ghost LAMA table + UR5 from HANDZONe, lined up with the table frame (front edge = the long side the user
        // stands at, robot at the right-hand end). Its real size replaces the default grey table's size.
        var ghostMat = GetOrCreateTransparentMaterial("Assets/Materials/GhostRobot.mat", GhostColour);
        var ghost = PlaceGhostRobot(workspace.transform, ghostMat);
        Vector2 trayOffset = Vector2.zero;
        if (ghost != null)
        {
            table.transform.localScale = new Vector3(ghost.Width, ghost.Height, ghost.Depth);
            table.transform.localPosition = new Vector3(0f, -ghost.Height / 2f, ghost.Depth / 2f);
            floor.transform.localPosition = new Vector3(0f, -ghost.Height, 0f);
            // The ghost LAMA table is the visible table now; the grey box only stays for its collider and size
            table.GetComponent<Renderer>().enabled = false;
            // Tray in the back corner beside the robot
            Vector3 robotBase = ghost.Component.baseInWorkspace;
            float trayLeft = robotBase.x - TrayRightEdgeFromRobot - TraySize.x;
            float trayFront = ghost.Depth - EdgeMargin - TraySize.y;
            // measured from the middle of the front edge, like the robot (TableCalibration.trayFromOrigin)
            trayOffset = new Vector2(trayLeft, trayFront);
            // Where walls may go = the whole table within the robot's reach, minus the tray (WallBuilder.Allowed,
            // outlined in black in the headset). Reach worked out with robot-bridge/ur5_kinematics.py: gripper
            // pointing down, 11-81 cm from the base; with a safety margin:
            ghost.Component.minReach = MinReach;
            ghost.Component.maxReach = MaxReach;
            Debug.Log($"PicoBricks: tray at x {trayLeft:F3}, z {trayFront:F3}; walls {MinReach * 100f:F0}-" +
                      $"{MaxReach * 100f:F0} cm from the robot");
        }

        // The printed pickup tray (TableCalibration keeps it at trayOffset from the table's front-left corner).
        // The bricks start in its pockets, like the real ones the robot picks up.
        var tray = new GameObject("PickupTray");
        tray.transform.SetParent(workspace.transform, false);
        float trayFrom = ghost != null ? 0f : -table.transform.localScale.x / 2f;
        tray.transform.localPosition = new Vector3(trayFrom + trayOffset.x, 0f, trayOffset.y);
        if (trayMesh != null)
        {
            tray.AddComponent<MeshFilter>().sharedMesh = trayMesh;
            tray.AddComponent<MeshRenderer>().sharedMaterial = trayMat;
            tray.AddComponent<MeshCollider>().sharedMesh = trayMesh;
        }

        var bricks = new GameObject("Bricks");
        bricks.transform.SetParent(tray.transform, false);
        for (int i = 0; i < BrickCount; i++)
        {
            float px = PocketX[i % PocketX.Length], py = PocketY[i / PocketX.Length];
            var brick = GameObject.CreatePrimitive(PrimitiveType.Cube);
            brick.name = $"Brick_{i + 1}";
            brick.transform.SetParent(bricks.transform);
            // lying flat in its pocket, long side pointing away from the front edge
            brick.transform.localPosition = new Vector3(px / 1000f, BrickSize.y / 2f + 0.001f, py / 1000f);
            brick.transform.localScale = BrickSize;
            brick.GetComponent<Renderer>().sharedMaterial = wood;

            var outline = new GameObject("Outline");
            outline.transform.SetParent(brick.transform, false);
            outline.AddComponent<MeshFilter>().sharedMesh = outlineMesh;
            var outlineRenderer = outline.AddComponent<MeshRenderer>();
            outlineRenderer.sharedMaterial = outlineMat;
            outlineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

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

            // Grid snap on release: 5 cm grid in the table's frame, 90° turns, drops onto what's below, then locks
            if (AddGridSnap)
            {
                var snap = brick.AddComponent<BrickSnap>();
                snap.workspace = workspace.transform;
                snap.table = table.transform;
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
            calibration.tray = tray.transform;
            calibration.trayOffset = trayOffset;
            calibration.trayFromOrigin = ghost != null;
            // With the ghost robot the table is the LAMA table: known size, 2-point calibration from its right corner
            calibration.fixedSize = ghost != null;
            if (ghost != null) calibration.fixedTableSize = new Vector3(ghost.Width, ghost.Height, ghost.Depth);
            calibration.rightController = FindChild(rig.transform, "Right Controller");
            calibration.bricks = controls;
            calibration.passthrough = passthrough;
            calibration.tipMaterial = GetOrCreateMaterial("Assets/Materials/CalibrationTip.mat", new Color(1f, 0.45f, 0f), unlit: true);
            if (calibration.rightController == null)
                Debug.LogWarning("PicoBricks: 'Right Controller' not found in the rig, table calibration won't work");

            // Version 2: draw a line on the table, choose layers, the bricks build the wall
            var wall = rig.GetComponent<WallBuilder>();
            if (wall == null) wall = Undo.AddComponent<WallBuilder>(rig);
            wall.workspace = workspace.transform;
            wall.table = table.transform;
            wall.bricksParent = bricks.transform;
            wall.rightController = calibration.rightController;
            wall.calibration = calibration;
            wall.controls = controls;
            wall.brickSize = BrickSize;
            wall.tray = tray.transform;
            if (trayMesh != null) wall.traySize = new Vector2(trayMesh.bounds.size.x, trayMesh.bounds.size.z);
            wall.lineMaterial = GetOrCreateMaterial("Assets/Materials/WallLine.mat", new Color(0.1f, 0.85f, 1f), unlit: true);
            wall.zoneMaterial = GetOrCreateMaterial("Assets/Materials/BuildArea.mat", new Color(1f, 1f, 1f), unlit: true);
            // No fixed drawing rectangle: the whole table, minus tray and unreachable parts (WallBuilder.Allowed).
            // Set explicitly: the component is reused between runs, so an old value would otherwise stay in the scene
            // (that kept the old 44 x 27 cm rectangle alive after 20 Oct)
            wall.drawArea = default;
            if (ghost != null)
            {
                ghost.Component.passthrough = passthrough;
                wall.robot = ghost.Component;
                wall.zoneMaterial = GetOrCreateMaterial("Assets/Materials/DrawArea.mat", Color.black, unlit: true);
            }
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

    static Material GetOrCreateMaterial(string path, Color color, bool unlit = false)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");
        // Unlit for lines: always the same bright colour, also in passthrough
        mat = new Material(Shader.Find(unlit ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", color);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // URP Lit, transparent (alpha blended) so you can see through the bricks
    static Material GetOrCreateTransparentMaterial(string path, Color color)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        bool isNew = mat == null;
        if (isNew)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        }
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Surface", 1f);   // transparent
        mat.SetFloat("_Blend", 0f);     // alpha
        mat.SetFloat("_ZWrite", 0f);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        if (isNew) AssetDatabase.CreateAsset(mat, path);
        else EditorUtility.SetDirty(mat);
        return mat;
    }

    // 12 thin boxes along the edges of a unit cube, sized so they are `thickness` metres thick once the brick's
    // own scale (brickSize) is applied. Used as a child of every brick for the black outline.
    static Mesh GetOrCreateOutlineMesh(string path, Vector3 brickSize, float thickness)
    {
        var verts = new System.Collections.Generic.List<Vector3>();
        var tris = new System.Collections.Generic.List<int>();
        var t = new Vector3(thickness / brickSize.x, thickness / brickSize.y, thickness / brickSize.z);

        void AddBox(Vector3 centre, Vector3 size)
        {
            Vector3 h = size / 2f;
            int start = verts.Count;
            for (int i = 0; i < 8; i++)
                verts.Add(centre + new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z));
            int[] faces =
            {
                0, 2, 3, 0, 3, 1,  4, 5, 7, 4, 7, 6,  0, 1, 5, 0, 5, 4,
                2, 6, 7, 2, 7, 3,  0, 4, 6, 0, 6, 2,  1, 3, 7, 1, 7, 5,
            };
            foreach (int f in faces) tris.Add(start + f);
        }

        foreach (float a in new[] { -0.5f, 0.5f })
            foreach (float b in new[] { -0.5f, 0.5f })
            {
                AddBox(new Vector3(0, a, b), new Vector3(1 + t.x, t.y, t.z)); // along x
                AddBox(new Vector3(a, 0, b), new Vector3(t.x, 1 + t.y, t.z)); // along y
                AddBox(new Vector3(a, b, 0), new Vector3(t.x, t.y, 1 + t.z)); // along z
            }

        var mesh = new Mesh { name = "BrickOutline" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        if (!AssetDatabase.IsValidFolder("Assets/Models"))
            AssetDatabase.CreateFolder("Assets", "Models");
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null)
        {
            EditorUtility.CopySerialized(mesh, existing);
            return existing;
        }
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    // ---------- ghost robot ----------

    [Serializable] class JointInfo { public string path; public float[] dir; public float[] initialEuler; }
    [Serializable] class JointFile { public JointInfo[] joints; }

    class GhostPlacement
    {
        public GhostRobot Component;
        public float Width, Depth, Height; // the LAMA table: along the front edge, front to back, floor to top (m)
    }

    // Puts HANDZONe's LAMA table + UR5 in the Workspace so that the table's long front edge lies on the Workspace's
    // front edge (origin = its middle, on the table top) with the robot at the right-hand end, as seen by the user.
    // The model's own measurements are used, not hand-typed numbers.
    static GhostPlacement PlaceGhostRobot(Transform workspace, Material ghostMat)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GhostPrefabPath);
        var jointsAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(GhostJointsPath);
        if (prefab == null || jointsAsset == null)
        {
            Debug.LogWarning("PicoBricks: HANDZONe robot not found in Assets/ThirdParty/HANDZONe, no ghost robot");
            return null;
        }

        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        go.name = "GhostRobot";
        Undo.RegisterCreatedObjectUndo(go, "Ghost robot");
        // Nothing may bump into the ghost: the bricks only collide with the (invisible) table box
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);

        var tableModel = FindChild(go.transform, "LAMA_Table");
        var ur5 = FindChild(go.transform, "UR5");
        if (tableModel == null || ur5 == null)
        {
            Debug.LogWarning("PicoBricks: LAMA_Table or UR5 missing in the HANDZONe prefab, no ghost robot");
            UnityEngine.Object.DestroyImmediate(go);
            return null;
        }

        // Measure the table model (world box around its meshes, while the prefab still stands where it was made)
        var tableRenderers = tableModel.GetComponentsInChildren<Renderer>(true);
        var box = tableRenderers[0].bounds;
        foreach (var r in tableRenderers) box.Encapsulate(r.bounds);
        Vector3 basePos = ur5.position;

        // Long horizontal side = the front edge, pointing towards the robot = "right" for the user
        bool longIsX = box.size.x >= box.size.z;
        Vector3 right = longIsX ? Vector3.right : Vector3.forward;
        if (Vector3.Dot(basePos - box.center, right) < 0f) right = -right;
        Vector3 forward = Vector3.Cross(right, Vector3.up); // away from the user, same rule as TableCalibration
        float width = longIsX ? box.size.x : box.size.z;
        float depth = longIsX ? box.size.z : box.size.x;
        Vector3 frontMiddle = box.center - forward * (depth / 2f);
        frontMiddle.y = box.max.y;

        // Express the whole prefab relative to that front edge, then hang it under the Workspace
        var frameRot = Quaternion.LookRotation(forward, Vector3.up);
        var inv = Quaternion.Inverse(frameRot);
        Vector3 localPos = inv * (go.transform.position - frontMiddle);
        Quaternion localRot = inv * go.transform.rotation;
        Vector3 baseLocal = inv * (basePos - frontMiddle);
        go.transform.SetParent(workspace, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;

        // See-through pale robot (the table keeps its look and only shows in VR mode)
        foreach (var r in ur5.GetComponentsInChildren<Renderer>(true))
        {
            r.sharedMaterials = Enumerable.Repeat(ghostMat, r.sharedMaterials.Length).ToArray();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        var ghost = go.AddComponent<GhostRobot>();
        ghost.baseInWorkspace = baseLocal;
        ghost.tableRenderers = tableRenderers;
        var file = JsonUtility.FromJson<JointFile>(jointsAsset.text);
        int n = file.joints.Length;
        ghost.joints = new Transform[n];
        ghost.directions = new Vector3[n];
        ghost.restEuler = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var j = file.joints[i];
            ghost.joints[i] = go.transform.Find(j.path);
            ghost.directions[i] = new Vector3(j.dir[0], j.dir[1], j.dir[2]);
            ghost.restEuler[i] = new Vector3(j.initialEuler[0], j.initialEuler[1], j.initialEuler[2]);
            if (ghost.joints[i] == null) Debug.LogWarning($"PicoBricks: ghost robot joint not found: {j.path}");
        }

        Debug.Log($"PicoBricks: ghost LAMA table {width:F3} x {depth:F3} m, {box.size.y:F3} m high; " +
                  $"UR5 base at {baseLocal} in the table frame");
        return new GhostPlacement { Component = ghost, Width = width, Depth = depth, Height = box.size.y };
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
