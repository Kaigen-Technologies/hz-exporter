using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity scene as a SceneBlobAsset manifest + one EntityChunkBlobAsset
    /// file holding every entity inline. The engine scene system is chunk-based; we
    /// emit a single chunk for now (chunk_0.hzt).
    /// </summary>
    public static class HZSceneExporter
    {
        // Struct sizes (must match engine)
        const int SIZEOF_SCENE_BLOB_ASSET = 72;       // header(48) + scene_dir StringBlob(8) + entities BlobPtr(16)
        const int SIZEOF_ENTITY_BLOB_ASSET = 72;      // header(48) + stable_id(8) + components BlobPtr(16)
        const int SIZEOF_SCENE_COMPONENT_BLOB = 32;   // stable_type_hash(8) + data_size(4) + data BlobArray(16) + 4 pad (u64 alignment)
        const int SIZEOF_BLOB_ASSET_REF = 24;
        const int SIZEOF_STRING_BLOB = 8;

        // Component stable_type_hash = fnv1a(name) as u64
        static readonly ulong HASH_SceneEntityTag = HZBlobWriter.FNV1a("SceneEntityTag");
        static readonly ulong HASH_EcsStableId = HZBlobWriter.FNV1a("EcsStableId");
        static readonly ulong HASH_EntityName = HZBlobWriter.FNV1a("EntityName");
        static readonly ulong HASH_LocalTransform = HZBlobWriter.FNV1a("LocalTransform");
        static readonly ulong HASH_EcsVisibility = HZBlobWriter.FNV1a("EcsVisibility");
        static readonly ulong HASH_EcsModelRef = HZBlobWriter.FNV1a("EcsModelRef");
        static readonly ulong HASH_EcsMaterialRef = HZBlobWriter.FNV1a("EcsMaterialRef");
        static readonly ulong HASH_EcsAABB = HZBlobWriter.FNV1a("EcsAABB");
        static readonly ulong HASH_Camera = HZBlobWriter.FNV1a("Camera");
        static readonly ulong HASH_EcsSceneSettings = HZBlobWriter.FNV1a("EcsSceneSettings");
        static readonly ulong HASH_EcsAnimationRef = HZBlobWriter.FNV1a("EcsAnimationRef");
        static readonly ulong HASH_EcsParent = HZBlobWriter.FNV1a("EcsParent");
        static readonly ulong HASH_EcsDirectionalLight = HZBlobWriter.FNV1a("EcsDirectionalLight");
        static readonly ulong HASH_EcsPointLight = HZBlobWriter.FNV1a("EcsPointLight");
        static readonly ulong HASH_EcsTerrainRef = HZBlobWriter.FNV1a("EcsTerrainRef");

        // Component data sizes (must match engine struct sizeof)
        const int SIZE_SceneEntityTag = 1;
        const int SIZE_EcsStableId = 8;
        const int SIZE_EntityName = 68;     // u32 len + char[64]
        const int SIZE_LocalTransform = 40;  // v3 position(12) + quat rotation(16) + v3 scale(12)
        const int SIZE_EcsVisibility = 8;   // 2x b32
        const int SIZE_EcsModelRef = 24;    // BlobAssetRef(ModelBlobAsset)
        const int SIZE_EcsMaterialRef = 24; // BlobAssetRef(MaterialBlobAsset)
        const int SIZE_EcsAABB = 24;        // 2x vec3
        // Camera: 4x mat4(256) + Frustum(96) + quat(16) + vec3(12) + 4x f32(16) = 396
        const int SIZE_Camera = 396;
        const int SIZE_EcsSceneSettings = 40;    // ColorF32(16) + f32(4) + ColorF32(16) + f32 max_shadow_distance(4)
        const int SIZE_EcsDirectionalLight = 32; // v3 color(12) + f32 intensity(4) + b32 cast_shadow(4) + 3*f32 shadow_strength/bias/normal_bias(12)
        const int SIZE_EcsPointLight = 20;       // v3 color(12) + f32 radius(4) + f32 intensity(4)
        const int SIZE_EcsAnimationRef = 24;     // BlobAssetRef(AnimationBlobAsset)
        const int SIZE_EcsParent = 8;            // EcsEntity (u64)
        const int SIZEOF_ANIMATION_BLOB_ASSET = 72;
        const int SIZE_EcsTerrainRef = 32;   // BlobAssetRef(24) + u32 grid_dim + u32 lod_levels

        // sizeof(ModelBlobAsset) for BlobAssetRef typehash
        const int SIZEOF_MODEL_BLOB_ASSET = 160;
        const int SIZEOF_MATERIAL_BLOB_ASSET = 112;
        const int SIZEOF_ENTITY_BLOB_ASSET_REF = 72; // sizeof(EntityBlobAsset)
        const int SIZEOF_TERRAIN_BLOB_ASSET = 128;

        /// <summary>Engine EcsPostProcessing values (color grading / tonemap / vignette). Caller
        /// fills from a URP post-processing Volume; Default() is the engine identity.</summary>
        public struct PostProcessingSettings
        {
            public float exposure, contrast, brightness, saturation, gamma;
            public uint tonemapMode;
            public float postExposure, hueShift;
            public Color colorFilter;
            public float whiteBalanceTemperature, whiteBalanceTint;
            public Color vignetteColor;
            public Vector2 vignetteCenter;
            public float vignetteIntensity, vignetteSmoothness;
            public bool vignetteRounded;
            // Bloom (engine requires bloom_enabled; bloom_intensity 0 disables).
            public float bloomIntensity, bloomThreshold, bloomScatter, bloomClamp;
            public Color bloomTint;

            public static PostProcessingSettings Default() => new PostProcessingSettings
            {
                exposure = 1f,
                colorFilter = Color.white,
                vignetteColor = new Color(0f, 0f, 0f, 1f),
                vignetteCenter = new Vector2(0.5f, 0.5f),
                // Unity's Vignette.smoothness default (the engine multiplies ×5 → pow exponent 1.0).
                // Must NOT be 0, which would make pow(x,0)=1 and erase the vignette entirely.
                vignetteSmoothness = 0.2f,
                // URP Bloom defaults (intensity 0 = off). Disabled overrides fall back to these.
                bloomThreshold = 0.9f,
                bloomScatter = 0.7f,
                bloomClamp = 65472f,
                bloomTint = Color.white,
            };
        }

        /// <summary>
        /// Info about one entity to export.
        /// </summary>
        public struct EntityExportInfo
        {
            public string name;
            public Vector3 position;      // local
            public Vector3 eulerAngles;
            public Vector3 scale;         // local
            public Vector3 worldPosition;
            public Quaternion worldRotation;
            public Vector3 worldScale;
            public bool visible;
            public bool castsShadow;
            public Bounds bounds;
            public string modelAssetPath;    // asset-relative path to model .hza
            public string materialAssetPath; // asset-relative path to material .hza
            public bool skinned;
            // Camera (null if not a camera entity)
            public bool isCamera;
            public float fov;
            public float nearPlane;
            public float farPlane;
            public Quaternion rotation; // Unity quaternion
            // Scene settings
            // Directional light
            public bool isDirectionalLight;
            public Vector3 lightDirection;
            public Vector3 lightColor;
            public float lightIntensity;
            public bool castShadow;
            public float shadowStrength;
            public float shadowBias;
            public float shadowNormalBias;
            // Hierarchy
            public ulong parentStableId; // 0 = root (no parent)
            public ulong stableId;       // assigned by caller
            // Bone attachment: when true, position/rotation/scale above are interpreted
            // bone-local (relative to the bone Transform) instead of root-local. The engine
            // resolves boneAttachmentName -> bone_index against the parent's joint_names and
            // composes parent.world × bone_global × LocalTransform at render time.
            public bool isBoneAttached;
            public string boneAttachmentName;
            // Animation
            public string animationAssetPath; // asset-relative path to animation .hza
            // Point light
            public bool isPointLight;
            public float pointLightRadius;
            // Scene settings
            public bool isSceneSettings;
            public Vector3 ambientColor;
            public float ambientIntensity;
            public Color shadowColor;
            // Global max shadow render distance (from the active render pipeline asset).
            public float maxShadowDistance;
            // Post-processing (only meaningful when isSceneSettings == true).
            public PostProcessingSettings postProcessing;
            // Skybox / IBL env (only meaningful when isSceneSettings == true)
            public bool hasEnv;
            public string envAssetPath;   // e.g. "unity/environments/SceneA_skybox.hza"
            public float envIntensity;
            public bool skybox;           // true only if the main camera clears to Skybox
            public Color clearColor;      // main camera background color (used when skybox == false)
            // Fog (only meaningful when isSceneSettings == true)
            public uint fogMode;          // 0=off, 1=linear, 2=exp, 3=exp2 (matches engine RendererFogMode)
            public Vector3 fogColor;      // sRGB picker value; engine srgb-linears it
            public float fogDensity;
            public float fogStart;
            public float fogEnd;
            // Terrain
            public string terrainAssetPath; // asset-relative path to terrain .hzt/.hza (TerrainBlobAsset)
            public uint terrainGridDim;
            public uint terrainLodLevels;
            // Extra LODs (LOD1+). Each entry: (model asset path, switch_coverage in
            // screen-height-fraction units). Strictly descending switch_coverage.
            // Emitted as EcsModelRef.extra_lods.
            public System.Collections.Generic.List<(string modelAssetPath, float switchCoverage)> extraLods;
            // Custom component blocks emitted verbatim after the standard components.
            // Each entry is the full text of one `component <Name>` block (the engine
            // must have a matching reflected struct, else the cooker silently drops it).
            public List<string> customComponents;
        }

        /// <summary>
        /// Export a scene: SceneBlobAsset manifest + one EntityChunkBlobAsset (chunk_0.hzt).
        /// </summary>
        public static void Export(EntityExportInfo[] entities, string scenePath,
            string sceneDir, string sceneDirAssetPath)
        {
            // Scene dir is cleaned once up front by the caller (ExportScene) so
            // terrain assets written during entity collection survive. Just ensure
            // it exists.
            Directory.CreateDirectory(sceneDir);

            string chunkFsPath = Path.Combine(sceneDir, "chunk_0.hzt");
            string chunkAssetPath = $"{sceneDirAssetPath}/chunk_0.hzt";

            WriteChunkHzt(entities, chunkFsPath);

            // Chunk bounds: point-AABB over entity world positions (export space — X negated).
            // Streaming hint only; single-chunk loads ignore it.
            Vector3 bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 bmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var e in entities)
            {
                Vector3 p = new Vector3(-e.worldPosition.x, e.worldPosition.y, e.worldPosition.z);
                bmin = Vector3.Min(bmin, p);
                bmax = Vector3.Max(bmax, p);
            }
            if (entities.Length == 0) { bmin = Vector3.zero; bmax = Vector3.zero; }

            WriteManifestHzt(sceneDirAssetPath, chunkAssetPath, bmin, bmax, scenePath);

            Debug.Log($"[HZSceneExporter] Exported scene → {scenePath} (1 chunk, {entities.Length} entities)");
        }

        // ============= .hzt text format writers =============

        static string F(float v) => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

        static void WriteManifestHzt(string sceneDirAssetPath, string chunkAssetPath,
            Vector3 boundsMin, Vector3 boundsMax, string outputPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("type SceneBlobAsset");
            sb.AppendLine("version 3");
            sb.AppendLine("---");
            sb.AppendLine($"scene_dir \"{sceneDirAssetPath}\"");
            sb.AppendLine("chunks 1");
            sb.AppendLine($"  chunk \"{chunkAssetPath}\" EntityChunkBlobAsset");
            sb.AppendLine($"  bounds_min {F(boundsMin.x)} {F(boundsMin.y)} {F(boundsMin.z)}");
            sb.AppendLine($"  bounds_max {F(boundsMax.x)} {F(boundsMax.y)} {F(boundsMax.z)}");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, sb.ToString());
        }

        // One EntityChunkBlobAsset file: header + every entity inline, blank line between.
        static void WriteChunkHzt(EntityExportInfo[] entities, string outputPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("type EntityChunkBlobAsset");
            sb.AppendLine("version 3");
            sb.AppendLine("---");

            for (int i = 0; i < entities.Length; i++)
            {
                if (i > 0) sb.AppendLine();
                AppendEntityBody(sb, entities[i]);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, sb.ToString());
        }

        // Appends one `entity <stable_id>` block (followed by its components) to sb.
        static void AppendEntityBody(StringBuilder sb, EntityExportInfo info)
        {
            bool hasModel = !string.IsNullOrEmpty(info.modelAssetPath);
            bool hasMaterial = !string.IsNullOrEmpty(info.materialAssetPath);
            bool hasAnimation = !string.IsNullOrEmpty(info.animationAssetPath);
            bool hasParent = info.parentStableId != 0;

            sb.AppendLine($"entity {info.stableId}");

            // SceneEntityTag
            sb.AppendLine();
            sb.AppendLine("component SceneEntityTag");
            sb.AppendLine("  _ 0");

            // EcsStableId
            sb.AppendLine();
            sb.AppendLine("component EcsStableId");
            sb.AppendLine($"  id {info.stableId}");

            // EntityName — uses inline String64Bytes (FIELD_TYPE_FIXED_STRING in the engine).
            // Truncate at 63 chars to leave room for the null terminator the engine writes.
            {
                string n = info.name ?? "";
                if (n.Length > 63) n = n.Substring(0, 63);
                sb.AppendLine();
                sb.AppendLine("component EntityName");
                sb.AppendLine($"  name \"{n}\"");
            }

            // Transforms — skipped for SceneSettings, which is a pure config singleton with
            // no spatial meaning (no engine system queries its transform).
            if (!info.isSceneSettings)
            {
                // LocalTransform
                {
                    // Position: negate X
                    float px = -info.position.x, py = info.position.y, pz = info.position.z;
                    // Rotation: X-mirror (qx, -qy, -qz, qw)
                    var q = info.rotation;
                    float qx = q.x, qy = -q.y, qz = -q.z, qw = q.w;

                    sb.AppendLine();
                    sb.AppendLine("component LocalTransform");
                    sb.AppendLine("  position");
                    sb.AppendLine($"    x {F(px)}");
                    sb.AppendLine($"    y {F(py)}");
                    sb.AppendLine($"    z {F(pz)}");
                    sb.AppendLine("  rotation");
                    sb.AppendLine($"    x {F(qx)}");
                    sb.AppendLine($"    y {F(qy)}");
                    sb.AppendLine($"    z {F(qz)}");
                    sb.AppendLine($"    w {F(qw)}");
                    sb.AppendLine("  scale");
                    sb.AppendLine($"    x {F(info.scale.x)}");
                    sb.AppendLine($"    y {F(info.scale.y)}");
                    sb.AppendLine($"    z {F(info.scale.z)}");
                }

                // WorldTransform (actual world-space values)
                {
                    float px = -info.worldPosition.x, py = info.worldPosition.y, pz = info.worldPosition.z;
                    var q = info.worldRotation;
                    float qx = q.x, qy = -q.y, qz = -q.z, qw = q.w;

                    sb.AppendLine();
                    sb.AppendLine("component WorldTransform");
                    sb.AppendLine("  position");
                    sb.AppendLine($"    x {F(px)}");
                    sb.AppendLine($"    y {F(py)}");
                    sb.AppendLine($"    z {F(pz)}");
                    sb.AppendLine("  rotation");
                    sb.AppendLine($"    x {F(qx)}");
                    sb.AppendLine($"    y {F(qy)}");
                    sb.AppendLine($"    z {F(qz)}");
                    sb.AppendLine($"    w {F(qw)}");
                    sb.AppendLine("  scale");
                    sb.AppendLine($"    x {F(info.worldScale.x)}");
                    sb.AppendLine($"    y {F(info.worldScale.y)}");
                    sb.AppendLine($"    z {F(info.worldScale.z)}");
                }
            }

            // EcsVisibility
            sb.AppendLine();
            sb.AppendLine("component EcsVisibility");
            sb.AppendLine($"  visible {(info.visible ? "true" : "false")}");
            sb.AppendLine($"  casts_shadow {(info.castsShadow ? "true" : "false")}");

            // EcsParent
            if (hasParent)
            {
                sb.AppendLine();
                sb.AppendLine("component EcsParent");
                sb.AppendLine($"  parent {info.parentStableId}");
            }

            // EcsBoneAttachmentSpec (resolved at runtime to EcsBoneAttachment{bone_index} via
            // the parent's ModelBlobAsset.joint_names). When this is present, LocalTransform
            // above is interpreted bone-local rather than parent-relative.
            if (info.isBoneAttached && !string.IsNullOrEmpty(info.boneAttachmentName))
            {
                sb.AppendLine();
                sb.AppendLine("component EcsBoneAttachmentSpec");
                sb.AppendLine($"  bone_name \"{info.boneAttachmentName}\"");
            }

            // EcsModelRef
            if (hasModel)
            {
                sb.AppendLine();
                sb.AppendLine("component EcsModelRef");
                sb.AppendLine($"  model \"{info.modelAssetPath}\" ModelBlobAsset");
                int lodCount = info.extraLods != null ? info.extraLods.Count : 0;
                if (lodCount > 0)
                {
                    sb.AppendLine($"  extra_lods {lodCount}");
                    foreach (var (lodPath, switchCov) in info.extraLods)
                    {
                        sb.AppendLine($"    model \"{lodPath}\" ModelBlobAsset");
                        sb.AppendLine($"    switch_coverage {F(switchCov)}");
                    }
                }
            }

            // EcsMaterialRef
            if (hasMaterial)
            {
                uint matTypeSize = (uint)SIZEOF_MATERIAL_BLOB_ASSET;
                uint matTypehash = (uint)HZBlobWriter.TypeHash("MaterialBlobAsset", SIZEOF_MATERIAL_BLOB_ASSET);
                sb.AppendLine();
                sb.AppendLine("component EcsMaterialRef");
                sb.AppendLine($"  material \"{info.materialAssetPath}\" {matTypeSize} {matTypehash}");
            }

            // EcsAnimationRef
            if (hasAnimation)
            {
                sb.AppendLine();
                sb.AppendLine("component EcsAnimationRef");
                sb.AppendLine($"  animation \"{info.animationAssetPath}\" AnimationBlobAsset");
            }

            // EcsTerrainRef
            bool hasTerrain = !string.IsNullOrEmpty(info.terrainAssetPath);
            if (hasTerrain)
            {
                sb.AppendLine();
                sb.AppendLine("component EcsTerrainRef");
                sb.AppendLine($"  terrain \"{info.terrainAssetPath}\" TerrainBlobAsset");
                sb.AppendLine($"  grid_dim {info.terrainGridDim}");
                sb.AppendLine($"  lod_levels {info.terrainLodLevels}");
            }

            // EcsAABB
            if (hasModel)
            {
                var b = info.bounds;
                sb.AppendLine();
                sb.AppendLine("component EcsAABB");
                sb.AppendLine("  value");
                sb.AppendLine($"    min {F(-b.max.x)} {F(b.min.y)} {F(b.min.z)}");
                sb.AppendLine($"    max {F(-b.min.x)} {F(b.max.y)} {F(b.max.z)}");
            }

            // Camera — pos/rot in the engine Camera component are world-space (the engine
            // builds view/view_proj from these directly, not via the entity-tree hierarchy).
            // Use worldPosition/worldRotation so cameras nested under non-identity parents
            // export correctly.
            if (info.isCamera)
            {
                var mirrored = new Quaternion(info.worldRotation.x, -info.worldRotation.y, -info.worldRotation.z, info.worldRotation.w);
                var flip180Y = Quaternion.Euler(0, 180, 0);
                var cq = mirrored * flip180Y;

                sb.AppendLine();
                sb.AppendLine("component Camera");
                sb.AppendLine("  view ");
                sb.AppendLine("  proj ");
                sb.AppendLine("  view_proj ");
                sb.AppendLine("  inv_view_proj ");
                sb.AppendLine("  frustum");
                sb.AppendLine("    planes ");
                sb.AppendLine("  rot");
                sb.AppendLine($"    x {F(cq.x)}");
                sb.AppendLine($"    y {F(cq.y)}");
                sb.AppendLine($"    z {F(cq.z)}");
                sb.AppendLine($"    w {F(cq.w)}");
                sb.AppendLine($"  pos {F(-info.worldPosition.x)} {F(info.worldPosition.y)} {F(info.worldPosition.z)}");
                sb.AppendLine($"  fov {F(info.fov)}");
                sb.AppendLine($"  near_plane {F(info.nearPlane)}");
                sb.AppendLine($"  far_plane {F(info.farPlane)}");
                sb.AppendLine("  aspect 0.000000");
            }

            // EcsDirectionalLight
            if (info.isDirectionalLight)
            {
                sb.AppendLine();
                sb.AppendLine("component EcsDirectionalLight");
                sb.AppendLine("  color");
                sb.AppendLine($"    x {F(info.lightColor.x)}");
                sb.AppendLine($"    y {F(info.lightColor.y)}");
                sb.AppendLine($"    z {F(info.lightColor.z)}");
                sb.AppendLine($"  intensity {F(info.lightIntensity)}");
                sb.AppendLine($"  cast_shadow {(info.castShadow ? "true" : "false")}");
                sb.AppendLine($"  shadow_strength {F(info.shadowStrength)}");
                sb.AppendLine($"  shadow_bias {F(info.shadowBias)}");
                sb.AppendLine($"  shadow_normal_bias {F(info.shadowNormalBias)}");
            }

            // EcsPointLight
            if (info.isPointLight)
            {
                sb.AppendLine();
                sb.AppendLine("component EcsPointLight");
                sb.AppendLine("  color");
                sb.AppendLine($"    x {F(info.lightColor.x)}");
                sb.AppendLine($"    y {F(info.lightColor.y)}");
                sb.AppendLine($"    z {F(info.lightColor.z)}");
                sb.AppendLine($"  radius {F(info.pointLightRadius)}");
                sb.AppendLine($"  intensity {F(info.lightIntensity)}");
            }

            // EcsSceneSettings
            if (info.isSceneSettings)
            {
                sb.AppendLine();
                sb.AppendLine("component EcsSceneSettings");
                sb.AppendLine("  ambient_color");
                sb.AppendLine($"    r {F(info.ambientColor.x)}");
                sb.AppendLine($"    g {F(info.ambientColor.y)}");
                sb.AppendLine($"    b {F(info.ambientColor.z)}");
                sb.AppendLine($"    a 1.000000");
                sb.AppendLine($"  ambient_intensity {F(info.ambientIntensity)}");
                sb.AppendLine("  shadow_color");
                sb.AppendLine($"    r {F(info.shadowColor.r)}");
                sb.AppendLine($"    g {F(info.shadowColor.g)}");
                sb.AppendLine($"    b {F(info.shadowColor.b)}");
                sb.AppendLine($"    a {F(info.shadowColor.a)}");
                sb.AppendLine($"  max_shadow_distance {F(info.maxShadowDistance)}");
                if (info.hasEnv)
                {
                    sb.AppendLine($"  env \"{info.envAssetPath}\" HdrEnvironmentBlobAsset");
                    sb.AppendLine($"  env_intensity {F(info.envIntensity)}");
                }
                sb.AppendLine($"  skybox {(info.skybox ? "true" : "false")}");
                sb.AppendLine($"  fog_mode {info.fogMode}");
                sb.AppendLine("  fog_color");
                sb.AppendLine($"    r {F(info.fogColor.x)}");
                sb.AppendLine($"    g {F(info.fogColor.y)}");
                sb.AppendLine($"    b {F(info.fogColor.z)}");
                sb.AppendLine($"    a 1.000000");
                sb.AppendLine($"  fog_density {F(info.fogDensity)}");
                sb.AppendLine($"  fog_start {F(info.fogStart)}");
                sb.AppendLine($"  fog_end {F(info.fogEnd)}");
                // clear_color is the LAST field in the engine EcsSceneSettings struct;
                // the .hzt reader advances by struct offset, so it must be emitted last
                // (emitting it before fog_* makes the reader skip the fog fields → 0).
                sb.AppendLine("  clear_color");
                sb.AppendLine($"    r {F(info.clearColor.r)}");
                sb.AppendLine($"    g {F(info.clearColor.g)}");
                sb.AppendLine($"    b {F(info.clearColor.b)}");
                sb.AppendLine($"    a {F(info.clearColor.a)}");

                // EcsPostProcessing — singleton color-grading/tonemap/vignette config, emitted on
                // the scene-settings entity (same pattern as EcsSceneSettings). Values come from the
                // scene's URP post-processing Volume (only enabled overrides applied; the rest are
                // the engine identity).
                var pp = info.postProcessing;
                sb.AppendLine();
                sb.AppendLine("component EcsPostProcessing");
                sb.AppendLine($"  exposure {F(pp.exposure)}");
                sb.AppendLine($"  contrast {F(pp.contrast)}");
                sb.AppendLine($"  brightness {F(pp.brightness)}");
                sb.AppendLine($"  saturation {F(pp.saturation)}");
                sb.AppendLine($"  gamma {F(pp.gamma)}");
                sb.AppendLine($"  tonemap_mode {pp.tonemapMode}");
                sb.AppendLine($"  post_exposure {F(pp.postExposure)}");
                sb.AppendLine($"  hue_shift {F(pp.hueShift)}");
                sb.AppendLine("  color_filter");
                sb.AppendLine($"    r {F(pp.colorFilter.r)}");
                sb.AppendLine($"    g {F(pp.colorFilter.g)}");
                sb.AppendLine($"    b {F(pp.colorFilter.b)}");
                sb.AppendLine($"    a {F(pp.colorFilter.a)}");
                sb.AppendLine($"  white_balance_temperature {F(pp.whiteBalanceTemperature)}");
                sb.AppendLine($"  white_balance_tint {F(pp.whiteBalanceTint)}");
                sb.AppendLine("  vignette_color");
                sb.AppendLine($"    r {F(pp.vignetteColor.r)}");
                sb.AppendLine($"    g {F(pp.vignetteColor.g)}");
                sb.AppendLine($"    b {F(pp.vignetteColor.b)}");
                sb.AppendLine($"    a {F(pp.vignetteColor.a)}");
                sb.AppendLine("  vignette_center");
                sb.AppendLine($"    x {F(pp.vignetteCenter.x)}");
                sb.AppendLine($"    y {F(pp.vignetteCenter.y)}");
                sb.AppendLine($"  vignette_intensity {F(pp.vignetteIntensity)}");
                sb.AppendLine($"  vignette_smoothness {F(pp.vignetteSmoothness)}");
                sb.AppendLine($"  vignette_rounded {(pp.vignetteRounded ? "true" : "false")}");
                sb.AppendLine($"  bloom_intensity {F(pp.bloomIntensity)}");
                sb.AppendLine($"  bloom_threshold {F(pp.bloomThreshold)}");
                sb.AppendLine($"  bloom_scatter {F(pp.bloomScatter)}");
                sb.AppendLine($"  bloom_clamp {F(pp.bloomClamp)}");
                sb.AppendLine("  bloom_tint");
                sb.AppendLine($"    r {F(pp.bloomTint.r)}");
                sb.AppendLine($"    g {F(pp.bloomTint.g)}");
                sb.AppendLine($"    b {F(pp.bloomTint.b)}");
                sb.AppendLine($"    a {F(pp.bloomTint.a)}");
            }

            // Custom components (boid authoring data, etc.) — emitted verbatim.
            if (info.customComponents != null)
            {
                foreach (var block in info.customComponents)
                {
                    if (string.IsNullOrEmpty(block)) continue;
                    sb.AppendLine();
                    sb.AppendLine(block.TrimEnd('\n'));
                }
            }
        }

        // ============= Legacy .hza binary writers (kept for non-scene assets) =============

        static void WriteEntity(EntityExportInfo info, ulong stableId, string outputPath)
        {
            // Determine components
            bool hasModel = !string.IsNullOrEmpty(info.modelAssetPath);
            bool hasMaterial = !string.IsNullOrEmpty(info.materialAssetPath);

            int compCount = 4; // SceneEntityTag + EcsStableId + EntityName + Transform
            compCount++; // EcsVisibility
            if (hasModel) compCount++; // EcsModelRef
            if (hasMaterial) compCount++; // EcsMaterialRef
            if (hasModel) compCount++; // EcsAABB
            bool hasAnimation = !string.IsNullOrEmpty(info.animationAssetPath);
            bool hasParent = info.parentStableId != 0;
            if (hasAnimation) compCount++; // EcsAnimationRef
            if (hasParent) compCount++; // EcsParent
            if (info.isCamera) compCount++; // Camera
            if (info.isDirectionalLight) compCount++; // EcsDirectionalLight
            if (info.isPointLight) compCount++; // EcsPointLight
            if (info.isSceneSettings) compCount++; // EcsSceneSettings
            bool hasTerrain = !string.IsNullOrEmpty(info.terrainAssetPath);
            if (hasTerrain) compCount++; // EcsTerrainRef

            // Collect entity-level deps (model + material paths)
            var entityDeps = new List<string>();
            if (hasModel) entityDeps.Add(info.modelAssetPath);
            if (hasMaterial) entityDeps.Add(info.materialAssetPath);
            if (hasAnimation) entityDeps.Add(info.animationAssetPath);
            if (hasTerrain) entityDeps.Add(info.terrainAssetPath);

            var w = new HZBlobWriter(4096);
            int blobBase = w.Position;

            // === EntityBlobAsset header ===
            w.WriteHeader("EntityBlobAsset");
            int depsPtrPos = w.ReserveBlobPtr(); // header.dependencies

            w.WriteU64(stableId); // stable_id

            int compsPtrPos = w.ReserveBlobPtr(); // components

            Debug.Assert(w.Position == SIZEOF_ENTITY_BLOB_ASSET,
                $"EntityBlobAsset size mismatch: expected {SIZEOF_ENTITY_BLOB_ASSET}, got {w.Position}");

            // === SceneComponentBlob array ===
            int compsArrayPos = w.Position;
            w.PatchBlobPtr(compsPtrPos, compsArrayPos, compCount,
                SIZEOF_SCENE_COMPONENT_BLOB, "SceneComponentBlob");

            // We need to track each component's data BlobPtr position to patch later
            var compSlots = new List<(ulong hash, int dataSize, int dataPtrPos, Action<HZBlobWriter> writeData)>();

            // Helper to reserve a component slot
            void ReserveComponent(ulong hash, int dataSize, Action<HZBlobWriter> writeData)
            {
                w.WriteU64(hash);           // stable_type_hash (8)
                w.WriteU32((uint)dataSize); // data_size (4)
                int dataPtrPos = w.ReserveBlobPtr(); // data BlobArray(u8) (16)
                w.WriteZeros(4);            // padding to 32 (u64 alignment)
                compSlots.Add((hash, dataSize, dataPtrPos, writeData));
            }

            // --- SceneEntityTag ---
            ReserveComponent(HASH_SceneEntityTag, SIZE_SceneEntityTag, wr => {
                wr.WriteU8(0);
            });

            // --- EcsStableId ---
            ReserveComponent(HASH_EcsStableId, SIZE_EcsStableId, wr => {
                wr.WriteU64(stableId);
            });

            // --- EntityName ---
            ReserveComponent(HASH_EntityName, SIZE_EntityName, wr => {
                byte[] nameBytes = Encoding.UTF8.GetBytes(info.name ?? "");
                int len = Math.Min(nameBytes.Length, 63);
                wr.WriteU32((uint)len);
                wr.WriteBytes(nameBytes.Length >= 64
                    ? nameBytes[..64]
                    : nameBytes);
                // Pad to 64 chars
                int pad = 64 - Math.Min(nameBytes.Length, 64);
                if (pad > 0) wr.WriteZeros(pad);
            });

            // --- LocalTransform (coordinate conversion: negate X, quaternion rotation) ---
            ReserveComponent(HASH_LocalTransform, SIZE_LocalTransform, wr => {
                // position: negate X
                wr.WriteVec3(-info.position.x, info.position.y, info.position.z);
                // rotation: quaternion, X-mirror: (qx, -qy, -qz, qw)
                var q = info.rotation;
                wr.WriteVec4(q.x, -q.y, -q.z, q.w);
                // scale: unchanged
                wr.WriteVec3(info.scale.x, info.scale.y, info.scale.z);
            });

            // --- EcsVisibility ---
            ReserveComponent(HASH_EcsVisibility, SIZE_EcsVisibility, wr => {
                wr.WriteU32(info.visible ? 1u : 0u);
                wr.WriteU32(info.castsShadow ? 1u : 0u);
            });

            // --- EcsModelRef ---
            if (hasModel)
            {
                ReserveComponent(HASH_EcsModelRef, SIZE_EcsModelRef, null); // special: needs BlobAssetRef patching
            }

            // --- EcsMaterialRef ---
            if (hasMaterial)
            {
                ReserveComponent(HASH_EcsMaterialRef, SIZE_EcsMaterialRef, null); // special: needs BlobAssetRef patching
            }

            // --- EcsAnimationRef ---
            if (hasAnimation)
            {
                ReserveComponent(HASH_EcsAnimationRef, SIZE_EcsAnimationRef, null); // BlobAssetRef patching
            }

            // --- EcsTerrainRef ---
            if (hasTerrain)
            {
                ReserveComponent(HASH_EcsTerrainRef, SIZE_EcsTerrainRef, null); // BlobAssetRef + 2 u32s
            }

            // --- EcsParent ---
            if (hasParent)
            {
                ReserveComponent(HASH_EcsParent, SIZE_EcsParent, wr => {
                    // Write the parent's stable_id as the EcsEntity value.
                    // scene_patch_entity_refs_load will map this to a live entity.
                    wr.WriteU64(info.parentStableId);
                });
            }

            // --- EcsAABB ---
            if (hasModel)
            {
                ReserveComponent(HASH_EcsAABB, SIZE_EcsAABB, wr => {
                    var b = info.bounds;
                    wr.WriteVec3(-b.max.x, b.min.y, b.min.z);
                    wr.WriteVec3(-b.min.x, b.max.y, b.max.z);
                });
            }

            // --- Camera ---
            if (info.isCamera)
            {
                ReserveComponent(HASH_Camera, SIZE_Camera, wr => {
                    // Camera struct layout:
                    // mat4 view(64) + mat4 proj(64) + mat4 view_proj(64) + mat4 inv_view_proj(64) = 256 zeros
                    // Frustum frustum(96) = 96 zeros
                    // quaternion rot(16) + vec3 pos(12) + f32 fov(4) + f32 near_plane(4) + f32 far_plane(4) + f32 aspect(4)
                    wr.WriteZeros(256); // 4x mat4 (computed at runtime)
                    wr.WriteZeros(96);  // Frustum (computed at runtime)

                    // quaternion rot: convert Unity LH→RH
                    // 1) X-mirror: (qx, -qy, -qz, qw)
                    // 2) Unity forward is +Z, engine forward is -Z → rotate 180° around Y
                    var mirrored = new Quaternion(info.rotation.x, -info.rotation.y, -info.rotation.z, info.rotation.w);
                    var flip180Y = Quaternion.Euler(0, 180, 0);
                    var q = mirrored * flip180Y;
                    wr.WriteVec4(q.x, q.y, q.z, q.w);

                    // vec3 pos: negate X
                    wr.WriteVec3(-info.position.x, info.position.y, info.position.z);

                    wr.WriteF32(info.fov);
                    wr.WriteF32(info.nearPlane);
                    wr.WriteF32(info.farPlane);
                    wr.WriteF32(0); // aspect (computed at runtime)
                });
            }

            // --- EcsDirectionalLight ---
            if (info.isDirectionalLight)
            {
                ReserveComponent(HASH_EcsDirectionalLight, SIZE_EcsDirectionalLight, wr => {
                    wr.WriteVec3(info.lightColor.x, info.lightColor.y, info.lightColor.z);
                    wr.WriteF32(info.lightIntensity);
                    wr.WriteU32(info.castShadow ? 1u : 0u);
                    wr.WriteF32(info.shadowStrength);
                    wr.WriteF32(info.shadowBias);
                    wr.WriteF32(info.shadowNormalBias);
                });
            }

            // --- EcsPointLight ---
            if (info.isPointLight)
            {
                ReserveComponent(HASH_EcsPointLight, SIZE_EcsPointLight, wr => {
                    wr.WriteVec3(info.lightColor.x, info.lightColor.y, info.lightColor.z);
                    wr.WriteF32(info.pointLightRadius);
                    wr.WriteF32(info.lightIntensity);
                });
            }

            // --- EcsSceneSettings ---
            if (info.isSceneSettings)
            {
                ReserveComponent(HASH_EcsSceneSettings, SIZE_EcsSceneSettings, wr => {
                    wr.WriteVec4(info.ambientColor.x, info.ambientColor.y, info.ambientColor.z, 1f);
                    wr.WriteF32(info.ambientIntensity);
                    wr.WriteVec4(info.shadowColor.r, info.shadowColor.g, info.shadowColor.b, info.shadowColor.a);
                    wr.WriteF32(info.maxShadowDistance);
                });
            }

            Debug.Assert(w.Position == compsArrayPos + compCount * SIZEOF_SCENE_COMPONENT_BLOB,
                $"Component array size mismatch");

            // === Write component data ===
            for (int i = 0; i < compSlots.Count; i++)
            {
                var slot = compSlots[i];
                int dataPos = w.Position;
                w.PatchBlobPtr(slot.dataPtrPos, dataPos, slot.dataSize, 1, "u8");

                if (slot.hash == HASH_EcsModelRef)
                {
                    // BlobAssetRef(ModelBlobAsset) — 24 bytes
                    // Write placeholder, then patch path string after
                    int refPos = w.Position;
                    w.WriteZeros(SIZEOF_BLOB_ASSET_REF);
                    // Write path string data and patch the BlobAssetRef
                    w.WriteAndPatchBlobAssetRef(refPos, info.modelAssetPath,
                        SIZEOF_MODEL_BLOB_ASSET, "ModelBlobAsset");
                }
                else if (slot.hash == HASH_EcsMaterialRef)
                {
                    int refPos = w.Position;
                    w.WriteZeros(SIZEOF_BLOB_ASSET_REF);
                    w.WriteAndPatchBlobAssetRef(refPos, info.materialAssetPath,
                        SIZEOF_MATERIAL_BLOB_ASSET, "MaterialBlobAsset");
                }
                else if (slot.hash == HASH_EcsAnimationRef)
                {
                    int refPos = w.Position;
                    w.WriteZeros(SIZEOF_BLOB_ASSET_REF);
                    w.WriteAndPatchBlobAssetRef(refPos, info.animationAssetPath,
                        SIZEOF_ANIMATION_BLOB_ASSET, "AnimationBlobAsset");
                }
                else if (slot.hash == HASH_EcsTerrainRef)
                {
                    // EcsTerrainRef layout: BlobAssetRef(24) + u32 grid_dim + u32 lod_levels = 32
                    int refPos = w.Position;
                    w.WriteZeros(SIZEOF_BLOB_ASSET_REF);
                    w.WriteU32(info.terrainGridDim);
                    w.WriteU32(info.terrainLodLevels);
                    w.WriteAndPatchBlobAssetRef(refPos, info.terrainAssetPath,
                        SIZEOF_TERRAIN_BLOB_ASSET, "TerrainBlobAsset");
                }
                else
                {
                    slot.writeData(w);
                }

                // Verify data size matches (for non-ref components the written size should match)
                // For ref components, the written size includes the string data after the struct
            }

            // Write entity header.dependencies
            if (entityDeps.Count > 0)
            {
                int depsArrayPos = w.Position;
                w.PatchBlobPtr(depsPtrPos, depsArrayPos, entityDeps.Count,
                    SIZEOF_STRING_BLOB, "StringBlob");

                int[] depSBPositions = new int[entityDeps.Count];
                for (int d = 0; d < entityDeps.Count; d++)
                    depSBPositions[d] = w.ReserveStringBlob();

                for (int d = 0; d < entityDeps.Count; d++)
                    w.WriteAndPatchStringBlob(depSBPositions[d], entityDeps[d]);
            }
            else
            {
                PatchEmptyBlobPtr(w, depsPtrPos, SIZEOF_STRING_BLOB, "StringBlob");
            }

            // Patch header.asset_size
            int finalSize = w.Position;
            w.PatchU64(blobBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            w.WriteToFile(outputPath);
        }

        static void WriteManifest(string[] entityPaths, string sceneDirAssetPath, string outputPath)
        {
            var w = new HZBlobWriter(4096);
            int blobBase = w.Position;

            w.WriteHeader("SceneBlobAsset");
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob"); // no scene-level deps

            int sceneDirPos = w.ReserveStringBlob();      // scene_dir
            int entitiesPtrPos = w.ReserveBlobPtr();       // entities

            Debug.Assert(w.Position == SIZEOF_SCENE_BLOB_ASSET,
                $"SceneBlobAsset size mismatch: expected {SIZEOF_SCENE_BLOB_ASSET}, got {w.Position}");

            // Entity BlobAssetRef array
            int entityArrayPos = w.Position;
            w.PatchBlobPtr(entitiesPtrPos, entityArrayPos, entityPaths.Length,
                SIZEOF_BLOB_ASSET_REF, "BlobAssetRef");

            int[] entityRefPositions = new int[entityPaths.Length];
            for (int i = 0; i < entityPaths.Length; i++)
            {
                entityRefPositions[i] = w.Position;
                w.WriteZeros(SIZEOF_BLOB_ASSET_REF);
            }

            // String data
            w.WriteAndPatchStringBlob(sceneDirPos, sceneDirAssetPath);

            for (int i = 0; i < entityPaths.Length; i++)
            {
                w.WriteAndPatchBlobAssetRef(entityRefPositions[i], entityPaths[i],
                    SIZEOF_ENTITY_BLOB_ASSET_REF, "EntityBlobAsset");
            }

            int finalSize = w.Position;
            w.PatchU64(blobBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            w.WriteToFile(outputPath);
        }

        static void PatchEmptyBlobPtr(HZBlobWriter w, int pos, int typeSize, string typeName)
        {
            w.PatchI32(pos, 0);
            w.PatchU32(pos + 4, 0);
            w.PatchU32(pos + 8, (uint)typeSize);
            w.PatchU32(pos + 12, (uint)HZBlobWriter.TypeHash(typeName, typeSize));
        }
    }
}
