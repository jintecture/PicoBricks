using UnityEngine;

// A dark see-through panel with rounded corners behind a floating text (TextMesh), so instructions stay readable in
// front of bright walls and tables (Jin, 26 Oct: 45 % black). It follows the text's size every frame and hides when
// the text is empty. Add with LabelPanel.Add(textMesh).
[RequireComponent(typeof(TextMesh))]
public class LabelPanel : MonoBehaviour
{
    const float Opacity = 0.45f;
    const float Padding = 0.25f;    // around the text, in line heights
    const float Corner = 0.3f;      // corner radius, in line heights
    const int CornerSteps = 6;

    TextMesh text;
    MeshRenderer textRenderer;
    MeshFilter panelFilter;
    MeshRenderer panelRenderer;
    Mesh mesh;
    Bounds last;

    public static LabelPanel Add(TextMesh text)
    {
        var p = text.GetComponent<LabelPanel>();
        return p != null ? p : text.gameObject.AddComponent<LabelPanel>();
    }

    void Awake()
    {
        text = GetComponent<TextMesh>();
        textRenderer = GetComponent<MeshRenderer>();
        var go = new GameObject("Text Panel");
        go.transform.SetParent(transform, false);
        panelFilter = go.AddComponent<MeshFilter>();
        panelRenderer = go.AddComponent<MeshRenderer>();
        panelRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        panelRenderer.receiveShadows = false;
        // Same shader as the intro (always in the build); here hidden behind nearer objects, drawn just before the text
        var shader = Shader.Find("PicoBricks/IntroOverlay");
        if (shader != null)
        {
            var mat = new Material(shader) { color = new Color(0f, 0f, 0f, Opacity), renderQueue = 2999 };
            mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.LessEqual);
            panelRenderer.sharedMaterial = mat;
        }
        mesh = new Mesh { name = "Text Panel" };
        panelFilter.sharedMesh = mesh;
    }

    void LateUpdate()
    {
        bool show = textRenderer.enabled && !string.IsNullOrEmpty(text.text) && panelRenderer.sharedMaterial != null;
        panelRenderer.enabled = show;
        if (!show) return;
        var b = textRenderer.localBounds;   // the text's size in its own space
        if (b.size == last.size && b.center == last.center) return;
        last = b;
        Rebuild(b);
    }

    // Rounded rectangle around the text, a hair behind it (the text faces -z towards the viewer)
    void Rebuild(Bounds b)
    {
        int lines = Mathf.Max(1, text.text.Split('\n').Length);
        float lineHeight = b.size.y / lines;
        float pad = lineHeight * Padding, r = lineHeight * Corner;
        float x0 = b.min.x - pad, x1 = b.max.x + pad, y0 = b.min.y - pad, y1 = b.max.y + pad;
        r = Mathf.Min(r, (x1 - x0) / 2f, (y1 - y0) / 2f);
        float z = b.center.z + 0.002f;
        var outline = new System.Collections.Generic.List<Vector3>();
        Vector2[] centres = { new Vector2(x1 - r, y1 - r), new Vector2(x0 + r, y1 - r), new Vector2(x0 + r, y0 + r), new Vector2(x1 - r, y0 + r) };
        for (int c = 0; c < 4; c++)
            for (int s = 0; s <= CornerSteps; s++)
            {
                float a = (c * 90f + 90f * s / CornerSteps) * Mathf.Deg2Rad;
                outline.Add(new Vector3(centres[c].x + Mathf.Cos(a) * r, centres[c].y + Mathf.Sin(a) * r, z));
            }
        var verts = new Vector3[outline.Count + 1];
        verts[0] = new Vector3(b.center.x, b.center.y, z);
        for (int i = 0; i < outline.Count; i++) verts[i + 1] = outline[i];
        var tris = new int[outline.Count * 3];
        for (int i = 0; i < outline.Count; i++)
        {
            tris[i * 3] = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = (i + 1) % outline.Count + 1;
        }
        mesh.Clear();
        mesh.vertices = verts;
        mesh.triangles = tris;   // the shader draws both sides (Cull Off)
        mesh.RecalculateBounds();
    }
}
