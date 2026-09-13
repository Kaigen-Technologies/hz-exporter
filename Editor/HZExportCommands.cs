using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.Scenes;
using HZExporter;

/// <summary>
/// CLI-callable export commands. Each public static method can be invoked via remote TCP:
///   unity_cmd.ps1 -cmd "exec:HZExportCommands.MethodName"
/// </summary>
public static class HZExportCommands
{
    // Local staging dir, resolved from config (default ExportedAssets/ under project root).
    static string OutputDir => HZExporter.HZExporterConfig.Instance.ResolvedOutputDir();

    // Asset-relative prefix (how the engine sees the paths, relative to assets/)
    const string AssetPrefix = "unity";

    /// <summary>Copy the local staging dir into the configured engine project
    /// (merge/overwrite). No-op with a warning when no engine path is set.</summary>
    [MenuItem("Hz Exporter/Copy to Engine")]
    public static void CopyToEngine()
    {
        var cfg = HZExporter.HZExporterConfig.Instance;
        string dest = cfg.ResolvedEngineDir();
        if (string.IsNullOrEmpty(dest))
        {
            Debug.LogWarning("[HZ] No engine project path configured (Hz Exporter/Configure); skipping copy.");
            return;
        }
        string src = OutputDir;
        if (!System.IO.Directory.Exists(src))
        {
            Debug.LogWarning($"[HZ] Nothing to copy — staging dir not found: {src}");
            return;
        }
        int files = CopyDirMerge(src, dest);
        Debug.Log($"[HZ] Copied {files} file(s) to engine: {dest}");
    }

    [MenuItem("Hz Exporter/Play Engine")]
    public static void PlayEngine() => HZExporter.HZEnginePlayer.Play();

    [MenuItem("Hz Exporter/Play Engine", true)]
    static bool PlayEngineValidate() => !HZExporter.HZEnginePlayer.IsRunning;

    [MenuItem("Hz Exporter/Stop Engine")]
    public static void StopEngine() => HZExporter.HZEnginePlayer.Stop();

    [MenuItem("Hz Exporter/Stop Engine", true)]
    static bool StopEngineValidate() => HZExporter.HZEnginePlayer.IsRunning;

    // Recursively copy src → dst, overwriting existing files, leaving other dst files intact.
    static int CopyDirMerge(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        int count = 0;
        foreach (var file in System.IO.Directory.GetFiles(src))
        {
            System.IO.File.Copy(file, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(file)), true);
            count++;
        }
        foreach (var dir in System.IO.Directory.GetDirectories(src))
            count += CopyDirMerge(dir, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(dir)));
        return count;
    }

    /// <summary>Translate the BasicShader real-world test graph
    /// (Assets/yyyrjis/ModularAnimeCharacter1/Shaders/BasicShader.shadergraph).</summary>
    public static void ExportBasicShader()
    {
        const string src = "Assets/yyyrjis/ModularAnimeCharacter1/Shaders/BasicShader.shadergraph";
        string outDir = OutputDir + "/shaders";
        try { HZShaderGraphExporter.ExportFile(src, outDir); }
        catch (System.Exception e) { Debug.LogError($"[HZ-SG] BasicShader: {e.Message}\n{e.StackTrace}"); }
    }

    public static void ExportCharacterBaseShader()
    {
        const string src = "Assets/yyyrjis/ModularAnimeCharacter1/Shaders/CharacterBaseShader.shadergraph";
        string outDir = OutputDir + "/shaders";
        try { HZShaderGraphExporter.ExportFile(src, outDir); }
        catch (System.Exception e) { Debug.LogError($"[HZ-SG] CharacterBaseShader: {e.Message}\n{e.StackTrace}"); }
    }

    public static void ExportSurfaceShader()
    {
        const string src = "Assets/Idyllic Fantasy Nature/Shader/Surface.shadergraph";
        string outDir = OutputDir + "/shaders";
        try { HZShaderGraphExporter.ExportFile(src, outDir); }
        catch (System.Exception e) { Debug.LogError($"[HZ-SG] Surface: {e.Message}\n{e.StackTrace}"); }
    }

    public static void ExportVegetationShader()
    {
        const string src = "Assets/Idyllic Fantasy Nature/Shader/Vegetation.shadergraph";
        string outDir = OutputDir + "/shaders";
        try { HZShaderGraphExporter.ExportFile(src, outDir); }
        catch (System.Exception e) { Debug.LogError($"[HZ-SG] Vegetation: {e.Message}\n{e.StackTrace}"); }
    }

    public static void ExportWaterShader()
    {
        const string src = "Assets/Idyllic Fantasy Nature/Shader/Water.shadergraph";
        string outDir = OutputDir + "/shaders";
        try { HZShaderGraphExporter.ExportFile(src, outDir); }
        catch (System.Exception e) { Debug.LogError($"[HZ-SG] Water: {e.Message}\n{e.StackTrace}"); }
    }

    public static void ExportWiggleShader()
    {
        const string src = "Assets/Boids/SampleAssets/Shaders/Wiggle.shadergraph";
        string outDir = OutputDir + "/shaders";
        try { HZShaderGraphExporter.ExportFile(src, outDir); }
        catch (System.Exception e) { Debug.LogError($"[HZ-SG] Wiggle: {e.Message}\n{e.StackTrace}"); }
    }

    // Folders the "Export Shader Graphs" menu walks. Add more here as new
    /// <summary>Translate every .shadergraph referenced in shader_mapping.json into a
    /// .hz.glsl file under ExportedAssets/shaders/. Resolves paths via Shader.Find so
    /// there are no hardcoded folder paths.</summary>
    [MenuItem("Hz Exporter/Export Shader Graphs")]
    public static void ExportShaderGraphs()
    {
        int ok = 0, fail = 0;
        foreach (var shaderName in HZShaderMap.MappedShaderNames())
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) continue;
            var path = AssetDatabase.GetAssetPath(shader);
            if (!path.EndsWith(".shadergraph", System.StringComparison.OrdinalIgnoreCase)) continue;
            string outDir = ShaderOutDirFor(shaderName);
            try { HZShaderGraphExporter.ExportFile(path, outDir); ok++; }
            catch (System.Exception e) { Debug.LogError($"[HZ-SG] {System.IO.Path.GetFileName(path)}: {e.Message}\n{e.StackTrace}"); fail++; }
        }
        Debug.Log($"[HZ-SG] done — {ok} ok, {fail} failed");
    }

    /// <summary>Directory a translated ShaderGraph must land in so the engine
    /// resolves the `shader_source` its materials carry. The mapping's
    /// engineShader path is authoritative: `/shaders/unity/water.hz.glsl` means
    /// `<shaderRootDir>/unity/`. Without a configured shader root the shaders
    /// stage under the output dir instead.</summary>
    static string ShaderOutDirFor(string unityShaderName)
    {
        string root = HZExporter.HZExporterConfig.Instance.ResolvedShaderRootDir();
        if (string.IsNullOrEmpty(root)) return OutputDir + "/shaders";

        var entry = HZShaderMap.LookupByName(unityShaderName);
        string enginePath = entry != null ? entry.engineShader : null;
        // strip the leading "/shaders/" the engine paths carry, keep the subdirs
        const string prefix = "/shaders/";
        if (string.IsNullOrEmpty(enginePath) || !enginePath.StartsWith(prefix, System.StringComparison.Ordinal))
            return root;
        string rel = enginePath.Substring(prefix.Length);
        int slash = rel.LastIndexOf('/');
        return slash < 0 ? root : System.IO.Path.Combine(root, rel.Substring(0, slash));
    }

    /// <summary>Test-export a cube with a default URP/Lit material through the grass path,
    /// so we can verify default grass materials become grass_terrain custom materials.</summary>
    public static void ExportGrassTerrainMatTest()
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.name = "GrassDefaultTest";
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        HZGlbWriter.ExportStaticMesh(go.GetComponent<MeshFilter>().sharedMesh,
            new Material[] { mat },
            OutputDir + "/glb/GrassTerrainMat_Test.glb", grassShader: true);
        Object.DestroyImmediate(go);
        Object.DestroyImmediate(mat);
        Debug.Log("[HZ] grass-terrain-mat test exported");
    }

    /// <summary>Log the active terrain's TREE data (treePrototypes + treeInstances) — a
    /// separate system from grass details — to scope tree export.</summary>
    public static void DebugTerrainTrees()
    {
        var terrain = UnityEngine.Object.FindObjectOfType<Terrain>();
        if (terrain == null) { Debug.LogError("[HZtree] no Terrain"); return; }
        var td = terrain.terrainData;
        Debug.Log($"[HZtree] terrain '{td.name}' pos={terrain.transform.position} size={td.size} " +
                  $"treePrototypes={td.treePrototypes.Length} treeInstances={td.treeInstances.Length} " +
                  $"treeDistance={terrain.treeDistance} treeBillboardDistance={terrain.treeBillboardDistance}");
        for (int i = 0; i < td.treePrototypes.Length; i++)
        {
            var tp = td.treePrototypes[i];
            var prefab = tp.prefab;
            var lodg = prefab ? prefab.GetComponent<LODGroup>() : null;
            var mfs = prefab ? prefab.GetComponentsInChildren<MeshFilter>(true) : new MeshFilter[0];
            var smrs = prefab ? prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true) : new SkinnedMeshRenderer[0];
            Debug.Log($"[HZtree] proto{i} prefab='{(prefab ? prefab.name : "NULL")}' bend={tp.bendFactor} " +
                      $"LODGroup={(lodg ? lodg.lodCount + " lods" : "none")} meshFilters={mfs.Length} skinned={smrs.Length}");
        }
        // per-prototype instance count + a couple sample transforms
        var counts = new int[Mathf.Max(1, td.treePrototypes.Length)];
        foreach (var ti in td.treeInstances) if (ti.prototypeIndex < counts.Length) counts[ti.prototypeIndex]++;
        for (int i = 0; i < counts.Length; i++) Debug.Log($"[HZtree] proto{i}: {counts[i]} instances");
        int n = Mathf.Min(3, td.treeInstances.Length);
        for (int i = 0; i < n; i++)
        {
            var ti = td.treeInstances[i];
            var world = terrain.transform.position + Vector3.Scale(ti.position, td.size);
            Debug.Log($"[HZtree] inst{i} proto={ti.prototypeIndex} normPos={ti.position} world={world} " +
                      $"wScale={ti.widthScale} hScale={ti.heightScale} rot={ti.rotation:F2}");
        }
    }

    /// <summary>Test-export a cube using the FloorTriplanar shader-graph material so we can
    /// verify HZShaderMap picks up the new JSON entry and routes _MainTexture/_Normal/etc.</summary>
    public static void ExportFloorTriplanarTest()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(
            "Assets/StylizedCharacterPack/Materials/Shaders/MetalPannel_ShaderMat.mat");
        if (mat == null) { Debug.LogError("[HZ] MetalPannel_ShaderMat.mat not found"); return; }
        Debug.Log($"[HZ] Testing shader='{mat.shader.name}'");
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        HZGlbWriter.ExportStaticMesh(go.GetComponent<MeshFilter>().sharedMesh,
            new Material[] { mat },
            OutputDir + "/glb/FloorTriplanar_Test.glb");
        Object.DestroyImmediate(go);
    }

    /// <summary>Export the active scene's skybox to an equirect Radiance .hdr.
    /// Engine cooks .hdr → cubemap KTX + HdrEnvironmentBlobAsset on its side.</summary>
    public static void ExportSkybox()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        if (string.IsNullOrEmpty(sceneName)) sceneName = "Skybox";
        string outPath = OutputDir + "/environments/" + sceneName + "_skybox.hdr";
        HZSkyboxExporter.Export(outPath);
    }

    public static void ExportTestCube()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var meshFilter = go.GetComponent<MeshFilter>();
        var meshRenderer = go.GetComponent<MeshRenderer>();
        var mesh = meshFilter.sharedMesh;
        var mat = meshRenderer.sharedMaterial;

        HZGlbWriter.ExportStaticMesh(mesh, mat, OutputDir + "/meshes/Cube.glb");

        Object.DestroyImmediate(go);
        Debug.Log("[HZ] Cube GLB export done!");
    }

    public static void ExportTexturedCube()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var meshFilter = go.GetComponent<MeshFilter>();
        var mesh = meshFilter.sharedMesh;

        // Create material with a texture
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/TutorialInfo/Icons/URP.png");
        if (tex == null)
        {
            Debug.LogError("[HZ] Could not find URP.png texture");
            Object.DestroyImmediate(go);
            return;
        }

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetTexture("_BaseMap", tex);
        mat.SetColor("_BaseColor", Color.white);

        string matAssetPath = ExportMaterial(mat, "TexturedCube", false);
        HZMeshExporter.Export(mesh, matAssetPath, OutputDir + "/meshes/TexturedCube.hza");

        Object.DestroyImmediate(mat);
        Object.DestroyImmediate(go);
        Debug.Log("[HZ] Textured cube export done!");
    }

    public static void ExportXBot()
    {
        ExportFbxMeshes("Assets/New Folder/X Bot.fbx", "X_Bot");
    }

    public static void ExportXBotGlb()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/New Folder/X Bot.fbx");
        var instance = Object.Instantiate(prefab);

        var allSmrs = instance.GetComponentsInChildren<SkinnedMeshRenderer>();
        var validSmrs = new List<SkinnedMeshRenderer>();
        foreach (var s in allSmrs)
            if (s.sharedMesh != null && s.bones != null && s.bones.Length > 0)
                validSmrs.Add(s);

        if (validSmrs.Count == 0)
        {
            Debug.LogError("[HZ] No valid SMRs found on X Bot");
            Object.DestroyImmediate(instance);
            return;
        }

        SkinnedMeshRenderer primarySmr = validSmrs[0];
        foreach (var s in validSmrs)
            if (s.bones.Length > primarySmr.bones.Length)
                primarySmr = s;

        var smrArray = validSmrs.ToArray();
        var mats = new Material[validSmrs.Count][];
        for (int i = 0; i < validSmrs.Count; i++)
            mats[i] = validSmrs[i].sharedMaterials;

        HZGlbWriter.ExportSkinnedMesh(smrArray, mats, primarySmr, instance.transform, OutputDir + "/glb/XBot.glb");

        Object.DestroyImmediate(instance);
        Debug.Log("[HZ] X Bot GLB export done!");
    }

    /// <summary>
    /// Loads an FBX at assetPath, finds all Mesh sub-assets, and exports each as a
    /// ModelBlobAsset. Detects SkinnedMeshRenderer to export with skinning data.
    /// </summary>
    static void ExportFbxMeshes(string assetPath, string prefix)
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
        if (assets == null || assets.Length == 0)
        {
            Debug.LogError($"[HZ] No assets found at {assetPath}");
            return;
        }

        // Instantiate the FBX to access SkinnedMeshRenderers (they need a live hierarchy)
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        GameObject instance = null;
        if (prefab != null)
            instance = Object.Instantiate(prefab);

        // Build a map of mesh name → SkinnedMeshRenderer from the instance
        var smrByMeshName = new Dictionary<string, SkinnedMeshRenderer>();
        if (instance != null)
        {
            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh != null)
                    smrByMeshName[smr.sharedMesh.name] = smr;
            }
        }

        int meshCount = 0;
        foreach (var asset in assets)
        {
            if (asset is Mesh mesh && mesh.vertexCount > 0)
                meshCount++;
        }

        if (meshCount == 0)
        {
            Debug.LogError($"[HZ] No meshes found in {assetPath}");
            if (instance != null) Object.DestroyImmediate(instance);
            return;
        }

        int exported = 0;
        foreach (var asset in assets)
        {
            if (asset is Mesh mesh && mesh.vertexCount > 0)
            {
                string safeName = meshCount == 1
                    ? prefix
                    : $"{prefix}_{SanitizeName(mesh.name)}";
                string filename = safeName + ".hza";
                string outputPath = $"{OutputDir}/meshes/{filename}";

                bool skinned = smrByMeshName.ContainsKey(mesh.name);

                // Export material
                Material mat = null;
                if (skinned && smrByMeshName.TryGetValue(mesh.name, out var smr))
                {
                    mat = smr.sharedMaterial;
                }
                string matAssetPath = ExportMaterial(mat, safeName, skinned);

                if (skinned)
                    HZMeshExporter.Export(smrByMeshName[mesh.name], matAssetPath, outputPath);
                else
                    HZMeshExporter.Export(mesh, matAssetPath, outputPath);

                exported++;
            }
        }

        if (instance != null)
            Object.DestroyImmediate(instance);

        Debug.Log($"[HZ] Exported {exported} mesh(es) from {assetPath}");
    }

    /// <summary>
    /// Export a material and its textures. Returns the asset-relative path to the material .hza.
    /// </summary>
    static string ExportMaterial(Material mat, string baseName, bool skinned)
    {
        string matFilename = $"{baseName}_mat.hzt";
        string matOutputPath = $"{OutputDir}/materials/{matFilename}";
        string matAssetPath = $"{AssetPrefix}/{matFilename}";

        // Check for albedo texture
        string texAssetPath = null;
        if (mat != null)
        {
            Texture2D albedo = null;
            if (mat.HasProperty("_BaseMap"))
                albedo = mat.GetTexture("_BaseMap") as Texture2D;
            else if (mat.HasProperty("_MainTex"))
                albedo = mat.GetTexture("_MainTex") as Texture2D;

            if (albedo != null)
            {
                string texName = $"{baseName}_albedo";
                string texHztPath = $"{OutputDir}/textures/{texName}_tex.hzt";
                string texPngPath = $"{OutputDir}/textures/{texName}.png";
                texAssetPath = $"{AssetPrefix}/{texName}_tex.hzt";

                string imageBasePath = $"/{AssetPrefix}/{texName}";

                bool hasAlpha = albedo.format == TextureFormat.RGBA32 ||
                                albedo.format == TextureFormat.ARGB32 ||
                                albedo.format == TextureFormat.DXT5 ||
                                albedo.alphaIsTransparency;

                HZTextureExporter.ExportHzt(albedo, imageBasePath, true, hasAlpha, texHztPath, texPngPath);
            }
        }

        HZMaterialExporter.ExportHzt(mat, skinned, texAssetPath, matOutputPath);
        return matAssetPath;
    }

    public static void ExportBodyBlock()
    {
        // Get skeleton bone order from X Bot — must match the model .hza we exported.
        var xbotPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/New Folder/X Bot.fbx");
        var xbotInstance = Object.Instantiate(xbotPrefab);

        SkinnedMeshRenderer smr = null;
        foreach (var s in xbotInstance.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (s.sharedMesh != null && s.sharedMesh.name == "Beta_Surface")
            {
                smr = s;
                break;
            }
        }

        if (smr == null || smr.bones == null || smr.bones.Length == 0)
        {
            Debug.LogError("[HZ] No Beta_Surface SkinnedMeshRenderer found on X Bot");
            Object.DestroyImmediate(xbotInstance);
            return;
        }

        Debug.Log($"[HZ] Using X Bot '{smr.sharedMesh.name}' SMR with {smr.bones.Length} bones");

        var boneNameToIndex = new Dictionary<string, int>();
        for (int i = 0; i < smr.bones.Length; i++)
            boneNameToIndex[smr.bones[i].name] = i;

        int jointCount = smr.bones.Length;
        Object.DestroyImmediate(xbotInstance);

        var clips = AssetDatabase.LoadAllAssetsAtPath("Assets/New Folder/Body Block.fbx");
        AnimationClip clip = null;
        foreach (var asset in clips)
        {
            if (asset is AnimationClip ac && !ac.name.StartsWith("__preview__"))
            {
                clip = ac;
                break;
            }
        }

        if (clip == null)
        {
            Debug.LogError("[HZ] No AnimationClip found in Body Block.fbx");
            return;
        }

        var bbPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/New Folder/Body Block.fbx");
        var bbInstance = Object.Instantiate(bbPrefab);
        var bbSmr = bbInstance.GetComponentInChildren<SkinnedMeshRenderer>();

        var animBoneMap = new Dictionary<string, int>();
        if (bbSmr != null)
        {
            foreach (var bone in bbSmr.bones)
            {
                if (bone != null && boneNameToIndex.TryGetValue(bone.name, out int idx))
                    animBoneMap[bone.name] = idx;
            }
        }

        Debug.Log($"[HZ] Matched {animBoneMap.Count} Body Block bones to X Bot skeleton (XBot has {jointCount}, BB has {bbSmr?.bones.Length ?? 0})");

        string outputPath = $"{OutputDir}/animations/Body_Block.hza";
        HZAnimationExporter.Export(clip, bbInstance, animBoneMap, jointCount, outputPath);

        Object.DestroyImmediate(bbInstance);
        Debug.Log("[HZ] Body Block animation export done!");
    }

    static ulong StableIdFor(GameObject go)
    {
        string gid = UnityEditor.GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
        ulong h = HZBlobWriter.FNV1a(gid);
        return h == 0 ? 1 : h;
    }

    static ulong StableIdForString(string key)
    {
        ulong h = HZBlobWriter.FNV1a(key);
        return h == 0 ? 1 : h;
    }

    /// <summary>
    /// Open Boids.unity (single) and additively open every SubScene it references
    /// (Simulation / Environment), so the entire scene graph is walkable in one editor
    /// session. The Entities SubScene component keeps its content in separate .unity
    /// files that aren't loaded by a plain OpenScene — we resolve each SubScene's
    /// SceneAsset and open it additively.
    /// </summary>
    public static void OpenBoidsScene()
    {
        OpenSceneWithSubScenes("Assets/Boids/Boids.unity");
    }

    // Open SkirtDemo (the KAGURA_MC2 cloth test scene) and run the full scene export.
    public static void ExportSkirtDemo()
    {
        const string scenePath =
            "Assets/MagicaCloth2/Example (Can be deleted)/DemoScenes/URP/SkirtDemo.unity";
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath,
            UnityEditor.SceneManagement.OpenSceneMode.Single);
        ExportScene();
    }

    // Scene requested by the CLI; empty/missing = export whatever is already open.
    // RemoteRefresh can only invoke no-arg statics, so the argument arrives here.
    const string TargetScenePath = "UserSettings/HzExportTarget.txt";

    /// <summary>Export the scene named in UserSettings/HzExportTarget.txt, or the
    /// active scene when that file is absent or blank. CLI entry point.</summary>
    public static void ExportTargetScene()
    {
        string want = null;
        try
        {
            if (System.IO.File.Exists(TargetScenePath))
                want = System.IO.File.ReadAllText(TargetScenePath).Trim();
        }
        catch (System.Exception e) { Debug.LogWarning($"[HZ] target scene read failed: {e.Message}"); }

        if (!string.IsNullOrEmpty(want))
        {
            string path = ResolveScenePath(want);
            if (path == null)
            {
                Debug.LogError($"[HZ] scene not found: '{want}'");
                return;
            }
            // SubScene content lives in separate .unity files a plain OpenScene never loads
            if (!OpenSceneWithSubScenes(path)) return;
        }
        ExportScene();
    }

    // Accepts a full "Assets/.../x.unity" path or a bare scene name.
    static string ResolveScenePath(string want)
    {
        if (want.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase)
            && System.IO.File.Exists(want))
            return want;
        string stem = System.IO.Path.GetFileNameWithoutExtension(want);
        foreach (var guid in AssetDatabase.FindAssets($"t:Scene {stem}"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), stem,
                              System.StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    /// <summary>Engine shadow tint. subtractiveShadowColor only tints shadows in
    /// Subtractive mixed lighting; under every other mode a realtime shadow receives
    /// no direct light at all, so the tint is black and ambient/IBL fills it.</summary>
    static Color ResolveShadowColor()
    {
        bool subtractive = false;
        try
        {
            var ls = Lightmapping.lightingSettings;
            subtractive = ls != null
                          && ls.mixedBakeMode == MixedLightingMode.Subtractive
                          && LightmapSettings.lightmaps.Length > 0;
        }
        catch (System.Exception) { subtractive = false; } // no LightingSettings asset
        return subtractive ? RenderSettings.subtractiveShadowColor : Color.black;
    }

    /// <summary>Report UV channel contents of the terrain detail (grass) meshes, so the
    /// translation uses the channel Unity's graph actually reads instead of guessing.</summary>
    public static void DumpGrassUVs()
    {
        foreach (var t in Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var protos = t.terrainData.detailPrototypes;
            for (int i = 0; i < protos.Length; i++)
            {
                var m = protos[i].prototype != null
                    ? protos[i].prototype.GetComponentInChildren<MeshFilter>()?.sharedMesh : null;
                if (m == null) { Debug.Log($"[HZUV] proto{i} usePrototypeMesh={protos[i].usePrototypeMesh} mesh=<none> (texture billboard)"); continue; }
                var uv0=new System.Collections.Generic.List<Vector4>();
                var uv3=new System.Collections.Generic.List<Vector4>();
                m.GetUVs(0, uv0); m.GetUVs(3, uv3);
                string r3 = "<empty>";
                if (uv3.Count > 0) {
                    float mnx=9e9f,mxx=-9e9f,mny=9e9f,mxy=-9e9f;
                    foreach (var v in uv3){ mnx=Mathf.Min(mnx,v.x); mxx=Mathf.Max(mxx,v.x); mny=Mathf.Min(mny,v.y); mxy=Mathf.Max(mxy,v.y); }
                    r3=$"n={uv3.Count} x=[{mnx:F3},{mxx:F3}] y=[{mny:F3},{mxy:F3}]";
                }
                Debug.Log($"[HZUV] proto{i} mesh='{m.name}' verts={m.vertexCount} uv0n={uv0.Count} uv3: {r3}");
            }
            break;
        }
    }

    /// <summary>Report exactly what Unity feeds the grass: detail-prototype render path,
    /// which UV channels the prototype meshes actually declare, and the colour values the
    /// material hands the shader (vs what is serialized).</summary>
    public static void DumpGrassTruth()
    {
        Debug.Log($"[HZGT] colorspace={QualitySettings.activeColorSpace} gamma_kw={Shader.IsKeywordEnabled("UNITY_COLORSPACE_GAMMA")}");
        foreach (var t in Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Debug.Log($"[HZGT] terrain='{t.name}' drawInstanced={t.drawInstanced} detailDensity={t.detailObjectDensity} detailDistance={t.detailObjectDistance}");
            var protos = t.terrainData.detailPrototypes;
            for (int i = 0; i < protos.Length; i++)
            {
                var p = protos[i];
                var go = p.prototype;
                var mr = go != null ? go.GetComponentInChildren<MeshRenderer>() : null;
                var mf = go != null ? go.GetComponentInChildren<MeshFilter>() : null;
                var m  = mf != null ? mf.sharedMesh : null;
                string matName = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.name : "<none>";
                string attrs = "<no mesh>";
                if (m != null)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var a in m.GetVertexAttributes())
                        sb.Append(a.attribute).Append('(').Append(a.dimension).Append(") ");
                    attrs = sb.ToString();
                }
                Debug.Log($"[HZGT] proto{i} proto='{(go!=null?go.name:"<null>")}' mat='{matName}' mesh='{(m!=null?m.name:"<null>")}' "
                        + $"renderMode={p.renderMode} usePrototypeMesh={p.usePrototypeMesh} useInstancing={p.useInstancing} "
                        + $"healthy={p.healthyColor} dry={p.dryColor} noiseSpread={p.noiseSpread} "
                        + $"w=[{p.minWidth:F3},{p.maxWidth:F3}] h=[{p.minHeight:F3},{p.maxHeight:F3}] density={p.density} "
                        + $"alignToGround={p.alignToGround} positionJitter={p.positionJitter} useDensityScaling={p.useDensityScaling} "
                        + $"attrs=[{attrs}]");
                if (mr != null && mr.sharedMaterial != null) DumpGrassMatColors(mr.sharedMaterial);
            }
            break;
        }
        foreach (var name in new[] { "Grass_01", "Grass_02", "Grass_03" })
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Idyllic Fantasy Nature/Materials/Grass/{name}.mat");
            if (mat != null) DumpGrassMatColors(mat);
        }
    }

    static void DumpGrassMatColors(Material mat)
    {
        foreach (var prop in new[] { "_Top_Color", "_Bottom_Color" })
        {
            if (!mat.HasProperty(prop)) { Debug.Log($"[HZGT]   {mat.name}.{prop} <absent>"); continue; }
            Color c = mat.GetColor(prop);
            Debug.Log($"[HZGT]   {mat.name}.{prop} get=({c.r:F6},{c.g:F6},{c.b:F6}) linear=({c.linear.r:F6},{c.linear.g:F6},{c.linear.b:F6}) gamma=({c.gamma.r:F6},{c.gamma.g:F6},{c.gamma.b:F6})");
        }
        foreach (var prop in new[] { "_Blend_Height", "_Custom_Color", "_Enable_Top_Color", "_Alpha_Cutoff" })
            if (mat.HasProperty(prop)) Debug.Log($"[HZGT]   {mat.name}.{prop} = {mat.GetFloat(prop):F6}");
    }

    const string ABDir = "/Users/gabrieldechichi/dev/gabrieldechichi/hz-demos/references";

    /// <summary>Render the main camera to a RT twice -- SSAO on, then off -- so the
    /// feature's actual pixel contribution is measured instead of assumed.</summary>
    public static void CaptureSSAOCompare()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[HZAB] no main camera"); return; }
        var feat = FindSSAOFeature();
        if (feat == null) { Debug.LogError("[HZAB] no SSAO renderer feature"); return; }

        bool was = feat.isActive;
        Shoot(cam, $"{ABDir}/unity_ssao_on.png", feat, true);
        Shoot(cam, $"{ABDir}/unity_ssao_off.png", feat, false);
        feat.SetActive(was);
        Debug.Log($"[HZAB] done; SSAO restored to {was}");
    }

    static UnityEngine.Rendering.Universal.ScriptableRendererFeature FindSSAOFeature()
    {
        var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urp == null) return null;
        var so = new UnityEditor.SerializedObject(urp);
        var list = so.FindProperty("m_RendererDataList");
        for (int i = 0; list != null && i < list.arraySize; i++)
        {
            var rd = list.GetArrayElementAtIndex(i).objectReferenceValue;
            if (rd == null) continue;
            var feats = new UnityEditor.SerializedObject(rd).FindProperty("m_RendererFeatures");
            for (int f = 0; feats != null && f < feats.arraySize; f++)
            {
                var fo = feats.GetArrayElementAtIndex(f).objectReferenceValue
                         as UnityEngine.Rendering.Universal.ScriptableRendererFeature;
                if (fo != null && fo.GetType().Name.Contains("ScreenSpaceAmbientOcclusion")) return fo;
            }
        }
        return null;
    }

    /// <summary>Report every non-terrain renderer using the Vegetation graph: whether its
    /// mesh actually carries UV3, and which colour-overlay branch its material takes. This
    /// is what decides if the grass UV3 mapping is safe to generalise.</summary>
    public static void DumpVegetationMeshes()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var mf = mr.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) continue;
            foreach (var mat in mr.sharedMaterials)
            {
                if (mat == null || mat.shader == null) continue;
                if (!mat.shader.name.Contains("Vegetation")) continue;
                string key = mat.name + "|" + mesh.name;
                if (!seen.Add(key)) continue;
                bool uv3 = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord3);
                string allAttrs = string.Join(" ", System.Array.ConvertAll(mesh.GetVertexAttributes(), a => a.attribute.ToString()));
                Debug.Log($"[HZVM] attrs mat='{mat.name}' mesh='{mesh.name}' [{allAttrs}]");
                string kws = string.Join(",", mat.shaderKeywords);
                Debug.Log($"[HZVM] mat='{mat.name}' mesh='{mesh.name}' hasUV3={uv3} overlayUV={mat.IsKeywordEnabled("_COLOR_OVERLAY_UV")} keywords=[{kws}]");
            }
        }
        Debug.Log("[HZVM] done");
    }

    /// <summary>Texture-independent read of the colour-overlay blend T. Two captures with
    /// the tint ramp reversed: A = tex*T*light, B = tex*(1-T)*light, so A/(A+B) == T with
    /// the albedo cancelled. A red/blue probe only works where the albedo is neutral.</summary>
    public static void CaptureBlendRatioProbe()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[HZBR] no main camera"); return; }
        var mats = new System.Collections.Generic.List<Material>();
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Idyllic Fantasy Nature/Materials" }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (m == null || m.shader == null || !m.shader.name.Contains("Vegetation")) continue;
            if (!m.HasProperty("_Top_Color") || !m.HasProperty("_Bottom_Color")) continue;
            mats.Add(m);
        }
        var sb = new Color[mats.Count]; var st = new Color[mats.Count];
        for (int i = 0; i < mats.Count; i++) { sb[i] = mats[i].GetColor("_Bottom_Color"); st[i] = mats[i].GetColor("_Top_Color"); }

        for (int i = 0; i < mats.Count; i++) { mats[i].SetColor("_Bottom_Color", Color.black); mats[i].SetColor("_Top_Color", Color.white); }
        ShootPlain(cam, $"{ABDir}/unity_blend_A.png");
        for (int i = 0; i < mats.Count; i++) { mats[i].SetColor("_Bottom_Color", Color.white); mats[i].SetColor("_Top_Color", Color.black); }
        ShootPlain(cam, $"{ABDir}/unity_blend_B.png");

        for (int i = 0; i < mats.Count; i++) { mats[i].SetColor("_Bottom_Color", sb[i]); mats[i].SetColor("_Top_Color", st[i]); }
        Debug.Log($"[HZBR] probed {mats.Count} materials; colours restored");
    }

    /// <summary>Write Unity's own generated shader for the Vegetation graph, so the
    /// translation is checked against what URP actually compiles, not the graph JSON.</summary>
    public static void DumpVegetationGeneratedShader()
    {
        const string graph = "Assets/Idyllic Fantasy Nature/Shader/Vegetation.shadergraph";
        var asm = System.Type.GetType("UnityEditor.ShaderGraph.ShaderGraphImporter, Unity.ShaderGraph.Editor").Assembly;
        var importer = asm.GetType("UnityEditor.ShaderGraph.ShaderGraphImporter");
        var assetsT = asm.GetType("UnityEditor.ShaderGraph.AssetCollection");
        System.Reflection.MethodInfo getText = null;
        foreach (var m in importer.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            if (m.Name == "GetShaderText" && m.GetParameters().Length == 4) getText = m;
        if (getText == null || assetsT == null) { Debug.LogError("[HZGS] GetShaderText(path, out tex, assets, out graph) not found"); return; }
        var args = new object[] { graph, null, System.Activator.CreateInstance(assetsT, true), null };
        var text = (string)getText.Invoke(null, args);
        string outPath = $"{ABDir}/Vegetation.generated.shader";
        System.IO.File.WriteAllText(outPath, text);
        Debug.Log($"[HZGS] wrote {outPath} ({text.Length} chars)");
    }

    /// <summary>List every material/shader pair rendered in the scene with its mesh, so a
    /// region of the frame can be tied to the shader that actually draws it.</summary>
    public static void DumpSceneMaterials()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            var mf = r.GetComponent<MeshFilter>();
            string mesh = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "<none>";
            foreach (var mat in r.sharedMaterials)
            {
                if (mat == null) continue;
                string key = mat.name + "|" + (mat.shader != null ? mat.shader.name : "?");
                if (!seen.Add(key)) continue;
                Debug.Log($"[HZSM] mat='{mat.name}' shader='{(mat.shader != null ? mat.shader.name : "?")}' go='{r.gameObject.name}' mesh='{mesh}' keywords=[{string.Join(",", mat.shaderKeywords)}]");
            }
        }
        Debug.Log("[HZSM] done");
    }

    /// <summary>Controlled read of what the Vegetation graph sees in TEXCOORD3 when a mesh
    /// does not provide it. Quads with known constant UV0/UV1 and no UV3, plus a control quad
    /// that does carry UV3; blend_height 0.2 makes the overlay T equal uv3.y, and the ramp is
    /// reversed across two captures so T = A/(A+B) independent of lighting.</summary>
    public static void CaptureUv3AliasProbe()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[HZUA] no main camera"); return; }
        var shader = Shader.Find("Idyllic Fantasy Nature/Vegetation");
        if (shader == null) { Debug.LogError("[HZUA] Vegetation shader not found"); return; }

        var mat = new Material(shader) { hideFlags = HideFlags.DontSave };
        mat.SetFloat("_COLOR_OVERLAY", 1);
        mat.DisableKeyword("_COLOR_OVERLAY_VERTEX_POSITION");
        mat.EnableKeyword("_COLOR_OVERLAY_UV");
        mat.SetFloat("_Custom_Color", 1);
        mat.SetFloat("_Enable_Top_Color", 1);
        mat.SetFloat("_Blend_Height", 0.2f);
        mat.SetFloat("_Alpha_Cutoff", 0.0f);
        mat.SetFloat("_Wind_Strength", 0);
        mat.SetFloat("_Bend_Strength", 0);
        mat.SetFloat("_Start_Bend_Range", 0);
        mat.SetVector("_Player_Position", new Vector4(1e5f, 1e5f, 1e5f, 0));
        mat.SetTexture("_Texture", Texture2D.whiteTexture);

        // (label, uv0.y, uv1.y or <0 for none, uv3.y or <0 for none)
        var cases = new (string label, float uv0, float uv1, float uv3)[]
        {
            ("uv0=0.25 uv1=0.75 noUV3", 0.25f, 0.75f, -1f),
            ("uv0=0.60 noUV1 noUV3",   0.60f, -1f,   -1f),
            ("CONTROL uv0=0.25 uv3=0.40", 0.25f, -1f, 0.40f),
            ("uv0=0.25 uv2=0.55 noUV1 noUV3", 0.25f, -1f, -1f),
            ("uv0=0.25 uv1=0.75 uv2=0.55 noUV3", 0.25f, 0.75f, -1f),
        };
        var uv2 = new float[] { -1f, -1f, -1f, 0.55f, 0.55f };
        var objs = new System.Collections.Generic.List<GameObject>();
        var centers = new Vector3[cases.Length];
        var fwd = cam.transform.forward; var right = cam.transform.right; var up = cam.transform.up;
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var m = new Mesh { hideFlags = HideFlags.DontSave, name = "HZUA_" + i };
            float h = 0.3f;
            m.vertices = new[] { new Vector3(-h,-h,0), new Vector3(h,-h,0), new Vector3(h,h,0), new Vector3(-h,h,0) };
            m.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            m.SetUVs(0, new System.Collections.Generic.List<Vector2> { new Vector2(0,c.uv0), new Vector2(0,c.uv0), new Vector2(0,c.uv0), new Vector2(0,c.uv0) });
            if (c.uv1 >= 0) m.SetUVs(1, new System.Collections.Generic.List<Vector2> { new Vector2(0,c.uv1), new Vector2(0,c.uv1), new Vector2(0,c.uv1), new Vector2(0,c.uv1) });
            if (uv2[i] >= 0) m.SetUVs(2, new System.Collections.Generic.List<Vector2> { new Vector2(0,uv2[i]), new Vector2(0,uv2[i]), new Vector2(0,uv2[i]), new Vector2(0,uv2[i]) });
            if (c.uv3 >= 0) m.SetUVs(3, new System.Collections.Generic.List<Vector2> { new Vector2(0,c.uv3), new Vector2(0,c.uv3), new Vector2(0,c.uv3), new Vector2(0,c.uv3) });
            m.triangles = new[] { 0,2,1, 0,3,2, 0,1,2, 0,2,3 };
            m.RecalculateBounds();
            var go = new GameObject("HZUA_" + i) { hideFlags = HideFlags.DontSave };
            go.AddComponent<MeshFilter>().sharedMesh = m;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            centers[i] = cam.transform.position + fwd * 3f + right * ((i - 2) * 0.7f) + up * 0.3f;
            go.transform.SetPositionAndRotation(centers[i], cam.transform.rotation);
            objs.Add(go);
            Debug.Log($"[HZUA] quad{i} {c.label} attrs=[{string.Join(" ", System.Array.ConvertAll(m.GetVertexAttributes(), a => a.attribute.ToString()))}]");
        }

        mat.SetColor("_Bottom_Color", Color.black); mat.SetColor("_Top_Color", Color.white);
        ShootPlain(cam, $"{ABDir}/unity_uv3alias_A.png");
        mat.SetColor("_Bottom_Color", Color.white); mat.SetColor("_Top_Color", Color.black);
        ShootPlain(cam, $"{ABDir}/unity_uv3alias_B.png");

        for (int i = 0; i < centers.Length; i++)
        {
            var vp = cam.WorldToViewportPoint(centers[i]);
            Debug.Log($"[HZUA] quad{i} px=({vp.x * 1920f:F0},{(1f - vp.y) * 1080f:F0})");
        }
        foreach (var go in objs) { Object.DestroyImmediate(go.GetComponent<MeshFilter>().sharedMesh); Object.DestroyImmediate(go); }
        Object.DestroyImmediate(mat);
        Debug.Log("[HZUA] done");
    }

    /// <summary>Lighting-only capture of every Vegetation material: tints white and the
    /// texture replaced by a white-RGB copy that keeps its alpha, so coverage is unchanged and
    /// the frame holds only the graph's lighting term. Divided into the reversed-ramp sum it
    /// separates texture colour from lighting.</summary>
    public static void CaptureLightingOnlyProbe() { LightingOnlyCapture("unity_lightonly.png"); }

    /// <summary>The lighting-only capture with every directional light's shadows off, so the
    /// graph's shadow attenuation is 1 and the frame isolates the ambient part of the term.</summary>
    public static void CaptureLightingOnlyNoShadowProbe()
    {
        var lights = new System.Collections.Generic.List<(Light l, LightShadows s)>();
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (l.type == LightType.Directional) { lights.Add((l, l.shadows)); l.shadows = LightShadows.None; }
        try { LightingOnlyCapture("unity_lightonly_noshadow.png"); }
        finally { foreach (var e in lights) e.l.shadows = e.s; }
        Debug.Log($"[HZLO] shadows restored on {lights.Count} directional light(s)");
    }

    /// <summary>Lighting-only captures (shadows on and off) with every Vegetation material's
    /// alpha cutoff at 0, so leaf cards are full quads in both the lit and shadow-caster passes.
    /// The same condition in the engine isolates caster geometry from alpha-test behaviour.</summary>
    public static void CaptureCutoffZeroProbe()
    {
        var mats = new System.Collections.Generic.List<(Material m, float c)>();
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Idyllic Fantasy Nature/Materials" }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (m == null || m.shader == null || !m.shader.name.Contains("Vegetation") || !m.HasProperty("_Alpha_Cutoff")) continue;
            mats.Add((m, m.GetFloat("_Alpha_Cutoff")));
            m.SetFloat("_Alpha_Cutoff", 0f);
        }
        try
        {
            LightingOnlyCapture("unity_lightonly_cut0.png");
            var lights = new System.Collections.Generic.List<(Light l, LightShadows s)>();
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (l.type == LightType.Directional) { lights.Add((l, l.shadows)); l.shadows = LightShadows.None; }
            try { LightingOnlyCapture("unity_lightonly_noshadow_cut0.png"); }
            finally { foreach (var e in lights) e.l.shadows = e.s; }
        }
        finally { foreach (var e in mats) e.m.SetFloat("_Alpha_Cutoff", e.c); }
        Debug.Log($"[HZC0] cutoff-0 captures done; restored cutoff on {mats.Count} materials");
    }

    static void LightingOnlyCapture(string file)
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[HZLO] no main camera"); return; }
        var mats = new System.Collections.Generic.List<Material>();
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Idyllic Fantasy Nature/Materials" }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (m == null || m.shader == null || !m.shader.name.Contains("Vegetation")) continue;
            if (!m.HasProperty("_Top_Color") || !m.HasProperty("_Bottom_Color") || !m.HasProperty("_Texture")) continue;
            mats.Add(m);
        }
        var whites = new System.Collections.Generic.Dictionary<Texture, Texture2D>();
        var sb = new Color[mats.Count]; var st = new Color[mats.Count]; var stex = new Texture[mats.Count];
        for (int i = 0; i < mats.Count; i++)
        {
            sb[i] = mats[i].GetColor("_Bottom_Color"); st[i] = mats[i].GetColor("_Top_Color"); stex[i] = mats[i].GetTexture("_Texture");
            if (stex[i] != null && !whites.ContainsKey(stex[i])) whites[stex[i]] = WhiteRgbKeepAlpha(stex[i]);
            mats[i].SetColor("_Bottom_Color", Color.white); mats[i].SetColor("_Top_Color", Color.white);
            if (stex[i] != null) mats[i].SetTexture("_Texture", whites[stex[i]]);
        }
        ShootPlain(cam, $"{ABDir}/{file}");
        for (int i = 0; i < mats.Count; i++)
        {
            mats[i].SetColor("_Bottom_Color", sb[i]); mats[i].SetColor("_Top_Color", st[i]); mats[i].SetTexture("_Texture", stex[i]);
        }
        foreach (var w in whites.Values) Object.DestroyImmediate(w);
        Debug.Log($"[HZLO] probed {mats.Count} materials; restored");
    }

    static Texture2D WhiteRgbKeepAlpha(Texture src)
    {
        var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, true, true) { hideFlags = HideFlags.DontSave };
        t.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        var px = t.GetPixels32();
        for (int i = 0; i < px.Length; i++) { px[i].r = 255; px[i].g = 255; px[i].b = 255; }
        t.SetPixels32(px);
        t.filterMode = src.filterMode; t.wrapMode = src.wrapMode; t.anisoLevel = src.anisoLevel;
        t.Apply(true);
        return t;
    }

    /// <summary>For every LODGroup in the main camera's frustum, report which LOD Unity
    /// renders from this viewpoint (relative screen height vs transition heights, with
    /// lodBias) and the mesh at each level.</summary>
    public static void DumpVisibleTreeLods()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[HZLD] no main camera"); return; }
        var planes = GeometryUtility.CalculateFrustumPlanes(cam);
        float halfAngle = Mathf.Tan(Mathf.Deg2Rad * cam.fieldOfView * 0.5f);
        Debug.Log($"[HZLD] lodBias={QualitySettings.lodBias} maxLOD={QualitySettings.maximumLODLevel} fov={cam.fieldOfView}");
        foreach (var g in Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            var lods = g.GetLODs();
            if (lods.Length == 0 || lods[0].renderers.Length == 0 || lods[0].renderers[0] == null) continue;
            if (!GeometryUtility.TestPlanesAABB(planes, lods[0].renderers[0].bounds)) continue;
            var sc = g.transform.lossyScale;
            float size = g.size * Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.y), Mathf.Abs(sc.z));
            float dist = Vector3.Distance(g.transform.TransformPoint(g.localReferencePoint), cam.transform.position);
            float rel = size * 0.5f / (dist * halfAngle) * QualitySettings.lodBias;
            int active = -1;
            for (int i = 0; i < lods.Length; i++) if (rel >= lods[i].screenRelativeTransitionHeight) { active = i; break; }
            var sbd = new System.Text.StringBuilder();
            for (int i = 0; i < lods.Length; i++)
            {
                var r = lods[i].renderers.Length > 0 ? lods[i].renderers[0] : null;
                var mf = r != null ? r.GetComponent<MeshFilter>() : null;
                sbd.Append($"L{i}(>{lods[i].screenRelativeTransitionHeight:F3} {(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "-")}) ");
            }
            Debug.Log($"[HZLD] '{g.name}' dist={dist:F1} size={size:F2} rel={rel:F3} active=L{active} {sbd}");
        }
        Debug.Log("[HZLD] done");
    }

    /// <summary>List the Vegetation renderers whose screen-space bounds overlap the broadleaf
    /// canopy measurement rect (0,0)-(1250,300) of the 1920x1080 capture, with their LODGroup,
    /// LOD level and distance, so a region's numbers can be tied to what draws it.</summary>
    public static void DumpCanopyRectRenderers()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[HZCR] no main camera"); return; }
        var lodOf = new System.Collections.Generic.Dictionary<Renderer, string>();
        foreach (var g in Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            var lods = g.GetLODs();
            for (int i = 0; i < lods.Length; i++)
                foreach (var r in lods[i].renderers) if (r != null) lodOf[r] = $"{g.name}:L{i}";
        }
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!r.enabled) continue;
            string vegMat = null;
            foreach (var sm in r.sharedMaterials)
                if (sm != null && sm.shader != null && sm.shader.name.Contains("Vegetation")) { vegMat = sm.name; break; }
            if (vegMat == null) continue;
            var b = r.bounds;
            float x0 = 1e9f, y0 = 1e9f, x1 = -1e9f, y1 = -1e9f; int behind = 0;
            for (int k = 0; k < 8; k++)
            {
                var c = new Vector3((k & 1) != 0 ? b.max.x : b.min.x, (k & 2) != 0 ? b.max.y : b.min.y, (k & 4) != 0 ? b.max.z : b.min.z);
                var vp = cam.WorldToViewportPoint(c);
                if (vp.z <= cam.nearClipPlane) { behind++; continue; }
                float px = vp.x * 1920f, py = (1f - vp.y) * 1080f;
                x0 = Mathf.Min(x0, px); x1 = Mathf.Max(x1, px); y0 = Mathf.Min(y0, py); y1 = Mathf.Max(y1, py);
            }
            if (behind == 8 || x1 < 0 || x0 > 1250 || y1 < 0 || y0 > 300) continue;
            var mf = r.GetComponent<MeshFilter>();
            string lod = lodOf.TryGetValue(r, out var l) ? l : "no-LODGroup";
            float dist = Vector3.Distance(b.center, cam.transform.position);
            Debug.Log($"[HZCR] go='{r.gameObject.name}' root='{r.transform.root.name}' mat='{vegMat}' behindCorners={behind} mesh='{(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "-")}' {lod} dist={dist:F1} screen=({x0:F0},{y0:F0})-({x1:F0},{y1:F0})");
        }
        Debug.Log("[HZCR] done");
    }

    /// <summary>Shadow casting mode of every renderer in the canopy trees' LODGroups, so
    /// the exported casts_shadow can be checked against what Unity actually draws into the map.</summary>
    public static void DumpTreeShadowCasting()
    {
        var names = new System.Collections.Generic.HashSet<string> { "Tree_02 (5)", "Tree_03 (2)", "WillowTree_03 (1)", "Fir_01" };
        foreach (var g in Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!names.Contains(g.name)) continue;
            var lods = g.GetLODs();
            for (int i = 0; i < lods.Length; i++)
                foreach (var r in lods[i].renderers)
                    if (r != null)
                        Debug.Log($"[HZSC] '{g.name}' L{i} go='{r.gameObject.name}' shadowCastingMode={r.shadowCastingMode} receiveShadows={r.receiveShadows} enabled={r.enabled} mats=[{string.Join(",", System.Array.ConvertAll(r.sharedMaterials, m => m != null ? m.name : "null"))}]");
        }
        Debug.Log("[HZSC] done");
    }

    /// <summary>Report where Unity's ambient comes from, then prove whether the live ambient
    /// probe follows the skybox: sample it, remove the skybox and regenerate, restore and
    /// regenerate. Leaves the probe regenerated from the restored skybox.</summary>
    public static void ProbeAmbientSource()
    {
        var dirs = new[] { Vector3.up, Vector3.forward, Vector3.down };
        string Eval(string tag)
        {
            var outc = new Color[3];
            RenderSettings.ambientProbe.Evaluate(dirs, outc);
            return $"{tag}: up=({outc[0].r:F3},{outc[0].g:F3},{outc[0].b:F3}) horizon=({outc[1].r:F3},{outc[1].g:F3},{outc[1].b:F3}) down=({outc[2].r:F3},{outc[2].g:F3},{outc[2].b:F3})";
        }
        Debug.Log($"[HZAM] ambientMode={RenderSettings.ambientMode} intensity={RenderSettings.ambientIntensity} skybox={(RenderSettings.skybox ? RenderSettings.skybox.name : "null")} sky={RenderSettings.ambientSkyColor} reflMode={RenderSettings.defaultReflectionMode} reflIntensity={RenderSettings.reflectionIntensity} customRefl={(RenderSettings.customReflectionTexture ? RenderSettings.customReflectionTexture.name : "null")}");
        Debug.Log($"[HZAM] lightingDataAsset={(Lightmapping.lightingDataAsset ? Lightmapping.lightingDataAsset.name : "null")} lightmaps={LightmapSettings.lightmaps.Length} giWorkflow={Lightmapping.giWorkflowMode} isRunning={Lightmapping.isRunning}");
        Debug.Log("[HZAM] " + Eval("P0 current"));
        var sky = RenderSettings.skybox;
        RenderSettings.skybox = null;
        Debug.Log("[HZAM] " + Eval("P1 skybox=null, no update"));
        DynamicGI.UpdateEnvironment();
        Debug.Log("[HZAM] " + Eval("P2 skybox=null, UpdateEnvironment"));
        RenderSettings.skybox = sky;
        DynamicGI.UpdateEnvironment();
        Debug.Log("[HZAM] " + Eval("P3 skybox restored, UpdateEnvironment"));
    }

    /// <summary>Report the default reflection source Unity binds when no lighting has been
    /// generated, so specular IBL parity can be judged the same way as the ambient probe.</summary>
    public static void DumpDefaultReflection()
    {
        var t = ReflectionProbe.defaultTexture;
        Debug.Log($"[HZRF] defaultReflection={(t ? t.name : "null")} dim={(t ? t.dimension.ToString() : "-")} size={(t ? t.width + "x" + t.height : "-")} mips={(t ? t.mipmapCount : 0)} hdr={ReflectionProbe.defaultTextureHDRDecodeValues} probes={Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None).Length}");
        if (t is Cubemap cube)
        {
            var rt = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGBFloat);
            Graphics.CopyTexture(cube, 2, Mathf.Max(0, cube.mipmapCount - 1), rt, 0, 0);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
            tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); RenderTexture.active = prev;
            Debug.Log($"[HZRF] +Y face last mip texel = {tex.GetPixel(0, 0)}");
            Object.DestroyImmediate(tex); RenderTexture.ReleaseTemporary(rt);
        }
    }

    /// <summary>Main camera with the full post stack, then with depth of field, split toning and the channel
    /// mixer each switched off in turn, so every effect's contribution is measured in Unity itself.</summary>
    public static void CapturePostEffects()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[HZPE] no main camera"); return; }
        var profile = FindActivePostProfile();
        if (profile == null) { Debug.LogError("[HZPE] no active global post-processing Volume"); return; }
        ShootPlain(cam, $"{ABDir}/unity_post_all.png");
        CaptureWithout<DepthOfField>(cam, profile, "unity_post_no_dof.png");
        CaptureWithout<SplitToning>(cam, profile, "unity_post_no_split.png");
        CaptureWithout<ChannelMixer>(cam, profile, "unity_post_no_mixer.png");
        Debug.Log("[HZPE] post effect captures done");
    }

    // export variants: each leaves the profile switched until the next call, so an export in between reads it; SetPostVariantAll restores
    public static void SetPostVariantAll() { SetPostVariant(true, true, true); }
    public static void SetPostVariantNoDof() { SetPostVariant(false, true, true); }
    public static void SetPostVariantNoSplit() { SetPostVariant(true, false, true); }
    public static void SetPostVariantNoMixer() { SetPostVariant(true, true, false); }
    public static void SetPostVariantNone() { SetPostVariant(false, false, false); }

    // DoF export variants for the scene_day check: focus distance override and engine debug view, persisted until SetDofVariantDefault
    static float s_dofFocusOverride = -1f;
    static uint s_dofDebugView = 0;
    public static void SetDofVariantFocus1m() { s_dofFocusOverride = 1.0f; s_dofDebugView = 0; Debug.Log("[HZPV] dof focus=1"); }
    public static void SetDofVariantFocus8m() { s_dofFocusOverride = 8.0f; s_dofDebugView = 0; Debug.Log("[HZPV] dof focus=8"); }
    public static void SetDofVariantDebugCoc() { s_dofFocusOverride = -1f; s_dofDebugView = 1; Debug.Log("[HZPV] dof debug=1"); }
    public static void SetDofVariantDebugTiles() { s_dofFocusOverride = -1f; s_dofDebugView = 2; Debug.Log("[HZPV] dof debug=2"); }
    public static void SetDofVariantDefault() { s_dofFocusOverride = -1f; s_dofDebugView = 0; Debug.Log("[HZPV] dof default"); }

    static void SetPostVariant(bool dof, bool split, bool mixer)
    {
        var profile = FindActivePostProfile();
        if (profile == null) { Debug.LogError("[HZPV] no active global post-processing Volume"); return; }
        if (profile.TryGet<DepthOfField>(out var d)) d.active = dof;
        if (profile.TryGet<SplitToning>(out var s)) s.active = split;
        if (profile.TryGet<ChannelMixer>(out var m)) m.active = mixer;
        Debug.Log($"[HZPV] variant dof={dof} split={split} mixer={mixer}");
    }

    /// <summary>Main camera with depth of field, split toning and the channel mixer all off: the baseline for the pre-existing parity gap.</summary>
    public static void CapturePostNone()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[HZPE] no main camera"); return; }
        SetPostVariantNone();
        try { ShootPlain(cam, $"{ABDir}/unity_post_none.png"); }
        finally { SetPostVariantAll(); }
        Debug.Log("[HZPE] none capture done");
    }

    static void CaptureWithout<T>(Camera cam, VolumeProfile profile, string file) where T : VolumeComponent
    {
        if (!profile.TryGet<T>(out var c)) { Debug.LogError($"[HZPE] profile has no {typeof(T).Name}"); return; }
        bool was = c.active;
        c.active = false;
        try { ShootPlain(cam, $"{ABDir}/{file}"); }
        finally { c.active = was; }
    }

    /// <summary>Main camera of the open scene with its full post stack, to references/&lt;scene&gt;_unity.png.</summary>
    public static void CaptureSceneReference()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[HZSR] no main camera"); return; }
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        ShootPlain(cam, $"{ABDir}/{scene}_unity.png");
        Debug.Log($"[HZSR] captured {scene}");
    }

    static void ShootPlain(Camera cam, string path)
    {
        var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32,
                                   RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
        var req = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = rt };
        if (!UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(cam, req))
        { Debug.LogError("[HZGP] render request unsupported"); return; }
        UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, req);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        rt.Release(); Object.DestroyImmediate(rt);
        Debug.Log($"[HZGP] wrote {path}");
    }

    static void Shoot(Camera cam, string path,
                      UnityEngine.Rendering.Universal.ScriptableRendererFeature feat, bool on)
    {
        feat.SetActive(on);
        var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32,
                                   RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
        var req = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = rt };
        if (!UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(cam, req))
        { Debug.LogError("[HZAB] render request unsupported"); return; }
        UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, req);

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        rt.Release(); Object.DestroyImmediate(rt);
        Debug.Log($"[HZAB] wrote {path} (ssao={on})");
    }

    /// <summary>Dump URP renderer features (SSAO) and the active volume stack.</summary>
    public static void DumpPostFx()
    {
        var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urp != null)
        {
            var so = new UnityEditor.SerializedObject(urp);
            var list = so.FindProperty("m_RendererDataList");
            for (int i = 0; list != null && i < list.arraySize; i++)
            {
                var rd = list.GetArrayElementAtIndex(i).objectReferenceValue;
                if (rd == null) continue;
                var rso = new UnityEditor.SerializedObject(rd);
                var feats = rso.FindProperty("m_RendererFeatures");
                Debug.Log($"[HZDUMP] renderer[{i}]={rd.name} features={(feats != null ? feats.arraySize : 0)}");
                for (int f = 0; feats != null && f < feats.arraySize; f++)
                {
                    var fo = feats.GetArrayElementAtIndex(f).objectReferenceValue;
                    if (fo == null) continue;
                    var fso = new UnityEditor.SerializedObject(fo);
                    string extra = "";
                    var it2 = fso.GetIterator(); bool go = it2.NextVisible(true);
                    while (go) {
                        if (it2.propertyType != UnityEditor.SerializedPropertyType.Generic)
                            extra += $" {it2.propertyPath}={PropStr(it2)}";
                        go = it2.NextVisible(true);
                    }
                    Debug.Log($"[HZDUMP]   feature {fo.GetType().Name} '{fo.name}'{extra}");
                }
            }
        }
        foreach (var v in Object.FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Debug.Log($"[HZDUMP] volume '{v.name}' activeGO={v.gameObject.activeInHierarchy} enabled={v.enabled} global={v.isGlobal} weight={v.weight:F3} prio={v.priority} profile={(v.sharedProfile ? v.sharedProfile.name : "<null>")}");
            if (v.sharedProfile == null) continue;
            foreach (var c in v.sharedProfile.components)
            {
                if (!c.active) { Debug.Log($"[HZDUMP]   {c.GetType().Name} INACTIVE"); continue; }
                string line = "";
                foreach (var f in c.GetType().GetFields())
                {
                    var val = f.GetValue(c) as UnityEngine.Rendering.VolumeParameter;
                    if (val == null || !val.overrideState) continue;
                    line += $" {f.Name}={val}";
                }
                Debug.Log($"[HZDUMP]   {c.GetType().Name}{line}");
            }
        }
    }

    static string PropStr(UnityEditor.SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case UnityEditor.SerializedPropertyType.Boolean: return p.boolValue.ToString();
            case UnityEditor.SerializedPropertyType.Float: return p.floatValue.ToString("F3");
            case UnityEditor.SerializedPropertyType.Integer: return p.intValue.ToString();
            case UnityEditor.SerializedPropertyType.Enum: return p.enumValueIndex.ToString();
            default: return "";
        }
    }

    /// <summary>Dump the live lighting/camera state the exporter reads, so engine
    /// mismatches are diagnosed against Unity's actual values, not a screenshot.</summary>
    public static void DumpLighting()
    {
        var cam = Camera.main;
        Debug.Log($"[HZDUMP] ambientMode={RenderSettings.ambientMode}"
                + $" ambientLight={RenderSettings.ambientLight:F4}"
                + $" ambientIntensity={RenderSettings.ambientIntensity:F4}"
                + $" ambientSky={RenderSettings.ambientSkyColor:F4}"
                + $" ambientEquator={RenderSettings.ambientEquatorColor:F4}"
                + $" ambientGround={RenderSettings.ambientGroundColor:F4}");
        Debug.Log($"[HZDUMP] skybox={(RenderSettings.skybox ? RenderSettings.skybox.name : "<null>")}"
                + $" reflection={RenderSettings.defaultReflectionMode}"
                + $" reflIntensity={RenderSettings.reflectionIntensity:F4}"
                + $" customRefl={(RenderSettings.customReflectionTexture ? RenderSettings.customReflectionTexture.name : "<null>")}");
        Debug.Log($"[HZDUMP] fog={RenderSettings.fog} fogMode={RenderSettings.fogMode}"
                + $" fogColor={RenderSettings.fogColor:F4} fogDensity={RenderSettings.fogDensity:F4}"
                + $" fogStart={RenderSettings.fogStartDistance:F2} fogEnd={RenderSettings.fogEndDistance:F2}");
        if (cam != null)
            Debug.Log($"[HZDUMP] camera clearFlags={cam.clearFlags} background={cam.backgroundColor:F4}"
                    + $" fov={cam.fieldOfView:F3} near={cam.nearClipPlane:F3} far={cam.farClipPlane:F1}"
                    + $" allowHDR={cam.allowHDR} gamma={QualitySettings.activeColorSpace}");
        var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urp != null)
            Debug.Log($"[HZDUMP] urp msaa={urp.msaaSampleCount} hdr={urp.supportsHDR}"
                    + $" shadowDistance={urp.shadowDistance:F1} cascades={urp.shadowCascadeCount}"
                    + $" softShadows={urp.supportsSoftShadows} colorGrading={urp.colorGradingMode}");
    }

    /// <summary>Open Idyllic Fantasy Nature scene_day and run the full scene export.</summary>
    public static void ExportSceneDay()
    {
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
            "Assets/Idyllic Fantasy Nature/Demo/scene_day.unity",
            UnityEditor.SceneManagement.OpenSceneMode.Single);
        ExportScene();
    }

    /// <summary>Open the Polytope Environment_Free demo scene (terrain + trees + grass).</summary>
    public static void OpenEnvironmentFree()
    {
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
            "Assets/Polytope Studio/Lowpoly_Demos/Environment_Free/Environment_Free.unity",
            UnityEditor.SceneManagement.OpenSceneMode.Single);
    }

    /// <summary>Log the active scene's structure so we pick the right granularity for grid layout.</summary>
    public static void DebugSceneHierarchy()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        Debug.Log($"[HZhier] scene '{scene.name}': {roots.Length} root object(s)");
        foreach (var root in roots)
        {
            int mr = root.GetComponentsInChildren<MeshRenderer>(true).Length;
            int smr = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            int childCount = root.transform.childCount;
            Debug.Log($"[HZhier]   '{root.name}': children={childCount} meshRenderers={mr} skinned={smr}");
            // sample first few children
            for (int i = 0; i < Mathf.Min(childCount, 6); i++)
            {
                var c = root.transform.GetChild(i);
                var rs = c.GetComponentsInChildren<Renderer>(true);
                Debug.Log($"[HZhier]       child[{i}] '{c.name}' renderers={rs.Length} pos={c.position}");
            }
        }
    }

    /// <summary>Lay out every renderer-bearing root GameObject in the active scene on a square
    /// grid, sized to the largest object's footprint so nothing overlaps. Each object is centered
    /// in its cell (by its combined renderer bounds) and dropped so its base sits on y=0.</summary>
    public static void LayoutSceneGrid()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

        // Asset granularity: a container (root with children but no renderer of its own) contributes
        // each renderer-bearing direct child; a standalone renderer-bearing root is one asset itself.
        // Camera/light (no renderers) drop out naturally.
        var assets = new List<Transform>();
        foreach (var root in scene.GetRootGameObjects())
        {
            bool ownRenderer = HasMeshRenderer(root, deep: false);
            if (root.transform.childCount > 0 && !ownRenderer)
            {
                foreach (Transform child in root.transform)
                    if (HasMeshRenderer(child.gameObject, deep: true)) assets.Add(child);
            }
            else if (HasMeshRenderer(root, deep: true))
            {
                assets.Add(root.transform);
            }
        }

        // Combined world bounds + XZ footprint per asset.
        var items = new List<(Transform t, Bounds b, float foot)>();
        foreach (var t in assets)
        {
            if (!TryWorldBounds(t.gameObject, out var b)) continue;
            items.Add((t, b, Mathf.Max(b.size.x, b.size.z)));
        }
        if (items.Count == 0) { Debug.LogWarning("[HZgrid] no assets found"); return; }

        // Skip ground/skybox-like outliers (footprint >> median) so they don't blow up the cell.
        var foots = items.Select(it => it.foot).OrderBy(f => f).ToList();
        float median = foots[foots.Count / 2];
        float outlier = Mathf.Max(median * 8f, 0.01f);
        var skipped = items.Where(it => it.foot > outlier).Select(it => it.t.name).ToList();
        items = items.Where(it => it.foot <= outlier).ToList();
        if (items.Count == 0) { Debug.LogWarning("[HZgrid] everything got filtered as outliers"); return; }

        float maxFoot = items.Max(it => it.foot);
        float cell = maxFoot * 1.15f + 0.05f;     // uniform cell sized to the largest → no overlap
        int cols = Mathf.CeilToInt(Mathf.Sqrt(items.Count));

        for (int i = 0; i < items.Count; i++)
        {
            var (t, b, _) = items[i];
            int col = i % cols, row = i / cols;
            Vector3 cellCenter = new Vector3(col * cell, 0f, row * cell);
            Undo.RecordObject(t, "Grid Layout");
            // Center the asset's bounds in its cell (XZ), base on y=0.
            t.position += new Vector3(cellCenter.x - b.center.x, -b.min.y, cellCenter.z - b.center.z);
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"[HZgrid] placed {items.Count} assets on a {cols}-col grid, cell={cell:F2}m "
                  + $"(max footprint {maxFoot:F2}m). Skipped {skipped.Count} oversized: {string.Join(", ", skipped)}");
    }

    // Max shadow render distance comes from the active Render Pipeline asset (URP's
    // `shadowDistance`), not QualitySettings.shadowDistance which is the legacy/built-in path and
    // is ignored under SRP. Read via reflection to avoid a hard URP assembly reference; falls back
    // to QualitySettings when no SRP asset is active.
    /// <summary>URP cascade count and cumulative split fractions of the shadow distance.</summary>
    static void ResolveShadowLayout(UniversalRenderPipelineAsset urp, out uint count, out Vector3 splits)
    {
        count = 0; splits = Vector3.zero;
        if (urp == null) return;
        count = (uint)urp.shadowCascadeCount;
        if (count == 2) splits = new Vector3(urp.cascade2Split, 0f, 0f);
        else if (count == 3) splits = new Vector3(urp.cascade3Split.x, urp.cascade3Split.y, 0f);
        else if (count == 4) splits = urp.cascade4Split;
    }

    /// <summary>Main light's effective soft-shadow tier as engine ShadowFilterQuality; the engine's widest kernel is 5x5, so URP Medium and High both map to high.</summary>
    static uint ResolveShadowFilter(UniversalRenderPipelineAsset urp)
    {
        Light sun = RenderSettings.sun;
        if (sun == null)
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (l.type == LightType.Directional) { sun = l; break; }
        if (urp == null || sun == null || sun.shadows == LightShadows.None) return 0u;
        if (!urp.supportsSoftShadows || sun.shadows == LightShadows.Hard) return 1u;
        var quality = sun.TryGetComponent<UniversalAdditionalLightData>(out var data)
            ? data.softShadowQuality : SoftShadowQuality.UsePipelineSettings;
        // the asset-level quality is internal in URP, so read its serialized field
        if (quality == SoftShadowQuality.UsePipelineSettings)
            quality = (SoftShadowQuality)new SerializedObject(urp).FindProperty("m_SoftShadowQuality").intValue;
        return quality == SoftShadowQuality.Low ? 2u : 3u;
    }

    static float ActiveShadowDistance()
    {
        var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
        if (rp != null)
        {
            var prop = rp.GetType().GetProperty("shadowDistance");
            if (prop != null && prop.PropertyType == typeof(float))
                return (float)prop.GetValue(rp);
        }
        return QualitySettings.shadowDistance;
    }

    // Read the scene's URP post-processing Volume into engine EcsPostProcessing values. Picks the
    // highest-priority active global Volume; only parameters whose override is enabled are applied,
    // the rest stay at the engine identity. URP has no exposure/brightness/gamma overrides, so those
    // keep their defaults.
    // the highest-priority active global Volume, the profile URP's stack resolves first
    static VolumeProfile FindActivePostProfile()
    {
        VolumeProfile profile = null;
        float bestPriority = float.NegativeInfinity;
        foreach (var v in Object.FindObjectsOfType<Volume>())
        {
            if (!v.isActiveAndEnabled || !v.isGlobal || v.sharedProfile == null) continue;
            if (v.priority >= bestPriority) { bestPriority = v.priority; profile = v.sharedProfile; }
        }
        return profile;
    }

    static HZSceneExporter.PostProcessingSettings ReadPostProcessing()
    {
        var pp = HZSceneExporter.PostProcessingSettings.Default();
        VolumeProfile profile = FindActivePostProfile();
        if (profile == null) { Debug.Log("[HZpp] no active global post-processing Volume; using defaults"); return pp; }

        if (profile.TryGet<Tonemapping>(out var tm) && tm.active && tm.mode.overrideState)
            pp.tonemapMode = MapTonemap(tm.mode.value);
        if (profile.TryGet<ColorAdjustments>(out var ca) && ca.active)
        {
            // The engine remaps white-balance (/65) and vignette (×3/×5) itself, but NOT contrast,
            // saturation, or hue — so we must feed those in the post-remap form the shader expects:
            //   contrast/saturation: Unity slider -100..100 -> /100 (engine adds the +1.0)
            //   hueShift: Unity degrees -180..180 -> /360
            // post_exposure stays raw EV (engine does exp2). color_filter raw.
            if (ca.postExposure.overrideState) pp.postExposure = ca.postExposure.value;
            if (ca.contrast.overrideState)     pp.contrast = ca.contrast.value / 100f;
            // URP ColorGradingLutPass feeds colorFilter.value.linear to the LUT builder
            if (ca.colorFilter.overrideState)  pp.colorFilter = ca.colorFilter.value.linear;
            if (ca.hueShift.overrideState)     pp.hueShift = ca.hueShift.value / 360f;
            if (ca.saturation.overrideState)   pp.saturation = ca.saturation.value / 100f;
        }
        if (profile.TryGet<WhiteBalance>(out var wb) && wb.active)
        {
            if (wb.temperature.overrideState) pp.whiteBalanceTemperature = wb.temperature.value;
            if (wb.tint.overrideState)        pp.whiteBalanceTint = wb.tint.value;
        }
        if (profile.TryGet<Vignette>(out var vg) && vg.active)
        {
            if (vg.color.overrideState)      pp.vignetteColor = vg.color.value;
            if (vg.center.overrideState)     pp.vignetteCenter = vg.center.value;
            if (vg.intensity.overrideState)  pp.vignetteIntensity = vg.intensity.value;
            if (vg.smoothness.overrideState) pp.vignetteSmoothness = vg.smoothness.value;
            if (vg.rounded.overrideState)    pp.vignetteRounded = vg.rounded.value;
        }
        if (profile.TryGet<Bloom>(out var bl) && bl.active)
        {
            if (bl.intensity.overrideState) pp.bloomIntensity = bl.intensity.value;
            if (bl.threshold.overrideState) pp.bloomThreshold = bl.threshold.value;
            if (bl.scatter.overrideState)   pp.bloomScatter = bl.scatter.value;
            if (bl.clamp.overrideState)     pp.bloomClamp = bl.clamp.value;
            if (bl.tint.overrideState)      pp.bloomTint = bl.tint.value;
        }
        // URP's stack resolves a parameter that is not overridden to the component DEFAULT, not the value stored in the profile
        if (profile.TryGet<SplitToning>(out var st) && st.active)
        {
            var d = ScriptableObject.CreateInstance<SplitToning>();
            Color sh = Resolved(st.shadows, d.shadows), hi = Resolved(st.highlights, d.highlights);
            pp.splitShadows = new Vector3(sh.r - 0.5f, sh.g - 0.5f, sh.b - 0.5f);
            pp.splitHighlights = new Vector3(hi.r - 0.5f, hi.g - 0.5f, hi.b - 0.5f);
            pp.splitBalance = Resolved(st.balance, d.balance) / 100f;
            Object.DestroyImmediate(d);
        }
        if (profile.TryGet<ChannelMixer>(out var cm) && cm.active)
        {
            var d = ScriptableObject.CreateInstance<ChannelMixer>();
            pp.mixerRed = new Vector3(Resolved(cm.redOutRedIn, d.redOutRedIn) / 100f - 1f, Resolved(cm.redOutGreenIn, d.redOutGreenIn) / 100f, Resolved(cm.redOutBlueIn, d.redOutBlueIn) / 100f);
            pp.mixerGreen = new Vector3(Resolved(cm.greenOutRedIn, d.greenOutRedIn) / 100f, Resolved(cm.greenOutGreenIn, d.greenOutGreenIn) / 100f - 1f, Resolved(cm.greenOutBlueIn, d.greenOutBlueIn) / 100f);
            pp.mixerBlue = new Vector3(Resolved(cm.blueOutRedIn, d.blueOutRedIn) / 100f, Resolved(cm.blueOutGreenIn, d.blueOutGreenIn) / 100f, Resolved(cm.blueOutBlueIn, d.blueOutBlueIn) / 100f - 1f);
            Object.DestroyImmediate(d);
        }
        if (profile.TryGet<DepthOfField>(out var dof) && dof.active && dof.mode.overrideState)
        {
            var d = ScriptableObject.CreateInstance<DepthOfField>();
            pp.dofMode = dof.mode.value == DepthOfFieldMode.Bokeh ? 1u : dof.mode.value == DepthOfFieldMode.Gaussian ? 2u : 0u;
            pp.dofFocusDistance = Resolved(dof.focusDistance, d.focusDistance);
            pp.dofFocalLength = Resolved(dof.focalLength, d.focalLength) / 1000f; // URP focalLength is millimetres
            pp.dofAperture = Resolved(dof.aperture, d.aperture);
            pp.dofBladeCount = (uint)Resolved(dof.bladeCount, d.bladeCount);
            pp.dofBladeCurvature = Resolved(dof.bladeCurvature, d.bladeCurvature);
            pp.dofBladeRotation = Resolved(dof.bladeRotation, d.bladeRotation);
            pp.dofMaxCocPixels = 0f; // engine default (32 px at 1080p); URP Bokeh caps near 14 px
            pp.dofRangeStart = Resolved(dof.gaussianStart, d.gaussianStart);
            pp.dofRangeEnd = Resolved(dof.gaussianEnd, d.gaussianEnd);
            pp.dofRangeMaxRadius = Resolved(dof.gaussianMaxRadius, d.gaussianMaxRadius);
            pp.dofQuality = Resolved(dof.highQualitySampling, d.highQualitySampling) ? 3u : 2u;
            if (s_dofFocusOverride > 0f) pp.dofFocusDistance = s_dofFocusOverride;
            pp.dofDebugView = s_dofDebugView;
            Object.DestroyImmediate(d);
        }
        Debug.Log($"[HZpp] '{profile.name}': contrast={pp.contrast} saturation={pp.saturation} "
                + $"postExp={pp.postExposure} wbTemp={pp.whiteBalanceTemperature} wbTint={pp.whiteBalanceTint} "
                + $"vignette(int={pp.vignetteIntensity}) tonemap={pp.tonemapMode} "
                + $"bloom(int={pp.bloomIntensity} thresh={pp.bloomThreshold} scatter={pp.bloomScatter}) "
                + $"split(sh={pp.splitShadows} hi={pp.splitHighlights} bal={pp.splitBalance}) mixer(r={pp.mixerRed} g={pp.mixerGreen} b={pp.mixerBlue}) "
                + $"dof(mode={pp.dofMode} focus={pp.dofFocusDistance} f={pp.dofFocalLength} N={pp.dofAperture} blades={pp.dofBladeCount} q={pp.dofQuality})");
        return pp;
    }

    static T Resolved<T>(VolumeParameter<T> p, VolumeParameter<T> defaults) => p.overrideState ? p.value : defaults.value;

    // URP TonemappingMode -> engine TonemapMode (NONE=0, ACES=4, NEUTRAL=6).
    static uint MapTonemap(TonemappingMode m)
    {
        switch (m)
        {
            case TonemappingMode.Neutral: return 6u;
            case TonemappingMode.ACES:    return 4u;
            default:                      return 0u;
        }
    }

    static bool HasMeshRenderer(GameObject go, bool deep)
    {
        if (deep)
            return go.GetComponentInChildren<MeshRenderer>(true) != null
                || go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        return go.GetComponent<MeshRenderer>() != null || go.GetComponent<SkinnedMeshRenderer>() != null;
    }

    static bool TryWorldBounds(GameObject go, out Bounds bounds)
    {
        // Build world AABB from each renderer's authored mesh bounds transformed by its transform.
        // Renderer.bounds is avoided because SkinnedMeshRenderer.bounds is bone-pose-driven and can
        // balloon to scene-scale when the skeleton isn't in a clean rest state.
        bounds = default; bool init = false;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            Mesh mesh = null;
            if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
            else if (r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var mf)) mesh = mf.sharedMesh;
            if (mesh == null) continue;

            var lb = mesh.bounds;
            var m = r.transform.localToWorldMatrix;
            Vector3 c = lb.center, e = lb.extents;
            for (int s = 0; s < 8; s++)
            {
                Vector3 corner = c + new Vector3(
                    ((s & 1) == 0 ? -e.x : e.x),
                    ((s & 2) == 0 ? -e.y : e.y),
                    ((s & 4) == 0 ? -e.z : e.z));
                Vector3 w = m.MultiplyPoint3x4(corner);
                if (!init) { bounds = new Bounds(w, Vector3.zero); init = true; }
                else bounds.Encapsulate(w);
            }
        }
        return init;
    }

    /// <summary>
    /// Convenience for the Boids scene specifically: open Boids.unity + its SubScenes,
    /// then export. (Plain "Export Active Scene" only walks already-loaded scenes, so
    /// for a subscene-based scene you must have the SubScenes open first.)
    /// </summary>
    public static void ExportBoids()
    {
        OpenBoidsScene();
        ExportScene();
    }

    // ===================== Boids (DOTS sample) custom components =====================
    // The Unity Boids sample authors data via MonoBehaviour "Authoring" components that
    // bake to ECS at runtime. We read them by type-name + reflection (the Boids scripts
    // have no asmdef the editor assembly directly references) and emit matching engine
    // components into the scene .hzt. The engine must define reflected structs with these
    // exact field names (BoidSchool / BoidTarget / BoidObstacle / SampledAnimationClip),
    // else the entity cooker silently drops the unknown `component` block.

    static string FF(float v) => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

    static void AddCustom(ref HZSceneExporter.EntityExportInfo info, string block)
    {
        info.customComponents ??= new List<string>();
        info.customComponents.Add(block);
    }

    static int ReflInt(object o, string field, int def)
    {
        var f = o.GetType().GetField(field);
        return f != null ? System.Convert.ToInt32(f.GetValue(o)) : def;
    }
    static float ReflFloat(object o, string field, float def)
    {
        var f = o.GetType().GetField(field);
        return f != null ? System.Convert.ToSingle(f.GetValue(o)) : def;
    }
    static object ReflObj(object o, string field)
    {
        var f = o.GetType().GetField(field);
        return f != null ? f.GetValue(o) : null;
    }
    static MonoBehaviour FindMono(GameObject go, string typeName)
    {
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetType().Name == typeName) return mb;
        return null;
    }

    static void AppendBoidComponents(GameObject go,
        ref HZSceneExporter.EntityExportInfo info,
        Dictionary<string, string> exportedGlbs)
    {
        // --- BoidSchoolAuthoring → BoidSchool (spawner: count, radius, flocking weights, model) ---
        var school = go.GetComponent("BoidSchoolAuthoring") as MonoBehaviour;
        if (school != null)
        {
            int count = ReflInt(school, "Count", 0);
            float radius = ReflFloat(school, "InitialRadius", 0f);
            var prefab = ReflObj(school, "Prefab") as GameObject;

            // Flocking settings come from the prefab's BoidAuthoring (defaults = Unity sample defaults).
            float cellRadius = 8f, sep = 1f, align = 1f, target = 2f, aversion = 30f, speed = 25f;
            string modelPath = "";
            if (prefab != null)
            {
                var boid = FindMono(prefab, "BoidAuthoring");
                if (boid != null)
                {
                    cellRadius = ReflFloat(boid, "CellRadius", cellRadius);
                    sep = ReflFloat(boid, "SeparationWeight", sep);
                    align = ReflFloat(boid, "AlignmentWeight", align);
                    target = ReflFloat(boid, "TargetWeight", target);
                    aversion = ReflFloat(boid, "ObstacleAversionDistance", aversion);
                    speed = ReflFloat(boid, "MoveSpeed", speed);
                }

                var pmf = prefab.GetComponentInChildren<MeshFilter>(true);
                var pmr = prefab.GetComponentInChildren<MeshRenderer>(true);
                if (pmf != null && pmr != null && pmf.sharedMesh != null)
                    modelPath = ExportStaticGlbForRenderer(pmr, pmf.sharedMesh, exportedGlbs);
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("component BoidSchool");
            sb.AppendLine($"  count {count}");
            sb.AppendLine($"  initial_radius {FF(radius)}");
            sb.AppendLine($"  cell_radius {FF(cellRadius)}");
            sb.AppendLine($"  separation_weight {FF(sep)}");
            sb.AppendLine($"  alignment_weight {FF(align)}");
            sb.AppendLine($"  target_weight {FF(target)}");
            sb.AppendLine($"  obstacle_aversion_distance {FF(aversion)}");
            sb.AppendLine($"  move_speed {FF(speed)}");
            sb.Append($"  model_path {modelPath}");
            AddCustom(ref info, sb.ToString());
            Debug.Log($"[HZ] BoidSchool '{go.name}': count={count} radius={radius} model={modelPath}");
        }

        // --- BoidTargetAuthoring → BoidTarget + SampledAnimationClip (reuse committed clip) ---
        if (go.GetComponent("BoidTargetAuthoring") != null)
        {
            AddCustom(ref info, "component BoidTarget\n  _ 0");
            AddCustom(ref info, $"component SampledAnimationClip\n  clip_path boids/{SanitizeName(go.name)}_animation.hzt");
        }

        // --- BoidObstacleAuthoring → BoidObstacle + SampledAnimationClip ---
        if (go.GetComponent("BoidObstacleAuthoring") != null)
        {
            AddCustom(ref info, "component BoidObstacle\n  _ 0");
            AddCustom(ref info, $"component SampledAnimationClip\n  clip_path boids/{SanitizeName(go.name)}_animation.hzt");
        }
    }

    /// <summary>
    /// Open a scene (Single) then additively open all SubScenes it references so the
    /// full graph is loaded. Returns once everything is open.
    /// </summary>
    static bool OpenSceneWithSubScenes(string mainScenePath)
    {
        if (EditorApplication.isPlaying)
        {
            // Scene ops are illegal in play mode; request exit and bail. Re-run after.
            Debug.LogWarning("[HZ] In play mode — exiting play mode. Re-run the export once stopped.");
            EditorApplication.isPlaying = false;
            return false;
        }
        // an already-active scene keeps its unsaved edits; only its missing SubScenes are opened
        if (SceneManager.GetActiveScene().path != mainScenePath)
            EditorSceneManager.OpenScene(mainScenePath, OpenSceneMode.Single);

        // Resolve SubScene components → SceneAsset path, open additively if not already open.
        var subScenes = Object.FindObjectsByType<SubScene>(FindObjectsSortMode.None);
        foreach (var ss in subScenes)
        {
            if (ss.SceneAsset == null) continue;
            string path = AssetDatabase.GetAssetPath(ss.SceneAsset);
            if (string.IsNullOrEmpty(path)) continue;
            var existing = SceneManager.GetSceneByPath(path);
            if (existing.IsValid() && existing.isLoaded) continue;
            EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            Debug.Log($"[HZ] Opened SubScene additively: {path}");
        }
        return true;
    }

    /// <summary>
    /// Gather root GameObjects from every loaded scene (active + additively-opened
    /// SubScenes). Single-scene exports naturally see just the one scene.
    /// </summary>
    static GameObject[] GetAllLoadedRoots()
    {
        var roots = new List<GameObject>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (s.isLoaded) roots.AddRange(s.GetRootGameObjects());
        }
        return roots.ToArray();
    }

    [MenuItem("Hz Exporter/Export Active Scene")]
    public static void ExportMutualsTest()
    {
        ExportScene();
    }

    public static void ExportScene()
    {
        // Clear the per-export PNG cache so we don't leak texture bytes across runs.
        HZGlbWriter.ResetTextureCache();

        var scene = SceneManager.GetActiveScene();
        var roots = GetAllLoadedRoots();
        var entities = new List<HZSceneExporter.EntityExportInfo>();

        // Nuke the scene's chunk dir up front: the terrain export (runs inside the
        // entity loop) and the chunk writer both write here, so the clean-slate must
        // happen once before either touches it. Layout mirrors the engine asset paths
        // exactly: chunk/terrain/cloth live at <out>/<scene>/ (asset path unity/<scene>/),
        // the manifest at <out>/scenes/<scene>.hzt (asset path unity/scenes/<scene>.hzt).
        string sceneOutDir = $"{OutputDir}/{SanitizeName(scene.name)}";
        if (System.IO.Directory.Exists(sceneOutDir))
            System.IO.Directory.Delete(sceneOutDir, true);
        System.IO.Directory.CreateDirectory(sceneOutDir);

        // Map from GameObject instance ID to stable ID for parent references
        var goToStableId = new Dictionary<EntityId, ulong>();

        // First pass: assign stable IDs to all GameObjects via GlobalObjectId hash
        foreach (var root in roots)
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                goToStableId[t.gameObject.GetEntityId()] = StableIdFor(t.gameObject);

        // Collect GameObjects to skip: skeleton bones owned by each Animator. A nested Animator
        // (e.g. an attached separate skinned model) is its OWN character — its GO stays exportable
        // and its bones are skipped only by its own pass, not by an outer Animator's pass. So we
        // walk each Animator's descendants and stop at any deeper Animator boundary.
        var skipGOs = new HashSet<EntityId>();
        foreach (var root in roots)
        {
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                var animGO = animator.gameObject;
                AddBoneSkips(animGO.transform, animGO, skipGOs);
            }
        }

        // Skip SubScene placeholder GameObjects (the "Simulation"/"Environment" proxies
        // in the main scene). Their actual content is in the additively-opened scenes;
        // the placeholder itself has no renderer and would export as an empty entity.
        foreach (var root in roots)
            foreach (var ss in root.GetComponentsInChildren<SubScene>(true))
                skipGOs.Add(ss.gameObject.GetEntityId());

        // LODGroup: keep LOD0's renderer(s) as base entities; skip every renderer that
        // belongs only to LOD1+. Build lodExtrasByBaseGO so the entity-emission pass can
        // attach extra_lods to the base. Overlap-safe: if a renderer is referenced by
        // both LOD0 and a deeper LOD, it stays exportable.
        //
        // Unity's lods[i].screenRelativeTransitionHeight is the screen-fraction threshold
        // BELOW which lods[i] becomes inactive. That maps to lods[i+1].switch_coverage
        // in engine units (engine LOD0 stays active down to FLT_MAX; LOD1+ each carry
        // their own threshold). We drop the last LOD's threshold — it's the cull point,
        // which v1 doesn't support.
        //
        // lodExtrasByBaseGO: key = LOD0 renderer's GameObject instanceID,
        //                   value = ordered list of {Renderer rdr, float switch_coverage}.
        var lodExtrasByBaseGO = new Dictionary<EntityId, List<(Renderer rdr, float switchCoverage)>>();
        foreach (var root in roots)
        {
            foreach (var lodGroup in root.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lodGroup.GetLODs();
                if (lods == null || lods.Length == 0) continue;

                // Find LOD0's base renderer. Skip empty/null entries entirely.
                Renderer baseRdr = null;
                if (lods[0].renderers != null)
                {
                    foreach (var r in lods[0].renderers)
                        if (r != null) { baseRdr = r; break; }
                }
                if (baseRdr == null)
                {
                    Debug.LogWarning($"[HZ] LODGroup '{lodGroup.name}': LOD0 has no renderer; skipping group");
                    continue;
                }

                var baseKeepIds = new HashSet<EntityId>();
                foreach (var r in lods[0].renderers)
                    if (r != null) baseKeepIds.Add(r.gameObject.GetEntityId());

                var extras = new List<(Renderer, float)>();
                for (int li = 1; li < lods.Length; li++)
                {
                    if (lods[li].renderers == null || lods[li].renderers.Length == 0)
                        continue; // null/empty LOD slot — treat as absent
                    Renderer lodRdr = null;
                    foreach (var r in lods[li].renderers)
                        if (r != null) { lodRdr = r; break; }
                    if (lodRdr == null) continue;

                    // Threshold to switch INTO this LOD = the previous LOD's transition height.
                    float switchCoverage = lods[li - 1].screenRelativeTransitionHeight;
                    extras.Add((lodRdr, switchCoverage));

                    // Skip every non-base LOD renderer that isn't also a base.
                    foreach (var r in lods[li].renderers)
                    {
                        if (r == null) continue;
                        var id = r.gameObject.GetEntityId();
                        if (!baseKeepIds.Contains(id)) skipGOs.Add(id);
                    }
                }

                if (extras.Count > 0)
                {
                    var baseId = baseRdr.gameObject.GetEntityId();
                    lodExtrasByBaseGO[baseId] = extras;
                }
            }
        }

        // GLB dedup: key by (mesh, materials[]) — same mesh with different material assignments
        // must produce distinct GLBs since materials (including textures) are embedded.
        var exportedGlbs = new Dictionary<string, string>();
        var usedGlbNames = new HashSet<string>();

        // Terrain dedup: shared TerrainData asset → exported once.
        // key = TerrainData asset path (or instance id when not a project asset) → asset-ref path.
        var exportedTerrains = new Dictionary<string, string>();

        // Grass albedo dedup: shared grass texture → one TextureBlobAsset in glb/textures.
        // key = texture asset path (or instance id) → asset-ref path.
        var exportedGrassAlbedos = new Dictionary<string, string>();

        // Deferred skinned mesh exports: collect animations per character before exporting
        // Key = character name (FBX filename with @... stripped)
        var deferredSkinned = new Dictionary<string, DeferredSkinnedExport>();

        // For bone attachment: per-Animator set of Transforms that are actually skinned bones
        // (members of some SMR's bones[] under that Animator). Lets a nested entity look up
        // "is my Unity ancestor a bone of the outer character that owns me?" in O(1) per ancestor.
        // Only includes bones whose nearest Animator IS this Animator (so a deeper Animator's
        // bones don't pollute its parent's set).
        var bonesByAnimator = new Dictionary<EntityId, HashSet<Transform>>(); // animator GO entityID → bones
        foreach (var rGo in roots)
        {
            foreach (var anim in rGo.GetComponentsInChildren<Animator>(true))
            {
                var set = new HashSet<Transform>();
                foreach (var smr in anim.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (NearestAnimator(smr.transform) != anim) continue;
                    if (smr.bones == null) continue;
                    foreach (var b in smr.bones)
                        if (b != null) set.Add(b);
                }
                bonesByAnimator[anim.gameObject.GetEntityId()] = set;
            }
        }

        // Pose every rig at "idle frame 0" before sampling entity transforms. The FBX bind
        // pose is sometimes degenerate (e.g. tail rig collapsed at the floor) — what the user
        // sees in Unity when the Animator runs is the idle animation, which puts everything
        // at its expected visual rest position. Sample the first AnimationClip at t=0 of each
        // Animator to reproduce that pose deterministically.
        foreach (var rootGO in roots)
        {
            foreach (var animator in rootGO.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController == null) continue;
                var clips = animator.runtimeAnimatorController.animationClips;
                if (clips == null || clips.Length == 0) continue;
                AnimationClip first = null;
                foreach (var c in clips)
                {
                    if (c == null || c.name.StartsWith("__preview__")) continue;
                    first = c; break;
                }
                if (first != null) first.SampleAnimation(animator.gameObject, 0f);
            }
        }

        // Second pass: export each GameObject as an entity.
        foreach (var root in roots)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;

                // Skip skeleton bones and SMR children of skinned roots
                if (skipGOs.Contains(go.GetEntityId())) continue;

                ulong stableId = goToStableId[go.GetEntityId()];

                // Find nearest exported ancestor (skip skeleton bones)
                ulong parentStableId = 0;
                Transform parentAnchor = null;  // the Transform whose stableId we're parenting to
                Transform ancestor = t.parent;
                while (ancestor != null)
                {
                    var ancestorId = ancestor.gameObject.GetEntityId();
                    if (!skipGOs.Contains(ancestorId) && goToStableId.TryGetValue(ancestorId, out ulong pid))
                    {
                        parentStableId = pid;
                        parentAnchor = ancestor;
                        break;
                    }
                    ancestor = ancestor.parent;
                }

                // Bone attachment: if this entity's Unity parent chain crosses a bone of the
                // outer character we're being parented to, we want to attach to that bone at
                // runtime (so the entity follows animated skeleton motion). Walk from t.parent
                // upward toward parentAnchor; if any ancestor is in the outer animator's
                // bones[] set, that's our bone anchor.
                Transform boneAnchor = null;
                if (parentAnchor != null && t.parent != null
                    && bonesByAnimator.TryGetValue(parentAnchor.gameObject.GetEntityId(), out var ownerBones)
                    && ownerBones != null && ownerBones.Count > 0)
                {
                    for (Transform a = t.parent; a != null && a != parentAnchor; a = a.parent)
                    {
                        if (ownerBones.Contains(a)) { boneAnchor = a; break; }
                    }
                }

                // Local TRS:
                //  - Bone-attached: relative to the bone Transform (engine composes parent.world ×
                //    bone_global × child.local once the spec resolves to a bone_index).
                //  - Otherwise, when bones (or other skipped GOs) sit between t and the actual
                //    exported parent, t.localPosition/Rotation/Scale would be relative to the
                //    wrong frame (the immediate Unity parent). Recompute relative to parentAnchor.
                //  - Else use t.localPosition (parent matches).
                Vector3 localPos; Quaternion localRot; Vector3 localScl;
                if (boneAnchor != null)
                {
                    Matrix4x4 m = boneAnchor.worldToLocalMatrix * t.localToWorldMatrix;
                    Vector4 c3 = m.GetColumn(3);
                    localPos = new Vector3(c3.x, c3.y, c3.z);
                    localRot = m.rotation;
                    localScl = m.lossyScale;
                }
                else if (parentAnchor != null && parentAnchor != t.parent)
                {
                    Matrix4x4 m = parentAnchor.worldToLocalMatrix * t.localToWorldMatrix;
                    Vector4 c3 = m.GetColumn(3);
                    localPos = new Vector3(c3.x, c3.y, c3.z);
                    localRot = m.rotation;
                    localScl = m.lossyScale;
                }
                else
                {
                    localPos = t.localPosition;
                    localRot = t.localRotation;
                    localScl = t.localScale;
                }

                // Base entity info — every entity gets this
                var info = new HZSceneExporter.EntityExportInfo {
                    name = go.name,
                    position = localPos,
                    rotation = localRot,
                    scale = localScl,
                    worldPosition = t.position,
                    worldRotation = t.rotation,
                    worldScale = t.lossyScale,
                    visible = go.activeInHierarchy,
                    castsShadow = false,
                    stableId = stableId,
                    parentStableId = parentStableId,
                    isBoneAttached = boneAnchor != null,
                    boneAttachmentName = boneAnchor != null ? boneAnchor.name : null,
                };

                // Check for skinned mesh (root of skinned hierarchy). SMRs that belong to a
                // *deeper* Animator (a nested attached character) are filtered out — they get
                // exported when we visit that nested Animator's GO as its own entity.
                var thisAnimator = go.GetComponent<Animator>();
                var allSmrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var validSmrs = new List<SkinnedMeshRenderer>();
                foreach (var s in allSmrs)
                    if (s.sharedMesh != null && s.bones != null && s.bones.Length > 0
                        && NearestAnimator(s.transform) == thisAnimator)
                        validSmrs.Add(s);

                if (validSmrs.Count > 0 && thisAnimator != null)
                {
                    SkinnedMeshRenderer primarySmr = validSmrs[0];
                    foreach (var s in validSmrs)
                        if (s.bones.Length > primarySmr.bones.Length)
                            primarySmr = s;

                    // Character name = FBX filename with @... stripped
                    string charName = GetCharacterName(primarySmr.sharedMesh);
                    string glbAssetPath = $"{AssetPrefix}/glb/{charName}.glb";

                    // Defer export — collect mesh info on first encounter
                    if (!deferredSkinned.ContainsKey(charName))
                    {
                        var smrArray = validSmrs.ToArray();
                        // sharedMaterials[] preserves multi-material SMRs (one per submesh).
                        var mats = new Material[validSmrs.Count][];
                        for (int si = 0; si < validSmrs.Count; si++)
                            mats[si] = validSmrs[si].sharedMaterials;

                        deferredSkinned[charName] = new DeferredSkinnedExport {
                            smrs = smrArray,
                            materials = mats,
                            primarySmr = primarySmr,
                            entityRoot = go.transform,
                            glbPath = $"{OutputDir}/glb/{charName}.glb",
                            glbAssetPath = glbAssetPath,
                            animClips = new List<HZGlbWriter.AnimClipInfo>(),
                        };
                    }

                    info.modelAssetPath = glbAssetPath;
                    info.bounds = primarySmr.bounds;
                    info.castsShadow = true;

                    // Collect animation clips for this character
                    var animator = go.GetComponent<Animator>();
                    if (animator.runtimeAnimatorController != null)
                    {
                        var deferred = deferredSkinned[charName];
                        var existingNames = new HashSet<string>();
                        foreach (var existing in deferred.animClips)
                            existingNames.Add(existing.name);

                        foreach (var clip in animator.runtimeAnimatorController.animationClips)
                        {
                            string clipName = SanitizeName(clip.name);
                            if (!existingNames.Contains(clipName))
                            {
                                deferred.animClips.Add(new HZGlbWriter.AnimClipInfo {
                                    clip = clip,
                                    name = clipName,
                                    skeletonRoot = go,
                                });
                                existingNames.Add(clipName);
                            }
                        }

                        if (deferred.animClips.Count > 0)
                        {
                            // Point to the cooked animation .hza that the engine extracts from the GLB
                            // Engine cooks unity/SeaGull.glb → cooked/unity/SeaGull/{clipName}.hza
                            string firstClipName = SanitizeName(animator.runtimeAnimatorController.animationClips[0].name);
                            info.animationAssetPath = $"{AssetPrefix}/glb/{charName}/{firstClipName}.hza";
                        }
                    }

                    if (HZClothIntegration.ExportMeshCloths != null)
                    {
                        // MeshCloth authoring → per-character cloth blobs + EcsClothRefs.
                        // Namespaced by character (scene/<char>/cloth) so two characters with
                        // same-named cloths (e.g. both "skirt") don't collide on one flat file.
                        string clothSceneNm = SanitizeName(scene.name);
                        string clothOutDir = $"{OutputDir}/{clothSceneNm}/{charName}/cloth";
                        string clothAssetPrefix = $"{AssetPrefix}/{clothSceneNm}/{charName}/cloth";
                        var clothFiles = HZClothIntegration.ExportMeshCloths(go, validSmrs.ToArray(), clothOutDir);
                        const int ECS_CLOTH_MAX_PER_MODEL = 24;
                        if (clothFiles.Count > ECS_CLOTH_MAX_PER_MODEL)
                        {
                            Debug.LogWarning($"[HZExport] {go.name}: {clothFiles.Count} MeshCloths exceeds " +
                                             $"ECS_CLOTH_MAX_PER_MODEL ({ECS_CLOTH_MAX_PER_MODEL}); extra cloths dropped");
                            clothFiles = clothFiles.GetRange(0, ECS_CLOTH_MAX_PER_MODEL);
                        }
                        if (clothFiles.Count > 0)
                        {
                            var csb = new System.Text.StringBuilder();
                            csb.AppendLine("component EcsClothRefs");
                            csb.AppendLine($"  cloths {clothFiles.Count}");
                            foreach (var fn in clothFiles)
                                csb.AppendLine($"    cloth {clothAssetPrefix}/{fn} ClothAuthoringBlobAsset");
                            AddCustom(ref info, csb.ToString());
                        }
                    }
                }
                // Terrain — export the TerrainData (heightmap + splatmaps + layer textures)
                // and reference it via EcsTerrainRef. Dedup so a TerrainData shared by
                // multiple Terrain components only exports once.
                else if (go.TryGetComponent<Terrain>(out var terrain) && terrain.terrainData != null)
                {
                    var td = terrain.terrainData;
                    string tdPath = AssetDatabase.GetAssetPath(td);
                    string terrainKey = string.IsNullOrEmpty(tdPath)
                        ? "tid:" + td.GetEntityId()
                        : tdPath;
                    if (!exportedTerrains.TryGetValue(terrainKey, out string terrainRef))
                    {
                        string sceneNm = SanitizeName(scene.name);
                        string terrainNm = SanitizeName(td.name);
                        // Filesystem layout mirrors the engine asset path exactly:
                        // <out>/<scene>/terrain/... ↔ unity/<scene>/terrain/...
                        string outDir = $"{OutputDir}/{sceneNm}/terrain/{terrainNm}";
                        string assetPrefix = $"{AssetPrefix}/{sceneNm}/terrain/{terrainNm}";
                        // Grass models flow through the same GLB dedup as scene meshes; grass
                        // albedo textures go to the shared glb/textures tree (deduped).
                        terrainRef = HZTerrainExporter.Export(terrain, outDir, assetPrefix,
                            (mr, mesh) => ExportStaticGlbForRenderer(mr, mesh, exportedGlbs, grassShader: true),
                            (tex, hasAlpha) => ExportGrassAlbedo(tex, hasAlpha, exportedGrassAlbedos));
                        exportedTerrains[terrainKey] = terrainRef;
                    }
                    info.terrainAssetPath = terrainRef;
                    // grid_dim / lod_levels: runtime chunk-grid params. Match the engine
                    // TerrainSystem defaults (see ecs_terrain_init).
                    info.terrainGridDim = 32;
                    info.terrainLodLevels = 6;

                    // The engine treats EcsTerrainRef's WorldTransform.position.{x,z} as the
                    // terrain's MINIMUM CORNER; the chunk extends +world_width/+world_depth
                    // from there. A Unity terrain's transform.position IS its min corner and
                    // it extends +X/+Z. But the export mirrors X (negates it downstream) and
                    // leaves Z alone — and mirroring flips an interval, so Unity's min-X
                    // corner becomes the engine's MAX-X corner. To recover the engine min
                    // corner we shift X by the full world_width; Z needs no shift (Unity's
                    // min-Z corner is already the engine min-Z corner).
                    // Both Local and World get the same Unity-space delta; the terrain's
                    // parent has identity rotation/scale (verified: local + parent world sum
                    // linearly), so the hierarchy recompute stays consistent.
                    float shiftX = td.size.x;
                    info.position = new Vector3(info.position.x + shiftX, info.position.y,
                                                info.position.z);
                    info.worldPosition = new Vector3(info.worldPosition.x + shiftX,
                                                     info.worldPosition.y,
                                                     info.worldPosition.z);
                }
                // Static mesh — export as .glb, dedup by (mesh, material combo).
                else if (go.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null
                         && go.TryGetComponent<MeshRenderer>(out var mr))
                {
                    info.modelAssetPath = ExportStaticGlbForRenderer(mr, mf.sharedMesh, exportedGlbs);
                    info.bounds = mr.bounds;
                    info.castsShadow = mr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;

                    // If this renderer is the base (LOD0) of a LODGroup, export each
                    // extra LOD's mesh as a model asset and attach as extra_lods.
                    if (lodExtrasByBaseGO.TryGetValue(go.GetEntityId(), out var extras))
                    {
                        info.extraLods = new List<(string, float)>(extras.Count);
                        foreach (var (lodRdr, switchCov) in extras)
                        {
                            // Only static MeshRenderer LODs supported in v1; warn and skip otherwise.
                            if (lodRdr is MeshRenderer lodMr
                                && lodRdr.TryGetComponent<MeshFilter>(out var lodMf)
                                && lodMf.sharedMesh != null)
                            {
                                string lodPath = ExportStaticGlbForRenderer(lodMr, lodMf.sharedMesh, exportedGlbs);
                                info.extraLods.Add((lodPath, switchCov));
                            }
                            else
                            {
                                Debug.LogWarning($"[HZ] LOD '{lodRdr.gameObject.name}' is not a static MeshRenderer; skipping (v1 limitation)");
                            }
                        }
                    }
                }
                // Camera
                else if (go.TryGetComponent<Camera>(out var cam))
                {
                    info.isCamera = true;
                    info.fov = cam.fieldOfView;
                    info.nearPlane = cam.nearClipPlane;
                    info.farPlane = cam.farClipPlane;

                    Debug.Log($"[HZ] Entity: {go.name} (camera)");
                }
                // Lights
                else if (go.TryGetComponent<Light>(out var light))
                {
                    // light.color IS the picker (sRGB) value and Unity's shaders use
                    // light.color.linear, so pass it RAW: the engine's srgb_to_linear
                    // then reproduces exactly what URP uses. LinearToSrgb here would
                    // double-encode (measured: sun 1.00/1.06/1.25x too bright in R/G/B).
                    Color c = light.color;
                    if (light.type == LightType.Directional)
                    {
                        Vector3 fwd = t.forward;
                        info.isDirectionalLight = true;
                        info.lightDirection = new Vector3(-fwd.x, fwd.y, fwd.z);
                        info.lightColor = new Vector3(c.r, c.g, c.b);
                        info.lightIntensity = light.intensity;
                        info.castShadow = light.shadows != LightShadows.None;
                        info.shadowStrength = light.shadowStrength;
                        // URP uses the pipeline asset's bias unless the light opts out of pipeline settings
                        var biasAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                        bool lightBias = biasAsset == null ||
                            (light.TryGetComponent<UniversalAdditionalLightData>(out var urpLightData) && !urpLightData.usePipelineSettings);
                        info.shadowBias = lightBias ? light.shadowBias : biasAsset.shadowDepthBias;
                        info.shadowNormalBias = lightBias ? light.shadowNormalBias : biasAsset.shadowNormalBias;
                        Debug.Log($"[HZ] Entity: {go.name} (directional light, shadows={info.castShadow})");
                    }
                    else if (light.type == LightType.Point)
                    {
                        info.isPointLight = true;
                        info.lightColor = new Vector3(c.r, c.g, c.b);
                        info.pointLightRadius = light.range;
                        info.lightIntensity = light.intensity;
                    }
                }

                // Boid authoring (DOTS sample) → engine boid components. Independent of the
                // mesh/camera/light branch above (a Target is both a static mesh AND a BoidTarget).
                AppendBoidComponents(go, ref info, exportedGlbs);

                entities.Add(info);
            }
        }

        // Map a timeline-bound Animator → the character key in deferredSkinned (by SMR membership),
        // so timeline clips can be fed into the right character's GLB. Null when unbound/unmatched.
        System.Func<Animator, string> resolveChar = anim =>
        {
            if (anim == null) return null;
            var set = new HashSet<SkinnedMeshRenderer>(anim.GetComponentsInChildren<SkinnedMeshRenderer>(true));
            foreach (var kv in deferredSkinned)
                foreach (var s in kv.Value.smrs)
                    if (set.Contains(s)) return kv.Key;
            return null;
        };
        // Add timeline AnimationTrack clips to the matching character's GLB anim set (dedup by name)
        // so they get baked + cooked alongside the controller clips, BEFORE the GLB export below.
        HZEmoteExporter.CollectTimelineClips(roots, resolveChar, (charName, clip) =>
        {
            if (!deferredSkinned.TryGetValue(charName, out var d)) return;
            string nm = SanitizeName(clip.name);
            if (d.animClips.Any(a => a.name == nm)) return;
            d.animClips.Add(new HZGlbWriter.AnimClipInfo { clip = clip, name = nm, skeletonRoot = d.entityRoot.gameObject });
        });

        // Export deferred skinned meshes (with collected animations)
        foreach (var kvp in deferredSkinned)
        {
            var d = kvp.Value;
            var animArray = d.animClips.Count > 0 ? d.animClips.ToArray() : null;
            HZGlbWriter.ExportSkinnedMesh(d.smrs, d.materials, d.primarySmr, d.entityRoot, d.glbPath, animArray);
        }

        // Timelines → EmoteBlobAsset .hzt (paths point at the just-baked GLB anims).
        HZEmoteExporter.Export(roots, resolveChar, OutputDir, AssetPrefix);

        // Terrain trees: TerrainData.treeInstances (separate from grass details). Each instance
        // becomes an individual entity referencing its prototype's model (LOD0 + extra LODs).
        CollectTreeEntities(roots, scene, entities, exportedGlbs);

        string sceneName = SanitizeName(scene.name);

        // Background comes from the MAIN camera's clear flags, not just whether a
        // skybox material exists. A scene can have a skybox assigned but a camera
        // that clears to a SolidColor — in that case the skybox is never visible,
        // so we don't export it; the engine clears to the camera's background color.
        Camera mainCam = Camera.main;
        if (mainCam == null)
        {
            foreach (var root in roots)
            {
                var c = root.GetComponentInChildren<Camera>(true);
                if (c != null) { mainCam = c; break; }
            }
        }
        bool cameraUsesSkybox = mainCam != null && mainCam.clearFlags == CameraClearFlags.Skybox;
        // Camera background: pass the raw (linear) color through unchanged — the engine
        // clears the swapchain with it directly (GPU sRGB-encodes on write), unlike
        // ambient/fog which the engine srgb_to_linears on use.
        Color clearCol = mainCam != null ? mainCam.backgroundColor : Color.black;

        // Skybox / IBL env. clearFlags alone is NOT enough: with no skybox material
        // Unity clears to the camera background and has no reflection source, so
        // exporting an env would add ambient the Unity frame does not have. A
        // camera-local Skybox component overrides RenderSettings.
        Material effectiveSky = RenderSettings.skybox;
        if (mainCam != null)
        {
            var camSky = mainCam.GetComponent<Skybox>();
            if (camSky != null && camSky.material != null) effectiveSky = camSky.material;
        }
        bool ambientFromSky =
            RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Skybox &&
            effectiveSky != null;
        bool reflFromCustom =
            RenderSettings.defaultReflectionMode ==
                UnityEngine.Rendering.DefaultReflectionMode.Custom &&
            RenderSettings.customReflectionTexture != null;
        bool reflFromSky =
            RenderSettings.defaultReflectionMode ==
                UnityEngine.Rendering.DefaultReflectionMode.Skybox &&
            effectiveSky != null;

        bool wantsSkybox = cameraUsesSkybox && effectiveSky != null;
        bool wantsEnv = wantsSkybox || ambientFromSky || reflFromCustom || reflFromSky;
        // Unity has skybox ambient and reflections only after Generate Lighting; until then both are black
        bool lightingGenerated = Lightmapping.lightingDataAsset != null;
        string envAssetPath = null;
        float envDiffuse = 0f, envSpecular = 0f;
        if (wantsEnv)
        {
            string hdrPath = $"{OutputDir}/environments/{sceneName}_skybox.hdr";
            if (HZSkyboxExporter.Export(hdrPath))
            {
                envAssetPath = $"{AssetPrefix}/environments/{sceneName}_skybox.hza";
                envDiffuse = ambientFromSky && lightingGenerated ? RenderSettings.ambientIntensity : 0f;
                envSpecular = reflFromCustom || (reflFromSky && lightingGenerated)
                    ? RenderSettings.reflectionIntensity : 0f;
            }
        }
        if (!wantsEnv)
            Debug.Log("[HZ] no skybox/reflection source — exporting flat clear + no IBL");

        // Scene settings (synthetic entity, no GameObject)
        {
            // Engine applies srgb_to_linear to ambient/shadow color; encode the picker value
            // (raw RenderSettings color, no .linear) so engine recovers what Unity uses.
            Color ambient = RenderSettings.ambientLight; // picker value; engine linearises
            Color shadowCol = ResolveShadowColor();
            // Skybox ambient exists only as the env diffuse term; Flat/Trilight ambientLight IS the ambient
            float flatAmbientIntensity =
                RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Skybox ? 0f : 1.0f;
            var shadowAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            ResolveShadowLayout(shadowAsset, out uint cascadeCount, out Vector3 cascadeSplits);
            // Unity FogMode: Linear=1, Exponential=2, ExponentialSquared=3 — same numbers as RendererFogMode.
            uint fogMode = RenderSettings.fog ? (uint)RenderSettings.fogMode : 0u;
            // RenderSettings.fogColor already comes back in the space the engine
            // expects (the engine srgb_to_linears it). Unlike ambient/light, do NOT
            // apply LinearToSrgb here — that double-encodes and brightens the fog.
            Color fogColRaw = RenderSettings.fogColor;
            // URP packs unity_FogParams.x = density/sqrt(ln2) and the shader uses exp2(...).
            // Engine uses exp(-d*z) for EXP and exp(-(d*z)^2) for EXP2. EXP2 round-trips
            // (the squared factor cancels ln2); EXP needs *sqrt(ln2) to match URP visually.
            float fogDensity = RenderSettings.fogDensity;
            if (fogMode == 2u) fogDensity *= Mathf.Sqrt(Mathf.Log(2.0f)); // ~0.8326
            entities.Add(new HZSceneExporter.EntityExportInfo {
                name = "SceneSettings",
                position = Vector3.zero,
                rotation = Quaternion.identity,
                scale = Vector3.one,
                visible = true,
                stableId = StableIdForString(scene.path + "::scene_settings"),
                isSceneSettings = true,
                ambientColor = new Vector3(ambient.r, ambient.g, ambient.b),
                ambientIntensity = flatAmbientIntensity,
                shadowColor = shadowCol,
                maxShadowDistance = ActiveShadowDistance(),
                postProcessing = ReadPostProcessing(),
                hasEnv = envAssetPath != null,
                envAssetPath = envAssetPath,
                envIntensity = envDiffuse,
                envSpecularIntensity = envSpecular,
                skyboxIntensity = 1.0f, // the exported HDR is rendered from Unity's skybox, exposure included
                shadowCascadeCount = cascadeCount,
                shadowCascadeSplits = cascadeSplits,
                shadowFilter = ResolveShadowFilter(shadowAsset),
                skybox = wantsSkybox,
                clearColor = clearCol,
                fogMode = fogMode,
                fogColor = new Vector3(fogColRaw.r, fogColRaw.g, fogColRaw.b),
                fogDensity = fogDensity,
                fogStart = RenderSettings.fogStartDistance,
                fogEnd = RenderSettings.fogEndDistance,
            });
        }
        string scenePath = $"{OutputDir}/scenes/{sceneName}.hzt";
        string sceneDir = $"{OutputDir}/{sceneName}";
        string sceneDirAssetPath = $"{AssetPrefix}/{sceneName}";

        HZSceneExporter.Export(entities.ToArray(), scenePath, sceneDir, sceneDirAssetPath);
        Debug.Log($"[HZ] Scene export done! {entities.Count} entities, {exportedGlbs.Count} unique GLBs");

        ExportShaderGraphs();

        if (HZExporter.HZExporterConfig.Instance.autoCopyOnExport)
            CopyToEngine();
    }

    struct DeferredSkinnedExport
    {
        public SkinnedMeshRenderer[] smrs;
        public Material[][] materials;   // [smrIdx][submeshIdx] — preserves multi-material SMRs
        public SkinnedMeshRenderer primarySmr;
        public Transform entityRoot;   // GO with the Animator — anchor for bone chain bake
        public string glbPath;
        public string glbAssetPath;
        public List<HZGlbWriter.AnimClipInfo> animClips;
    }

    /// <summary>
    /// Get character name from mesh FBX path. Strips @... suffix.
    /// E.g. "Big@Idle.fbx" → "Big", "SeaGull.fbx" → "SeaGull"
    /// </summary>
    static string GetCharacterName(Mesh mesh)
    {
        string name = GetMeshSourceName(mesh);
        int atIdx = name.IndexOf('@');
        if (atIdx > 0) name = name.Substring(0, atIdx);
        return name;
    }

    /// <summary>
    /// Stable signature for a material array — by asset path when the material is a project asset,
    /// else by instance-id fallback. Drives dedup: two entities sharing the same mesh + same
    /// material combo reuse one GLB; different materials → separate GLBs.
    /// </summary>
    static string GetMaterialsSignature(Material[] mats)
    {
        if (mats == null || mats.Length == 0) return "none";
        var parts = new string[mats.Length];
        for (int i = 0; i < mats.Length; i++)
        {
            var m = mats[i];
            if (m == null) { parts[i] = "null"; continue; }
            string ap = AssetDatabase.GetAssetPath(m);
            parts[i] = !string.IsNullOrEmpty(ap) ? ap : $"iid:{m.GetEntityId()}";
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// Produce a unique GLB filename: prefer `baseName`; on collision, append the first
    /// material's sanitized name, then a numeric suffix as needed. Preserves the legacy
    /// short name for the first entity that hits each baseName.
    /// </summary>
    static string UniqueGlbName(string baseName, Material[] mats, HashSet<string> used)
    {
        if (!used.Contains(baseName)) return baseName;
        string matSuffix = (mats != null && mats.Length > 0 && mats[0] != null)
            ? SanitizeName(mats[0].name) : "mat";
        string candidate = $"{baseName}_{matSuffix}";
        int n = 2;
        while (used.Contains(candidate)) { candidate = $"{baseName}_{matSuffix}_{n++}"; }
        return candidate;
    }

    // Export a static MeshRenderer's mesh+materials as a deduped GLB. Returns the
    // asset-relative path used by EcsModelRef.model. Shared by the entity static-mesh
    // path and the LOD entries on a LODGroup base.
    //
    // Dedup is by mesh only (not by material combo). Multiple entities sharing the
    // same mesh with different material overrides reuse one GLB; the engine extracts
    // material/textures from the GLB once and entity-level material overrides come
    // from external EcsMaterialRef. Splitting GLBs by material would cause the engine
    // GLB cooker to write the same texture filename from multiple sources → asset
    // collision.
    static string ExportStaticGlbForRenderer(MeshRenderer mr, Mesh mesh,
        Dictionary<string, string> exportedGlbs, bool grassShader = false)
    {
        // WORKAROUND: the builtin Plane is reused across scenes and exports to the same
        // `Plane.glb`. The cooker dedupes textures/materials by GLB name, so each scene's Plane
        // would clobber the others. Suffix the Plane (and only the Plane) with the active scene
        // name so its GLB — and thus its cooked material/textures — is unique per scene.
        string planeAssetPath = mesh != null ? AssetDatabase.GetAssetPath(mesh) : null;
        bool isBuiltinPlane = mesh != null && mesh.name == "Plane"
            && (string.IsNullOrEmpty(planeAssetPath) || planeAssetPath.StartsWith("Library/"));
        string planeSuffix = isBuiltinPlane
            ? "_" + SanitizeName(SceneManager.GetActiveScene().name) : "";

        // Grass meshes emit a `_grass` shader variant, so they must be a distinct GLB from any
        // normal static export of the same mesh — namespace both the dedup key and the filename.
        string meshKey = GetMeshKey(mesh) + planeSuffix + (grassShader ? "::grass" : "");
        if (exportedGlbs.TryGetValue(meshKey, out string cached))
            return cached;

        string glbName = GetMeshSourceName(mesh) + planeSuffix + (grassShader ? "_grass" : "");
        string glbPath = $"{OutputDir}/glb/{glbName}.glb";
        string glbAssetPath = $"{AssetPrefix}/glb/{glbName}.glb";

        HZGlbWriter.ExportStaticMesh(mesh, mr.sharedMaterials, glbPath, grassShader);
        exportedGlbs[meshKey] = glbAssetPath;
        return glbAssetPath;
    }

    // Resolved tree-prototype model: LOD0 GLB ref + extra LOD refs + render flags.
    sealed class TreeProtoModel
    {
        public string modelPath;
        public List<(string path, float switchCoverage)> extraLods;
        public bool castsShadow;
        public Vector3 localBoundsSize = Vector3.one;
    }

    // Export one tree prototype prefab as a model (LOD0 + extra LODs), reusing the static-mesh
    // GLB path. Mirrors the scene LODGroup handling: LOD0's first static MeshRenderer is the base,
    // LOD1+ become extra_lods with switch_coverage = the previous LOD's transition height.
    static TreeProtoModel ExportTreePrototype(GameObject prefab, Dictionary<string, string> exportedGlbs)
    {
        var pm = new TreeProtoModel();
        var lg = prefab.GetComponentInChildren<LODGroup>(true);
        if (lg != null)
        {
            var lods = lg.GetLODs();
            MeshRenderer baseMr = null; MeshFilter baseMf = null;
            if (lods.Length > 0 && lods[0].renderers != null)
                foreach (var r in lods[0].renderers)
                    if (r is MeshRenderer m && m.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
                    { baseMr = m; baseMf = mf; break; }
            if (baseMr == null)
            {
                Debug.LogWarning($"[HZtree] prototype '{prefab.name}': LOD0 has no static MeshRenderer; skipping");
                return pm;
            }
            pm.modelPath = ExportStaticGlbForRenderer(baseMr, baseMf.sharedMesh, exportedGlbs);
            pm.castsShadow = baseMr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
            pm.localBoundsSize = baseMf.sharedMesh.bounds.size;
            pm.extraLods = new List<(string, float)>();
            for (int li = 1; li < lods.Length; li++)
            {
                if (lods[li].renderers == null) continue;
                MeshRenderer lr = null; MeshFilter lf = null;
                foreach (var r in lods[li].renderers)
                    if (r is MeshRenderer m && m.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
                    { lr = m; lf = mf; break; }
                if (lr == null) continue;
                float switchCov = lods[li - 1].screenRelativeTransitionHeight;
                pm.extraLods.Add((ExportStaticGlbForRenderer(lr, lf.sharedMesh, exportedGlbs), switchCov));
            }
        }
        else
        {
            var mr = prefab.GetComponentInChildren<MeshRenderer>(true);
            var mf = mr != null ? mr.GetComponent<MeshFilter>() : null;
            if (mr == null || mf == null || mf.sharedMesh == null)
            {
                Debug.LogWarning($"[HZtree] prototype '{prefab.name}': no static mesh; skipping");
                return pm;
            }
            pm.modelPath = ExportStaticGlbForRenderer(mr, mf.sharedMesh, exportedGlbs);
            pm.castsShadow = mr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
            pm.localBoundsSize = mf.sharedMesh.bounds.size;
        }
        return pm;
    }

    // Emit one entity per TerrainData.treeInstance. World position = terrain origin +
    // normalized-position × terrain size; rotation = Y by instance.rotation (radians); scale =
    // (width, height, width). The scene exporter's negate-X / rotation-mirror lands them on the
    // X-mirrored terrain (verified against the X-flipped heightmap). Prototype models are deduped.
    static void CollectTreeEntities(GameObject[] roots, UnityEngine.SceneManagement.Scene scene,
        List<HZSceneExporter.EntityExportInfo> entities, Dictionary<string, string> exportedGlbs)
    {
        var protoCache = new Dictionary<EntityId, TreeProtoModel>();
        foreach (var root in roots)
            foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
            {
                var td = terrain.terrainData;
                if (td == null) continue;
                var protos = td.treePrototypes;
                var instances = td.treeInstances;
                if (protos == null || instances == null || instances.Length == 0) continue;

                Vector3 tpos = terrain.transform.position;
                Vector3 tsize = td.size;
                int count = 0;
                for (int i = 0; i < instances.Length; i++)
                {
                    var ti = instances[i];
                    if (ti.prototypeIndex < 0 || ti.prototypeIndex >= protos.Length) continue;
                    var prefab = protos[ti.prototypeIndex].prefab;
                    if (prefab == null) continue;

                    var key = prefab.GetEntityId();
                    if (!protoCache.TryGetValue(key, out var pm))
                    {
                        pm = ExportTreePrototype(prefab, exportedGlbs);
                        protoCache[key] = pm;
                    }
                    if (pm == null || string.IsNullOrEmpty(pm.modelPath)) continue;

                    Vector3 world = tpos + Vector3.Scale(ti.position, tsize);
                    Quaternion rot = Quaternion.AngleAxis(ti.rotation * Mathf.Rad2Deg, Vector3.up);
                    Vector3 scl = new Vector3(ti.widthScale, ti.heightScale, ti.widthScale);

                    entities.Add(new HZSceneExporter.EntityExportInfo {
                        name = $"{prefab.name}_{i}",
                        position = world, rotation = rot, scale = scl,
                        worldPosition = world, worldRotation = rot, worldScale = scl,
                        visible = true,
                        castsShadow = pm.castsShadow,
                        bounds = new Bounds(world, Vector3.Scale(pm.localBoundsSize, scl)),
                        modelAssetPath = pm.modelPath,
                        extraLods = pm.extraLods != null && pm.extraLods.Count > 0
                            ? new List<(string, float)>(pm.extraLods) : null,
                        parentStableId = 0,
                        stableId = StableIdForString($"{scene.path}::tree::{terrain.GetEntityId()}::{i}"),
                    });
                    count++;
                }
                Debug.Log($"[HZtree] '{td.name}': {count} tree entities from {protos.Length} prototypes ({protoCache.Count} models)");
            }
    }

    // Export a grass albedo texture as a standalone TextureBlobAsset under glb/textures/,
    // deduped by source texture. Returns the asset-relative .hzt ref the terrain manifest uses.
    // The grass system loads this texture directly (not via a GLB material), so it must be a
    // first-class TextureBlobAsset.
    static string ExportGrassAlbedo(Texture2D tex, bool hasAlpha,
        Dictionary<string, string> exportedGrassAlbedos)
    {
        if (tex == null) return null;

        string srcPath = AssetDatabase.GetAssetPath(tex);
        string key = !string.IsNullOrEmpty(srcPath) ? srcPath : $"iid:{tex.GetEntityId()}";
        if (exportedGrassAlbedos.TryGetValue(key, out string cached))
            return cached;

        string texName = SanitizeName(tex.name);
        string texHztPath = $"{OutputDir}/glb/textures/{texName}.hzt";
        string texPngPath = $"{OutputDir}/glb/textures/{texName}.png";
        string imageBasePath = $"/{AssetPrefix}/glb/textures/{texName}";
        string assetRef = $"{AssetPrefix}/glb/textures/{texName}.hzt";

        HZTextureExporter.ExportHzt(tex, imageBasePath, true, hasAlpha, texHztPath, texPngPath);
        exportedGrassAlbedos[key] = assetRef;
        return assetRef;
    }

    static string GetMeshKey(Mesh mesh)
    {
        string assetPath = AssetDatabase.GetAssetPath(mesh);
        if (string.IsNullOrEmpty(assetPath) || assetPath.StartsWith("Library/"))
            return $"builtin:{mesh.name}";
        // Multiple sub-Meshes can live in one FBX (e.g. Chest + ChestLid). Include mesh.name
        // so each sub-Mesh gets a unique dedup key and filename.
        return $"{assetPath}::{mesh.name}";
    }

    static string GetMeshSourceName(Mesh mesh)
    {
        string assetPath = AssetDatabase.GetAssetPath(mesh);
        string meshName = SanitizeName(mesh.name);
        if (!string.IsNullOrEmpty(assetPath) && !assetPath.StartsWith("Library/"))
        {
            string fbx = SanitizeName(System.IO.Path.GetFileNameWithoutExtension(assetPath));
            return fbx == meshName ? fbx : $"{fbx}_{meshName}";
        }
        return meshName;
    }

    public static void DebugMetallicMap()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/StylizedCharacterPack/Materials/Characters/SeaGull_Material.mat");
        if (mat == null) { Debug.LogError("[HZ] SeaGull material not found"); return; }

        Texture2D tex = null;
        if (mat.HasProperty("_MetallicGlossMap")) tex = mat.GetTexture("_MetallicGlossMap") as Texture2D;
        if (tex == null) { Debug.LogError("[HZ] No _MetallicGlossMap"); return; }

        Debug.Log($"[HZ] MetallicGlossMap: {tex.name}, {tex.width}x{tex.height}, format={tex.format}, readable={tex.isReadable}");

        // Blit to read pixels
        var tmp = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, tmp);
        RenderTexture.active = tmp;
        var read = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
        read.ReadPixels(new UnityEngine.Rect(0, 0, tex.width, tex.height), 0, 0);
        read.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(tmp);

        // Dump R, G, B, A channels as separate greyscale PNGs
        int w = tex.width, h = tex.height;
        var pixels = read.GetPixels();
        string outDir = System.IO.Path.GetFullPath("ExportedAssets/debug");
        System.IO.Directory.CreateDirectory(outDir);

        for (int ch = 0; ch < 4; ch++)
        {
            string chName = ch == 0 ? "R" : ch == 1 ? "G" : ch == 2 ? "B" : "A";
            var chTex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var chPixels = new Color[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                float v = ch == 0 ? pixels[i].r : ch == 1 ? pixels[i].g : ch == 2 ? pixels[i].b : pixels[i].a;
                chPixels[i] = new Color(v, v, v, 1f);
            }
            chTex.SetPixels(chPixels);
            chTex.Apply();
            byte[] png = chTex.EncodeToPNG();
            string path = $"{outDir}/metallic_gloss_{chName}.png";
            System.IO.File.WriteAllBytes(path, png);
            Debug.Log($"[HZ] Dumped channel {chName} → {path} ({png.Length} bytes)");
            Object.DestroyImmediate(chTex);
        }

        // Also log some pixel values
        int cx = w / 2, cy = h / 2;
        var pc = read.GetPixel(cx, cy);
        Debug.Log($"[HZ] center pixel({cx},{cy}): R={pc.r:F4} G={pc.g:F4} B={pc.b:F4} A={pc.a:F4}");
        var pb = read.GetPixel(100, 100);
        Debug.Log($"[HZ] pixel(100,100): R={pb.r:F4} G={pb.g:F4} B={pb.b:F4} A={pb.a:F4}");
        var pw = read.GetPixel(300, 1400);
        Debug.Log($"[HZ] pixel(300,1400): R={pw.r:F4} G={pw.g:F4} B={pw.b:F4} A={pw.a:F4}");

        Object.DestroyImmediate(read);
    }

    /// <summary>
    /// Export Big@Idle.fbx animation using the old .hza exporter + dump reference values as text.
    /// </summary>
    public static void ExportBigIdleReference()
    {
        // Load the SeaGull model to get the skeleton (it uses the "Big" rig)
        string modelFbx = "Assets/StylizedCharacterPack/Models/SeaGull.fbx";
        string animFbx = "Assets/StylizedCharacterPack/Animations/Big@Idle.fbx";

        var modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelFbx);
        var modelInstance = Object.Instantiate(modelPrefab);
        var smr = modelInstance.GetComponentInChildren<SkinnedMeshRenderer>();

        if (smr == null || smr.bones == null || smr.bones.Length == 0)
        {
            Debug.LogError("[HZ] No SMR found on SeaGull");
            Object.DestroyImmediate(modelInstance);
            return;
        }

        // Build bone name → index from the model's skeleton
        var boneNameToIndex = new Dictionary<string, int>();
        for (int i = 0; i < smr.bones.Length; i++)
            if (smr.bones[i] != null)
                boneNameToIndex[smr.bones[i].name] = i;

        int jointCount = smr.bones.Length;
        Object.DestroyImmediate(modelInstance);

        // Load animation clip from Big@Idle.fbx
        var assets = AssetDatabase.LoadAllAssetsAtPath(animFbx);
        AnimationClip clip = null;
        foreach (var asset in assets)
        {
            if (asset is AnimationClip ac && !ac.name.StartsWith("__preview__"))
            {
                clip = ac;
                break;
            }
        }

        if (clip == null)
        {
            Debug.LogError($"[HZ] No AnimationClip found in {animFbx}");
            return;
        }

        Debug.Log($"[HZ] Big@Idle clip: name={clip.name}, length={clip.length}s, " +
                  $"skeleton has {jointCount} joints, matched bones={boneNameToIndex.Count}");

        // Export .hza using old exporter
        var animPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(animFbx);
        var animInstance = Object.Instantiate(animPrefab);

        var animBoneMap = new Dictionary<string, int>();
        var animSmr = animInstance.GetComponentInChildren<SkinnedMeshRenderer>();
        if (animSmr != null)
        {
            foreach (var bone in animSmr.bones)
                if (bone != null && boneNameToIndex.TryGetValue(bone.name, out int idx))
                    animBoneMap[bone.name] = idx;
        }
        // Fallback: match by transform names in hierarchy
        if (animBoneMap.Count == 0)
        {
            foreach (var t in animInstance.GetComponentsInChildren<Transform>())
                if (boneNameToIndex.TryGetValue(t.name, out int idx) && !animBoneMap.ContainsKey(t.name))
                    animBoneMap[t.name] = idx;
        }

        Debug.Log($"[HZ] Matched {animBoneMap.Count}/{jointCount} bones for Big@Idle");

        string hzaPath = $"{OutputDir}/animations/Big_Idle_ref.hza";
        HZAnimationExporter.Export(clip, animInstance, animBoneMap, jointCount, hzaPath);

        // Also dump reference text with sampled values
        float sampleRate = 30f;
        float duration = clip.length;
        int keyframeCount = Mathf.Max(1, Mathf.RoundToInt(duration * sampleRate) + 1);

        var transformByName = new Dictionary<string, Transform>();
        foreach (var t in animInstance.GetComponentsInChildren<Transform>())
            if (!transformByName.ContainsKey(t.name))
                transformByName[t.name] = t;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Big@Idle reference animation");
        sb.AppendLine($"# clip={clip.name} duration={duration} keyframes={keyframeCount} joints={jointCount} matched={animBoneMap.Count}");
        sb.AppendLine($"# format: keyframe time | joint_index bone_name | tx ty tz | rx ry rz rw | sx sy sz");
        sb.AppendLine();

        // Sample first 3 keyframes and last keyframe
        int[] sampleFrames = keyframeCount <= 4
            ? new int[keyframeCount]
            : new int[] { 0, 1, 2, keyframeCount - 1 };
        if (keyframeCount <= 4)
            for (int i = 0; i < keyframeCount; i++) sampleFrames[i] = i;

        foreach (int ki in sampleFrames)
        {
            float time = (ki < keyframeCount - 1) ? ki / sampleRate : duration;
            clip.SampleAnimation(animInstance, time);

            sb.AppendLine($"keyframe {ki} time={time:F6}");
            foreach (var kvp in animBoneMap)
            {
                if (!transformByName.TryGetValue(kvp.Key, out Transform bt)) continue;
                var pos = bt.localPosition;
                var rot = bt.localRotation;
                var scl = bt.localScale;

                // Show both raw Unity values and converted values
                sb.AppendLine($"  joint[{kvp.Value}] {kvp.Key}");
                sb.AppendLine($"    unity:     pos=({pos.x:F6}, {pos.y:F6}, {pos.z:F6}) rot=({rot.x:F6}, {rot.y:F6}, {rot.z:F6}, {rot.w:F6}) scl=({scl.x:F6}, {scl.y:F6}, {scl.z:F6})");
                sb.AppendLine($"    converted: pos=({-pos.x:F6}, {pos.y:F6}, {pos.z:F6}) rot=({rot.x:F6}, {-rot.y:F6}, {-rot.z:F6}, {rot.w:F6}) scl=({scl.x:F6}, {scl.y:F6}, {scl.z:F6})");
            }
            sb.AppendLine();
        }

        string refPath = $"{OutputDir}/animations/Big_Idle_ref.txt";
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(refPath));
        System.IO.File.WriteAllText(refPath, sb.ToString());
        Debug.Log($"[HZ] Reference dump → {refPath}");

        Object.DestroyImmediate(animInstance);
    }

    /// <summary>
    /// Apply linear→sRGB so the engine's sRGB→linear gives back Unity's value.
    /// </summary>
    static float LinearToSrgb(float x)
    {
        if (x <= 0.0031308f) return x * 12.92f;
        return 1.055f * Mathf.Pow(x, 1f / 2.4f) - 0.055f;
    }

    static Vector3 LinearToSrgb(Vector3 v)
    {
        return new Vector3(LinearToSrgb(v.x), LinearToSrgb(v.y), LinearToSrgb(v.z));
    }

    static Color LinearToSrgb(Color c)
    {
        return new Color(LinearToSrgb(c.r), LinearToSrgb(c.g), LinearToSrgb(c.b), c.a);
    }

    static string SanitizeName(string name)
    {
        // Replace whitespace, path separators, and Windows-reserved filename characters.
        // Unity's FBX importer emits clip names like "Armature|Fly_03" — the | crashes
        // file creation on Windows.
        if (string.IsNullOrEmpty(name)) return name;
        return name
            .Replace(' ', '_').Replace('/', '_').Replace('\\', '_')
            .Replace('|', '_').Replace(':', '_').Replace('?', '_').Replace('*', '_')
            .Replace('"', '_').Replace('<', '_').Replace('>', '_');
    }

    /// <summary>
    /// Walks up from `t` and returns the first Animator found (including on `t` itself).
    /// Used to determine which character a SMR / bone belongs to when an Animator is nested
    /// inside another Animator's hierarchy.
    /// </summary>
    static Animator NearestAnimator(Transform t)
    {
        while (t != null)
        {
            var a = t.GetComponent<Animator>();
            if (a != null) return a;
            t = t.parent;
        }
        return null;
    }

    /// <summary>
    /// Walks descendants of `cur` and adds them to `skipGOs` as bones of `ownerAnim`.
    /// Stops at any deeper Animator boundary — that subtree belongs to a nested character
    /// and is processed by its own pass. The owner's GO itself is NOT added (caller responsibility).
    /// </summary>
    static void AddBoneSkips(Transform cur, GameObject ownerAnim, HashSet<EntityId> skipGOs)
    {
        for (int i = 0; i < cur.childCount; i++)
        {
            var child = cur.GetChild(i);
            if (child.GetComponent<Animator>() != null) continue; // nested character — its own pass
            skipGOs.Add(child.gameObject.GetEntityId());
            AddBoneSkips(child, ownerAnim, skipGOs);
        }
    }

    /// <summary>
    /// Diagnostic: dump every SkinnedMeshRenderer in the active scene with its bones[] array.
    /// Flags null entries, identifies probable MagicaCloth2 proxy SMRs, and shows which one
    /// the current primarySmr picker would choose.
    /// </summary>
    /// <summary>
    /// Scan the active scene for static meshes with subMeshCount > 1 to confirm multi-primitive
    /// Case B is exercised. Logs each to Editor.log.
    /// </summary>
    public static void FindMultiSubmeshStaticMeshes()
    {
        var scene = SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        int count = 0;
        foreach (var root in roots)
        {
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                if (mesh.subMeshCount > 1 || mr.sharedMaterials.Length > 1)
                {
                    count++;
                    Debug.Log($"[MultiSub] {mr.gameObject.name}: submeshCount={mesh.subMeshCount} materialCount={mr.sharedMaterials.Length} meshAsset={AssetDatabase.GetAssetPath(mesh)}");
                }
            }
        }
        Debug.Log($"[MultiSub] total={count}");
    }

    public static void DumpSkinnedBones()
    {
        string outPath = System.IO.Path.Combine(OutputDir, "dump_bones.txt");
        var sw = new System.IO.StreamWriter(outPath, false);
        void WLog(string s) { Debug.Log(s); sw.WriteLine(s); }

        var scene = SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        WLog($"[DumpBones] Scene: {scene.name}, {roots.Length} roots");

        foreach (var root in roots)
        {
            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs.Length == 0) continue;

            WLog($"[DumpBones] === Root: {root.name} ({smrs.Length} SMRs) ===");

            // Simulate primary picker logic
            SkinnedMeshRenderer primary = null;
            foreach (var s in smrs)
            {
                if (s.sharedMesh == null || s.bones == null || s.bones.Length == 0) continue;
                if (primary == null || s.bones.Length > primary.bones.Length) primary = s;
            }

            foreach (var smr in smrs)
            {
                string path = GetTransformPath(smr.transform, root.transform);
                var bones = smr.bones;
                var mesh = smr.sharedMesh;

                // Detect MC2: walk up parents looking for a MagicaCloth component (by type name, avoids compile-time ref)
                string mc2Owner = null;
                for (var t = smr.transform; t != null; t = t.parent)
                {
                    var comps = t.GetComponents<Component>();
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        var tn = c.GetType().FullName;
                        if (tn != null && tn.Contains("MagicaCloth"))
                        {
                            mc2Owner = $"{t.name}:{c.GetType().Name}";
                            break;
                        }
                    }
                    if (mc2Owner != null) break;
                }

                int nullCount = 0;
                if (bones != null)
                    foreach (var b in bones) if (b == null) nullCount++;

                string primaryMark = (smr == primary) ? "  <-- primarySmr" : "";
                WLog($"[DumpBones] SMR '{smr.name}' path='{path}' " +
                     $"mesh='{(mesh != null ? mesh.name : "<null>")}' " +
                     $"bones={bones?.Length ?? 0} nulls={nullCount} " +
                     $"rootBone='{(smr.rootBone != null ? smr.rootBone.name : "<null>")}' " +
                     $"mc2={mc2Owner ?? "no"}{primaryMark}");

                if (nullCount > 0 && bones != null)
                {
                    for (int i = 0; i < bones.Length; i++)
                        if (bones[i] == null)
                            WLog($"[DumpBones]   null bone at index {i}");
                }

                if (bones != null && bones.Length > 0)
                {
                    // Dump EVERY bone entry so we can see exactly what's there
                    for (int i = 0; i < bones.Length; i++)
                    {
                        var b = bones[i];
                        string bname = b != null ? b.name : "<NULL>";
                        string bpath = b != null ? GetTransformPath(b, root.transform) : "<NULL>";
                        WLog($"[DumpBones]   bone[{i}]={bname} path={bpath}");
                    }
                }
            }
        }

        WLog("[DumpBones] done");
        sw.Flush();
        sw.Close();
        Debug.Log($"[DumpBones] wrote {outPath}");
    }

    static string GetTransformPath(Transform t, Transform root)
    {
        if (t == root) return t.name;
        var sb = new System.Text.StringBuilder(t.name);
        var cur = t.parent;
        while (cur != null && cur != root)
        {
            sb.Insert(0, cur.name + "/");
            cur = cur.parent;
        }
        if (cur == root) sb.Insert(0, root.name + "/");
        return sb.ToString();
    }

    /// <summary>
    /// Mirror the exporter's primary-SMR picker + per-SMR boneRemap, then count how many
    /// vertex weights would fall into the "remap=0 fallback" slot due to null/missing bones.
    /// That's the set of vertices that get skinned to the root and produce visual explosions.
    /// </summary>
    public static void AnalyzeSkinWeights()
    {
        string outPath = System.IO.Path.Combine(OutputDir, "skin_weight_analysis.txt");
        var sw = new System.IO.StreamWriter(outPath, false);
        void WLog(string s) { Debug.Log(s); sw.WriteLine(s); }

        var scene = SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();

        foreach (var root in roots)
        {
            var allSmrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var validSmrs = new List<SkinnedMeshRenderer>();
            foreach (var s in allSmrs)
                if (s.sharedMesh != null && s.bones != null && s.bones.Length > 0)
                    validSmrs.Add(s);
            if (validSmrs.Count == 0) continue;

            SkinnedMeshRenderer primary = validSmrs[0];
            foreach (var s in validSmrs)
                if (s.bones.Length > primary.bones.Length) primary = s;

            var primaryBones = primary.bones;
            var boneToIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < primaryBones.Length; i++)
                if (primaryBones[i] != null) boneToIndex[primaryBones[i]] = i;

            WLog($"[Analyze] === Root '{root.name}' — primary SMR '{primary.name}' bones={primaryBones.Length} ===");

            int globalBadVerts = 0;
            int globalBadWeight = 0;

            foreach (var smr in validSmrs)
            {
                var bones = smr.bones;
                int[] remap = new int[bones.Length];
                bool[] isFallback = new bool[bones.Length];
                int nullInSmr = 0, missingFromPrimary = 0;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null)
                    {
                        remap[i] = 0; isFallback[i] = true; nullInSmr++;
                    }
                    else if (!boneToIndex.TryGetValue(bones[i], out remap[i]))
                    {
                        remap[i] = 0; isFallback[i] = true; missingFromPrimary++;
                    }
                }

                var mesh = smr.sharedMesh;
                var bws = mesh.boneWeights;
                int badVerts = 0;
                float totalBadWeight = 0f;
                var badBoneHist = new Dictionary<int, int>(); // bone index in smr.bones → count
                for (int v = 0; v < bws.Length; v++)
                {
                    var bw = bws[v];
                    bool hit = false;
                    void Check(int bi, float w)
                    {
                        if (bi >= 0 && bi < bones.Length && isFallback[bi] && w > 0f)
                        {
                            hit = true;
                            totalBadWeight += w;
                            badBoneHist.TryGetValue(bi, out int c);
                            badBoneHist[bi] = c + 1;
                        }
                    }
                    Check(bw.boneIndex0, bw.weight0);
                    Check(bw.boneIndex1, bw.weight1);
                    Check(bw.boneIndex2, bw.weight2);
                    Check(bw.boneIndex3, bw.weight3);
                    if (hit) badVerts++;
                }

                string tag = (smr == primary) ? " <PRIMARY>" : "";
                WLog($"[Analyze] SMR '{smr.name}'{tag} bones={bones.Length} " +
                     $"null_in_smr={nullInSmr} missing_from_primary={missingFromPrimary} " +
                     $"bad_verts={badVerts}/{bws.Length} totalBadWeight={totalBadWeight:F2}");

                if (badBoneHist.Count > 0)
                {
                    // Top 10 offending slots
                    var list = new List<KeyValuePair<int, int>>(badBoneHist);
                    list.Sort((a, b) => b.Value.CompareTo(a.Value));
                    int show = System.Math.Min(10, list.Count);
                    for (int i = 0; i < show; i++)
                    {
                        int bi = list[i].Key;
                        int cnt = list[i].Value;
                        string bn = bones[bi] != null ? bones[bi].name : "<NULL>";
                        bool inPrimary = bones[bi] != null && boneToIndex.ContainsKey(bones[bi]);
                        WLog($"[Analyze]   slot[{bi}]={bn} hits={cnt} inPrimary={inPrimary}");
                    }
                }

                globalBadVerts += badVerts;
                globalBadWeight += (int)totalBadWeight;
            }

            WLog($"[Analyze] === TOTAL bad_verts={globalBadVerts} totalBadWeight={globalBadWeight} ===");

            // Compare bindposes across SMRs: if two SMRs use the same bone but have different
            // bindpose matrices for it, a single shared inverseBindMatrices list in glTF is wrong.
            var primaryBP = primary.sharedMesh.bindposes;
            int bindposeMismatches = 0;
            foreach (var smr in validSmrs)
            {
                if (smr == primary) continue;
                var bps = smr.sharedMesh.bindposes;
                int mismatchCount = 0;
                float maxDelta = 0f;
                int firstMismatch = -1;
                int compareCount = System.Math.Min(bps.Length, primaryBP.Length);
                for (int i = 0; i < compareCount; i++)
                {
                    if (smr.bones[i] == null || primaryBones[i] == null) continue;
                    if (smr.bones[i] != primaryBones[i]) continue; // only compare when same bone
                    float d = 0f;
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 4; c++)
                            d += System.Math.Abs(bps[i][r, c] - primaryBP[i][r, c]);
                    if (d > 1e-4f)
                    {
                        mismatchCount++;
                        if (firstMismatch < 0) firstMismatch = i;
                        if (d > maxDelta) maxDelta = d;
                    }
                }
                if (mismatchCount > 0)
                {
                    bindposeMismatches++;
                    WLog($"[Analyze] BINDPOSE MISMATCH: SMR '{smr.name}' differs from primary on {mismatchCount}/{compareCount} bones (firstIdx={firstMismatch} maxDelta={maxDelta:F4})");
                }
                else
                {
                    WLog($"[Analyze] SMR '{smr.name}' bindposes match primary");
                }
            }
            WLog($"[Analyze] === {bindposeMismatches}/{validSmrs.Count - 1} SMRs have bindpose mismatches ===");

            // Dump preXform translation + bindpose-based delta for every SMR — look for large values
            WLog($"[Analyze] preXform per SMR (primary.worldToLocal * smr.localToWorld) vs bp[0]-based delta:");
            foreach (var smr in validSmrs)
            {
                Matrix4x4 pre = primary.transform.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                Vector3 t = (Vector3)pre.GetColumn(3);
                string bpDelta = "n/a";
                if (primary.sharedMesh.bindposes.Length > 0 && smr.sharedMesh.bindposes.Length > 0)
                {
                    var pbp = primary.sharedMesh.bindposes[0];
                    var mbp = smr.sharedMesh.bindposes[0];
                    var deltaMat = pbp.inverse * mbp;  // the correct preXform per math
                    Vector3 dt = (Vector3)deltaMat.GetColumn(3);
                    bpDelta = $"({dt.x:F3},{dt.y:F3},{dt.z:F3})";
                }
                WLog($"[Analyze]   {smr.name}: preXform_t=({t.x:F3},{t.y:F3},{t.z:F3})  bp-based={bpDelta}  pos={smr.transform.position}");
            }

            // Print primary & mismatching SMR transforms and sample bindpose matrices
            void DumpTransform(SkinnedMeshRenderer s)
            {
                WLog($"[Analyze]   transform.position={s.transform.position} rotation={s.transform.rotation.eulerAngles} lossyScale={s.transform.lossyScale}");
                WLog($"[Analyze]   localToWorld=\n{s.transform.localToWorldMatrix}");
                var bp = s.sharedMesh.bindposes;
                if (bp.Length > 0) WLog($"[Analyze]   bindpose[0]=\n{bp[0]}");
                if (bp.Length > 38) WLog($"[Analyze]   bindpose[38]=\n{bp[38]}");
            }
            WLog($"[Analyze] PRIMARY '{primary.name}':");
            DumpTransform(primary);
            foreach (var smr in validSmrs)
            {
                if (smr == primary) continue;
                if (smr.name != "head_Def" && smr.name != "Headgear") continue;
                WLog($"[Analyze] MISMATCH '{smr.name}':");
                DumpTransform(smr);
                // Computed preXform
                Matrix4x4 pre = primary.transform.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                WLog($"[Analyze]   preXform (primary.worldToLocal * smr.localToWorld)=\n{pre}");
                // Expected: primary_bp[i] should equal preXform * smr_bp[i] (?)
                // Actually: delta between bindposes if they include SMR xform
                if (primary.sharedMesh.bindposes.Length > 0 && smr.sharedMesh.bindposes.Length > 0)
                {
                    var pbp = primary.sharedMesh.bindposes[38];
                    var mbp = smr.sharedMesh.bindposes[38];
                    // primary_bp = bone.worldToLocal * primary.localToWorld
                    // mesh_bp    = bone.worldToLocal * mesh.localToWorld
                    // primary_bp * mesh_bp^-1 = primary.localToWorld * mesh.worldToLocal
                    var delta = pbp * mbp.inverse;
                    WLog($"[Analyze]   bindpose_delta[38] (primary_bp * mesh_bp.inv)=\n{delta}");
                    var expected = primary.transform.localToWorldMatrix * smr.transform.worldToLocalMatrix;
                    WLog($"[Analyze]   expected (primary.localToWorld * smr.worldToLocal)=\n{expected}");
                }
            }
        }

        sw.Flush(); sw.Close();
        Debug.Log($"[Analyze] wrote {outPath}");
    }
}
