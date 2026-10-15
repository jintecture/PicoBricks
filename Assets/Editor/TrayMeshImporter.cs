using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Turns the printable tray (hardware/pickup-tray/pickup_tray_bricks_1-6.stl, millimetres, z up) into a Unity mesh
// asset (metres, y up), so the AR scene uses exactly the tray we print. Unity can't import STL files itself.
public static class TrayMeshImporter
{
    public const string StlPath = "hardware/pickup-tray/pickup_tray_bricks_1-6.stl";
    public const string MeshPath = "Assets/Models/PickupTray.asset";

    public static Mesh ImportOrLoad()
    {
        string stl = Path.Combine(Path.GetDirectoryName(Application.dataPath), StlPath);
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        if (!File.Exists(stl))
        {
            Debug.LogWarning($"PicoBricks: tray STL not found at {stl}");
            return existing;
        }

        var mesh = ReadBinaryStl(stl);
        if (!AssetDatabase.IsValidFolder("Assets/Models"))
            AssetDatabase.CreateFolder("Assets", "Models");
        if (existing != null)
        {
            EditorUtility.CopySerialized(mesh, existing);
            AssetDatabase.SaveAssets();
            return existing;
        }
        AssetDatabase.CreateAsset(mesh, MeshPath);
        return mesh;
    }

    static Mesh ReadBinaryStl(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.ReadBytes(80);
        int count = (int)reader.ReadUInt32();
        var vertices = new Vector3[count * 3];
        var triangles = new int[count * 3];
        for (int t = 0; t < count; t++)
        {
            reader.ReadBytes(12); // facet normal, recalculated below
            for (int k = 0; k < 3; k++)
            {
                float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
                // Rhino (x right, y away, z up, mm) -> Unity (x right, y up, z away, m)
                vertices[t * 3 + k] = new Vector3(x, z, y) * 0.001f;
            }
            reader.ReadUInt16();
            // swapping y and z mirrors the shape's handedness, so flip the winding to keep faces pointing out
            triangles[t * 3] = t * 3;
            triangles[t * 3 + 1] = t * 3 + 2;
            triangles[t * 3 + 2] = t * 3 + 1;
        }

        var mesh = new Mesh { name = "PickupTray", indexFormat = IndexFormat.UInt32 };
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
