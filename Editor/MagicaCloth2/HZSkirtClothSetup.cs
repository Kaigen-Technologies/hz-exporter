using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using MagicaCloth2;

// Sets up a MagicaCloth MeshCloth on the skirt in SkirtDemo.unity with the same
// settings as the engine's demo_cloth_skirt.c (skirt_params + pin_top_band 0.06 +
// reduction simple_distance 0.07). Run via: exec:HZSkirtClothSetup.Setup
public static class HZSkirtClothSetup
{
    const string ScenePath =
        "Assets/MagicaCloth2/Example (Can be deleted)/DemoScenes/URP/SkirtDemo.unity";
    const string SkirtFbxPath =
        "Assets/MagicaCloth2/Example (Can be deleted)/UnityChan/UnityChanKAGURA/skirt.fbx";
    const string ClothObjectName = "Magica Cloth (skirt)";

    public static void Setup()
    {
        // MC2 hard-errors on non-readable meshes (RenderSetupData.cs:301)
        var importer = AssetImporter.GetAtPath(SkirtFbxPath) as ModelImporter;
        if (importer != null && !importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
            Debug.Log("[HZSkirtClothSetup] enabled Read/Write on skirt.fbx");
        }

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var skirtGo = GameObject.Find("skirt");
        if (skirtGo == null)
        {
            Debug.LogError("[HZSkirtClothSetup] 'skirt' object not found in scene");
            return;
        }
        var renderer = skirtGo.GetComponentInChildren<Renderer>();
        var mesh = GetMesh(renderer);
        if (renderer == null || mesh == null)
        {
            Debug.LogError("[HZSkirtClothSetup] no renderer/mesh under 'skirt'");
            return;
        }

        var old = GameObject.Find(ClothObjectName);
        if (old != null)
            Object.DestroyImmediate(old);

        // Separate GameObject at identity (mirrors the hand-authored KAGURA
        // setup) so cloth local space == world space and the skirt instance's
        // ~100x fbx scale never touches the cloth transform.
        var clothGo = new GameObject(ClothObjectName);
        var cloth = clothGo.AddComponent<MagicaCloth>();
        var sd = cloth.SerializeData;

        sd.clothType = ClothProcess.ClothType.MeshCloth;
        sd.sourceRenderers = new List<Renderer> { renderer };
        sd.reductionSetting.simpleDistance = 0.07f;

        // demo skirt_params(): mc2 defaults + these overrides
        sd.gravity = 3.0f;
        sd.inertiaConstraint.movementInertiaSmoothing = 0.0f;
        sd.angleRestorationConstraint.useAngleRestoration = true;
        sd.angleRestorationConstraint.stiffness.SetValue(0.02f, 1.0f, 0.2f, true);
        sd.triangleBendingConstraint.stiffness = 0.05f;
        sd.selfCollisionConstraint.selfMode = SelfCollisionConstraint.SelfCollisionMode.None;

        // pin_top_band 0.06: top 6% of world-Y extent -> Fixed, rest Move.
        // Cloth GO is at identity so selection positions are world positions.
        var l2w = renderer.transform.localToWorldMatrix;
        var verts = mesh.vertices;
        int n = verts.Length;
        var wpos = new Vector3[n];
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            wpos[i] = l2w.MultiplyPoint3x4(verts[i]);
            minY = Mathf.Min(minY, wpos[i].y);
            maxY = Mathf.Max(maxY, wpos[i].y);
        }
        float bandY = maxY - (maxY - minY) * 0.06f;

        var selection = new SelectionData(n);
        int fixedCount = 0;
        for (int i = 0; i < n; i++)
        {
            selection.positions[i] = wpos[i];
            bool pin = wpos[i].y >= bandY;
            selection.attributes[i] = pin ? VertexAttribute.Fixed : VertexAttribute.Move;
            if (pin) fixedCount++;
        }

        var tris = mesh.triangles;
        float maxEdge = 0;
        for (int t = 0; t < tris.Length; t += 3)
        {
            maxEdge = Mathf.Max(maxEdge, Vector3.Distance(wpos[tris[t]], wpos[tris[t + 1]]));
            maxEdge = Mathf.Max(maxEdge, Vector3.Distance(wpos[tris[t + 1]], wpos[tris[t + 2]]));
            maxEdge = Mathf.Max(maxEdge, Vector3.Distance(wpos[tris[t + 2]], wpos[tris[t]]));
        }
        selection.maxConnectionDistance = maxEdge;
        selection.userEdit = true;
        cloth.GetSerializeData2().selectionData = selection;

        EditorUtility.SetDirty(cloth);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[HZSkirtClothSetup] done. verts={n} fixed={fixedCount} " +
                  $"maxEdge={maxEdge:F4} yband=[{bandY:F3}..{maxY:F3}]");
    }

    public static void AddSway()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var skirtGo = GameObject.Find("skirt");
        if (skirtGo == null)
        {
            Debug.LogError("[HZSkirtClothSetup] 'skirt' object not found in scene");
            return;
        }
        // HZSkirtSway lives in Assembly-CSharp; access by string to avoid cross-assembly reference.
        var swayType = System.Type.GetType("HZSkirtSway, Assembly-CSharp");
        if (swayType != null)
        {
            var sway = skirtGo.GetComponent(swayType) ?? skirtGo.AddComponent(swayType);
            swayType.GetField("amplitude")?.SetValue(sway, 0.2f);
            swayType.GetField("frequency")?.SetValue(sway, 1.2f);
        }
        else
        {
            Debug.LogWarning("[HZSkirtClothSetup] HZSkirtSway not found in Assembly-CSharp; skipping sway setup.");
        }
        EditorUtility.SetDirty(skirtGo);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[HZSkirtClothSetup] sway added. amplitude=0.2 frequency=1.2");
    }

    static Mesh GetMesh(Renderer r)
    {
        if (r is SkinnedMeshRenderer smr)
            return smr.sharedMesh;
        var mf = r != null ? r.GetComponent<MeshFilter>() : null;
        return mf != null ? mf.sharedMesh : null;
    }
}
