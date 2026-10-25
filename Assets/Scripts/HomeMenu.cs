using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

// Home screen after the intro (Jin, 24 Oct): three options, THUMBSTICK up/down to choose (the chosen one blinks),
// A = open it, B = back to this screen from anywhere.
//   1. Build a wall with the robot   -> WallBuilder (+ laptop bridge, ghost robot, real robot)
//   2. Design our pavilion           -> PavilionBuilder (visual only, the whole table, no robot)
//   3. Settings                      -> table calibration (staff; A = start, B = back / cancel)
// Voice "menu_welcome" ("Welcome to Print, Hello Earth") whenever the screen opens.
// Drawn with PicoBricks/IntroOverlay on a dark panel that follows the head, like the intro.
public class HomeMenu : MonoBehaviour
{
    public enum Mode { Intro, Menu, Robot, Pavilion, Settings }

    /// <summary>What the visitor is doing now; WallBuilder and PavilionBuilder only react in their own mode.</summary>
    public static Mode Current { get; private set; } = Mode.Intro;

    public IntroSequence intro;
    public Shader overlayShader;
    public Texture2D logo, title, subtitle;
    [Tooltip("Robot, pavilion, settings (in this order)")]
    public Texture2D[] options = new Texture2D[3];
    public Texture2D hint, settingsTitle, settingsHint;
    public WallBuilder wall;
    public PavilionBuilder pavilion;
    public TableCalibration calibration;
    public BrickControls controls;
    [Tooltip("Only shown while building with the robot (and in settings): ghost robot, pickup tray with its bricks")]
    public Transform[] robotOnly;
    public float distance = 2.5f;

    const float LineHeight = 0.075f;   // height of one line of menu text at `distance` (m)

    int selected;
    bool aWasDown = true, bWasDown = true, stickWasPushed = true;   // ignore buttons still held from the intro
    Transform cam, anchor;
    GameObject menuPage, settingsPage;
    readonly List<Material> optionMats = new List<Material>();
    readonly Dictionary<Renderer, bool> hiddenRenderers = new Dictionary<Renderer, bool>();

    static HomeMenu instance;
    const float RestartHoldSeconds = 3f;
    float xDownSince = -1f;

    void Awake() => instance = this;

    void Start()
    {
        IntroSequence.Finished += ShowMenu;
        if (!IntroSequence.Playing) ShowMenu();
    }

    void OnDestroy() => IntroSequence.Finished -= ShowMenu;

    /// <summary>Back to the opening for the next visitor: hold X (left controller) for 3 s, or R in the laptop
    /// bridge window. Clears the pavilion design, puts the bricks back, keeps the table calibration.</summary>
    public static void RestartFromIntro()
    {
        if (instance == null || instance.intro == null || IntroSequence.Playing) return;
        instance.SetMode(Mode.Intro);
        if (instance.pavilion != null) instance.pavilion.Clear();
        if (instance.controls != null) instance.controls.ResetBricks();
        instance.intro.Play();
    }

    void Update()
    {
        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        right.TryGetFeatureValue(CommonUsages.primaryButton, out bool a);
        right.TryGetFeatureValue(CommonUsages.secondaryButton, out bool b);
        right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick);
        bool aPressed = a && !aWasDown, bPressed = b && !bWasDown;
        bool pushed = Mathf.Abs(stick.y) > 0.6f, stickPressed = pushed && !stickWasPushed;
        aWasDown = a; bWasDown = b; stickWasPushed = pushed;

        // Staff: hold X on the left controller for 3 seconds = start again from the opening
        InputDevices.GetDeviceAtXRNode(XRNode.LeftHand).TryGetFeatureValue(CommonUsages.primaryButton, out bool x);
        if (!x) xDownSince = -1f;
        else if (xDownSince < 0f) xDownSince = Time.time;
        else if (Time.time - xDownSince >= RestartHoldSeconds)
        {
            xDownSince = float.MaxValue;   // once per hold
            RestartFromIntro();
        }

        switch (Current)
        {
            case Mode.Menu:
                if (stickPressed) selected = (selected + (stick.y > 0f ? -1 : 1) + options.Length) % options.Length;
                if (aPressed) Open(selected);
                Blink();
                break;
            case Mode.Robot:
            case Mode.Pavilion:
                if (bPressed) ShowMenu();
                break;
            case Mode.Settings:
                if (calibration != null && calibration.IsCalibrating)
                {
                    if (bPressed) calibration.Cancel();
                }
                else if (bPressed) ShowMenu();
                else if (aPressed && calibration != null) calibration.Begin();
                // The steps are shown on the controller; the page is back when calibration ends (placed, failed or B)
                if (Current == Mode.Settings)
                    settingsPage.SetActive(calibration == null || !calibration.IsCalibrating);
                break;
        }
    }

    void LateUpdate()
    {
        if (anchor == null || cam == null) return;
        float k = 1f - Mathf.Exp(-10f * Time.deltaTime);   // follows the head, smoothed
        anchor.position = Vector3.Lerp(anchor.position, cam.TransformPoint(0f, 0f, distance), k);
        anchor.rotation = Quaternion.Slerp(anchor.rotation, cam.rotation, k);
    }

    // ---------- modes ----------

    void ShowMenu()
    {
        Build();
        SetMode(Mode.Menu);
        anchor.SetPositionAndRotation(cam.TransformPoint(0f, 0f, distance), cam.rotation);
        VoiceGuide.Play("menu_welcome");
    }

    void Open(int option)
    {
        if (option == 0) SetMode(Mode.Robot);
        else if (option == 1) SetMode(Mode.Pavilion);
        else SetMode(Mode.Settings);
    }

    void SetMode(Mode mode)
    {
        var before = Current;
        Current = mode;
        if (calibration != null && calibration.IsCalibrating && mode != Mode.Settings) calibration.Cancel();

        menuPage.SetActive(mode == Mode.Menu);
        settingsPage.SetActive(mode == Mode.Settings);
        if (wall != null) wall.SetShown(mode == Mode.Robot);
        if (pavilion != null) pavilion.SetShown(mode == Mode.Pavilion);
        // Leaving the robot: stop the rehearsal, bricks back into the tray (after hiding the wall builder, so it
        // doesn't start talking again)
        if (before == Mode.Robot && mode != Mode.Robot && controls != null) controls.ResetBricks();
        ShowRobotParts(mode == Mode.Robot || mode == Mode.Settings);
    }

    // Hide/show the ghost robot and the tray. Remembers which renderers were on (passthrough hides some of them).
    void ShowRobotParts(bool show)
    {
        if (!show)
        {
            if (hiddenRenderers.Count > 0) return;
            foreach (var root in robotOnly)
                if (root != null)
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        hiddenRenderers[r] = r.enabled;
                        r.enabled = false;
                    }
        }
        else
        {
            foreach (var kv in hiddenRenderers)
                if (kv.Key != null) kv.Key.enabled = kv.Value;
            hiddenRenderers.Clear();
        }
    }

    // ---------- drawing the pages ----------

    void Blink()
    {
        for (int i = 0; i < optionMats.Count; i++)
        {
            var c = optionMats[i].color;
            c.a = i == selected ? 0.25f + 0.75f * (0.5f + 0.5f * Mathf.Cos(Time.time * 2f * Mathf.PI)) : 1f;
            optionMats[i].color = c;
        }
    }

    void Build()
    {
        if (anchor != null) return;
        cam = Camera.main != null ? Camera.main.transform : transform;
        anchor = new GameObject("Home Menu").transform;
        menuPage = new GameObject("Menu Page");
        settingsPage = new GameObject("Settings Page");
        menuPage.transform.SetParent(anchor, false);
        settingsPage.transform.SetParent(anchor, false);

        foreach (var page in new[] { menuPage.transform, settingsPage.transform })
        {
            Quad(page, null, new Vector2(1.5f, 1.05f), 0f, 4000, new Color(0.03f, 0.03f, 0.03f, 0.96f));   // dark panel, nearly solid so the text reads well
            foreach (var (tex, q) in new[] { (logo, 4001), (title, 4002), (subtitle, 4003) })
                Quad(page, tex, Size(tex, 1.1f, 0f), 0.36f, q, Color.white);
        }
        float[] ys = { 0.12f, -0.04f, -0.20f };
        for (int i = 0; i < options.Length; i++)
            optionMats.Add(Quad(menuPage.transform, options[i], Size(options[i], 0f, LineHeight), ys[i], 4004 + i, Color.white));
        Quad(menuPage.transform, hint, Size(hint, 0f, LineHeight * 0.55f), -0.40f, 4010, Color.white);
        Quad(settingsPage.transform, settingsTitle, Size(settingsTitle, 0f, LineHeight), 0.04f, 4004, Color.white);
        Quad(settingsPage.transform, settingsHint, Size(settingsHint, 0f, LineHeight * 0.7f), -0.18f, 4005, Color.white);
    }

    // Size of a text image: given width (height from the image) or given height (width from the image)
    static Vector2 Size(Texture2D tex, float width, float height)
    {
        if (tex == null) return Vector2.zero;
        float aspect = (float)tex.width / tex.height;
        return width > 0f ? new Vector2(width, width / aspect) : new Vector2(height * aspect, height);
    }

    Material Quad(Transform parent, Texture2D tex, Vector2 size, float y, int queue, Color colour)
    {
        var mat = new Material(overlayShader) { mainTexture = tex, color = colour, renderQueue = queue };
        if (size == Vector2.zero) return mat;
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(quad.GetComponent<Collider>());
        quad.name = tex != null ? tex.name : "Panel";
        quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = new Vector3(0f, y, 0f);
        quad.transform.localScale = new Vector3(size.x, size.y, 1f);
        return mat;
    }
}
