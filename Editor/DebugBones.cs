using UnityEngine;
using UnityEditor;

public static class DebugBones
{
    static System.Text.StringBuilder _sb;
    static void Log(string msg) { _sb.AppendLine(msg); }

    public static void DumpBodyBlockHierarchy()
    {
        _sb = new System.Text.StringBuilder();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/New Folder/Body Block.fbx");
        var instance = Object.Instantiate(prefab);

        Log("=== Body Block Hierarchy ===");
        DumpTransform(instance.transform, 0);

        // Also check for duplicate names
        var allTransforms = instance.GetComponentsInChildren<Transform>();
        var nameCount = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var t in allTransforms)
        {
            if (!nameCount.ContainsKey(t.name)) nameCount[t.name] = 0;
            nameCount[t.name]++;
        }

        Log("=== Duplicate Names ===");
        foreach (var kvp in nameCount)
        {
            if (kvp.Value > 1)
                Log($"  DUPLICATE: '{kvp.Key}' appears {kvp.Value} times");
        }

        // Dump X Bot bones for comparison
        var xbotPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/New Folder/X Bot.fbx");
        var xbotInstance = Object.Instantiate(xbotPrefab);
        var smr = xbotInstance.GetComponentInChildren<SkinnedMeshRenderer>();

        Log("=== X Bot SMR Bones (first 10) ===");
        for (int i = 0; i < Mathf.Min(10, smr.bones.Length); i++)
            Log($"  [{i}] '{smr.bones[i].name}' path='{GetPath(smr.bones[i])}'");

        // Check Body Block SMR bones
        var bbSmr = instance.GetComponentInChildren<SkinnedMeshRenderer>();
        if (bbSmr != null)
        {
            Log($"=== Body Block SMR Bones (ALL {bbSmr.bones.Length}) ===");
            for (int i = 0; i < bbSmr.bones.Length; i++)
                Log($"  [{i}] '{bbSmr.bones[i].name}'");

            // Check which X Bot bones are NOT in Body Block
            var bbBoneNames = new System.Collections.Generic.HashSet<string>();
            foreach (var b in bbSmr.bones) bbBoneNames.Add(b.name);
            Log("=== X Bot bones MISSING from Body Block SMR ===");
            for (int i = 0; i < smr.bones.Length; i++)
            {
                if (!bbBoneNames.Contains(smr.bones[i].name))
                    Log($"  [{i}] '{smr.bones[i].name}'");
            }
        }

        Object.DestroyImmediate(instance);
        Object.DestroyImmediate(xbotInstance);

        string outPath = System.IO.Path.GetFullPath("ExportedAssets/debug_bones.txt");
        System.IO.File.WriteAllText(outPath, _sb.ToString());
        Debug.Log($"[DebugBones] Written to {outPath}");
    }

    static void DumpTransform(Transform t, int depth)
    {
        string indent = new string(' ', depth * 2);
        Log($"{indent}'{t.name}' children={t.childCount}");
        for (int i = 0; i < t.childCount; i++)
            DumpTransform(t.GetChild(i), depth + 1);
    }

    static string GetPath(Transform t)
    {
        string path = t.name;
        var parent = t.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }
}
