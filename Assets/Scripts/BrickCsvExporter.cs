
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public class BrickCsvExporter : MonoBehaviour
{
    [Header("Assign scene objects")]
    public Transform workspace;
    public Transform bricksParent;
    public Transform table;

    [ContextMenu("Export Scene Objects CSV")]
    public void ExportCsv()
    {
        if (workspace == null ||
            bricksParent == null ||
            table == null)
        {
            Debug.LogError(
                "Assign Workspace, Bricks Parent, and Table.");
            return;
        }

        var csv = new StringBuilder();

        csv.AppendLine(
            "object_id,object_type," +
            "x_m,y_m,z_m," +
            "qx,qy,qz,qw," +
            "size_x_m,size_y_m,size_z_m," +
            "frame");

        AddObjectRow(csv, table, "table");

        Transform[] all =
            bricksParent.GetComponentsInChildren<Transform>(true);

        foreach (Transform item in all)
        {
            if (item == bricksParent)
                continue;

            if (!item.name.StartsWith(
                "Brick_", StringComparison.Ordinal))
                continue;

            AddObjectRow(csv, item, "brick");
        }

        string path = Path.Combine(
            Application.persistentDataPath,
            "scene_objects.csv");

        File.WriteAllText(path, csv.ToString());

        Debug.Log($"Exported scene objects to: {path}");
    }

    void AddObjectRow(
        StringBuilder csv,
        Transform item,
        string type)
    {
        // Position relative to calibrated Workspace.
        Vector3 unityPos =
            workspace.InverseTransformPoint(item.position);

        // Swap Y and Z coordinates.
        Vector3 p = new Vector3(
            unityPos.x,
            unityPos.z,
            unityPos.y
        );

        // Orientation relative to Workspace.
        Quaternion unityRot =
            Quaternion.Inverse(workspace.rotation)
            * item.rotation;

        // Convert rotation to the swapped coordinate basis:
        // (x, y, z) -> (x, z, y).
        // This reflection changes handedness, so quaternion
        // components must be transformed accordingly.
        Quaternion q = new Quaternion(
            -unityRot.x,
            -unityRot.z,
            -unityRot.y,
            unityRot.w
        );

        // Swap Y and Z dimensions.
        Vector3 unitySize = item.localScale;

        Vector3 size = new Vector3(
            unitySize.x,
            unitySize.z,
            unitySize.y
        );

        csv.AppendLine(string.Join(",", new string[]
        {
            item.name,
            type,
            F(p.x), F(p.y), F(p.z),
            F(q.x), F(q.y), F(q.z), F(q.w),
            F(size.x), F(size.y), F(size.z),
            "workspace_swapped_yz"
        }));
    }

    static string F(float value)
    {
        return value.ToString(
            "G9", CultureInfo.InvariantCulture);
    }
}
