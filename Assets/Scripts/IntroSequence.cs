using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.XR;

// The opening of PRINT("HELLO, EARTH")³ (Jin, 23-24 Oct), played once when the app starts:
//   0 - 5.5 s  the 360 film of the group (StreamingAssets/intro_360.mp4, made by tools/make_intro.py) all around the
//              visitor, with the riser sound
//   5.5 s      black; the logo appears on the "da", the name on the "dam", then "Creative Robotics 2026"
//   8 s        the choices: A = build a wall with the robot (pavilion mode: coming soon) + voice "intro_welcome"
//   A          everything fades back to the real room and the wall builder asks the visitor to draw.
// Everything is drawn with PicoBricks/IntroOverlay (on top of the table, labels and ghost robot). If the film can't
// be loaded the intro starts at the logo. WallBuilder waits for Finished.
public class IntroSequence : MonoBehaviour
{
    public Shader overlayShader;
    public Texture2D logo, title, subtitle, choices;
    public AudioClip riser, logoSound;
    public string videoFile = "intro_360.mp4";
    public float filmLength = 5.5f;
    [Tooltip("Distance of the logo in front of the visitor (m) and its width")]
    public float distance = 3f, lockupWidth = 1.5f;

    /// <summary>True from app start until the visitor has chosen; other scripts keep quiet meanwhile.</summary>
    public static bool Playing { get; private set; }
    public static event Action Finished;

    Transform cam, sphere;
    Material sphereMat;
    Material logoMat, titleMat, subMat, choicesMat;
    RenderTexture filmTexture;
    VideoPlayer video;
    AudioSource music, hit;
    readonly System.Collections.Generic.List<GameObject> made = new System.Collections.Generic.List<GameObject>();

    void Awake() => Playing = true;

    void OnDestroy()
    {
        Playing = false;
        if (filmTexture != null) filmTexture.Release();
    }

    IEnumerator Start()
    {
        while (Camera.main == null) yield return null;
        cam = Camera.main.transform;
        yield return null; // one frame for the headset pose
        var yaw = Quaternion.Euler(0f, cam.eulerAngles.y, 0f);

        // Black dome around the visitor; the film is shown on it
        sphereMat = NewMaterial(null, Color.black, 4000);
        sphere = MakeObject("Intro Dome", SphereMesh(20f, 32, 64), sphereMat).transform;
        sphere.SetPositionAndRotation(cam.position, yaw);

        // Logo, name, subtitle (same-size layers, stacked) and the choices underneath, straight ahead
        var anchor = new GameObject("Intro Lockup").transform;
        made.Add(anchor.gameObject);
        anchor.SetPositionAndRotation(cam.position + yaw * Vector3.forward * distance + Vector3.up * 0.1f, yaw);
        logoMat = Layer(anchor, logo, lockupWidth, 0f, 4001);
        titleMat = Layer(anchor, title, lockupWidth, 0f, 4002);
        subMat = Layer(anchor, subtitle, lockupWidth, 0f, 4003);
        float lockupHeight = logo != null ? lockupWidth * logo.height / logo.width : 0.2f;
        float choicesWidth = lockupWidth * 0.75f;
        float choicesHeight = choices != null ? choicesWidth * choices.height / choices.width : 0.1f;
        choicesMat = Layer(anchor, choices, choicesWidth, -(lockupHeight / 2f + 0.14f + choicesHeight / 2f), 4004);

        music = gameObject.AddComponent<AudioSource>();
        hit = gameObject.AddComponent<AudioSource>();
        music.playOnAwake = hit.playOnAwake = false;
        music.spatialBlend = hit.spatialBlend = 0f;

        // Film: prepare first, so picture and sound start together
        bool filmOk = false, failed = false;
        video = sphere.gameObject.AddComponent<VideoPlayer>();
        video.playOnAwake = false;
        video.source = VideoSource.Url;
        video.url = Path.Combine(Application.streamingAssetsPath, videoFile);
        video.audioOutputMode = VideoAudioOutputMode.None;
        video.isLooping = false;
        video.skipOnDrop = true;
        filmTexture = new RenderTexture(4096, 2048, 0);
        video.renderMode = VideoRenderMode.RenderTexture;
        video.targetTexture = filmTexture;
        video.errorReceived += (v, msg) => { failed = true; Debug.LogWarning("PicoBricks intro: " + msg); };
        video.Prepare();
        float wait = Time.time;
        while (!video.isPrepared && !failed && Time.time - wait < 6f) yield return null;
        filmOk = video.isPrepared && !failed;

        float t0 = Time.time;
        if (filmOk)
        {
            sphereMat.mainTexture = filmTexture;
            sphereMat.color = Color.white;
            video.Play();
            if (riser != null) { music.clip = riser; music.Play(); }
            while (Time.time - t0 < filmLength - 0.03f) yield return null;
        }
        else
        {
            Debug.LogWarning("PicoBricks intro: film not available, starting at the logo");
        }

        // Black, then da-dam: the sound's "da" is 0.05 s in, the "dam" 0.37 s in
        video.Stop();
        sphereMat.mainTexture = null;
        sphereMat.color = Color.black;
        if (logoSound != null) { hit.clip = logoSound; hit.Play(); }
        StartCoroutine(Fade(logoMat, 0f, 1f, 0.12f, 0.05f));
        StartCoroutine(Fade(titleMat, 0f, 1f, 0.35f, 0.37f));
        StartCoroutine(Fade(subMat, 0f, 1f, 0.5f, 0.9f));
        yield return new WaitForSeconds(2.5f);

        StartCoroutine(Fade(choicesMat, 0f, 1f, 0.5f, 0f));
        VoiceGuide.Play("intro_welcome");

        // Wait for A (a press that starts now, not one still held)
        bool wasDown = true;
        while (true)
        {
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            right.TryGetFeatureValue(CommonUsages.primaryButton, out bool a);
            if (a && !wasDown) break;
            wasDown = a;
            yield return null;
        }

        // Back to the real room
        const float fadeOut = 0.8f;
        foreach (var m in new[] { logoMat, titleMat, subMat, choicesMat, sphereMat })
            StartCoroutine(Fade(m, 1f, 0f, fadeOut, 0f));
        yield return new WaitForSeconds(fadeOut + 0.05f);
        foreach (var go in made) Destroy(go);
        Playing = false;
        Finished?.Invoke();
        Destroy(this);
    }

    void LateUpdate()
    {
        if (sphere != null && cam != null) sphere.position = cam.position;   // the dome moves with the head, not turns
    }

    Material NewMaterial(Texture tex, Color colour, int queue)
    {
        var m = new Material(overlayShader) { mainTexture = tex, color = colour, renderQueue = queue };
        return m;
    }

    Material Layer(Transform anchor, Texture2D tex, float width, float y, int queue)
    {
        if (tex == null) return NewMaterial(null, Color.clear, queue);
        var mat = NewMaterial(tex, new Color(1f, 1f, 1f, 0f), queue);
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(quad.GetComponent<Collider>());
        quad.name = "Intro " + tex.name;
        quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
        quad.transform.SetParent(anchor, false);
        quad.transform.localPosition = new Vector3(0f, y, 0f);
        quad.transform.localScale = new Vector3(width, width * tex.height / tex.width, 1f);
        return mat;
    }

    GameObject MakeObject(string name, Mesh mesh, Material mat)
    {
        var go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        made.Add(go);
        return go;
    }

    static IEnumerator Fade(Material m, float from, float to, float seconds, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        var c = m.color;
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            c.a = Mathf.Lerp(from, to, t / seconds);
            m.color = c;
            yield return null;
        }
        c.a = to;
        m.color = c;
    }

    // Sphere seen from inside with equirectangular UVs: u = 0.5 straight ahead (+z), growing to the right; v = 1 at
    // the top, so the 360 film is not mirrored.
    static Mesh SphereMesh(float radius, int rows, int cols)
    {
        var verts = new Vector3[(rows + 1) * (cols + 1)];
        var uvs = new Vector2[verts.Length];
        for (int r = 0; r <= rows; r++)
        {
            float lat = Mathf.PI * ((float)r / rows - 0.5f);
            for (int c = 0; c <= cols; c++)
            {
                float lon = 2f * Mathf.PI * ((float)c / cols - 0.5f);
                int i = r * (cols + 1) + c;
                verts[i] = radius * new Vector3(Mathf.Sin(lon) * Mathf.Cos(lat), Mathf.Sin(lat), Mathf.Cos(lon) * Mathf.Cos(lat));
                uvs[i] = new Vector2((float)c / cols, (float)r / rows);
            }
        }
        var tris = new int[rows * cols * 6];
        int k = 0;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int a = r * (cols + 1) + c, b = a + cols + 1;
                tris[k++] = a; tris[k++] = a + 1; tris[k++] = b;
                tris[k++] = a + 1; tris[k++] = b + 1; tris[k++] = b;
            }
        var mesh = new Mesh { name = "Intro Dome", vertices = verts, uv = uvs, triangles = tris };
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * radius * 4f);
        return mesh;
    }
}
