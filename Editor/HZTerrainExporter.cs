using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity <see cref="Terrain"/> as a TerrainBlobAsset (.hzt) alongside its heightmap
    /// (R16 raw), splatmaps (RGBA PNG), and per-layer albedo/normal/mask PNGs. Mirrors the engine
    /// layout expected by <c>hz-engine-v5-ios</c>'s TerrainBlobAsset loader.
    ///
    /// Output goes to a single folder per terrain. Caller controls the path so the scene exporter
    /// can dedup (shared TerrainData → exported once).
    /// </summary>
    public static class HZTerrainExporter
    {
        const int MAX_LAYERS = 32;     // matches TERRAIN_MAX_LAYERS in engine terrain.h
        const int MAX_SPLATMAPS = 8;   // matches TERRAIN_MAX_SPLATMAPS in engine terrain.h
        const int DEFAULT_TILE_SIZE = 32;
        const int DEFAULT_MAX_LOD = 6;

        // --- grass defaults (engine-authored knobs; Unity has no equivalent) ---
        const float GRASS_TILE_SIZE = 7.0f;     // engine field-ring tile size
        const float GRASS_FALLOFF_EXP = 2.0f;   // density falloff toward max_view_distance
        const float GRASS_FAR_MULTIPLIER = 6.0f; // billboard far band = near * this
        const float GRASS_FAR_CAP = 1000.0f;    // clamp for the far view distance
        const int GRASS_INSTANCES_PER_TILE_CAP = 16384; // sanity cap on derived density (engine compacts survivors, so high per-tile is fine)
        const float GRASS_DEFAULT_ALPHA_CUTOFF = 0.5f;

        // Linear fudge on the ground-truth-derived instances_per_tile_max. The calibration uses
        // the terrain's MEAN coverage, but viewed regions are usually denser-than-average, so the
        // engine reads a bit thick. Lower this to thin grass out, raise toward 1.0 if too sparse.
        const float GRASS_DENSITY_CALIBRATION = 0.7f;

        /// <summary>Export a grass prototype's mesh as a shared GLB; returns the engine
        /// asset-relative ref (e.g. "unity/glb/Reeds.glb"). Injected by the scene exporter
        /// so grass models flow through the same GLB dedup as scene meshes.</summary>
        public delegate string ExportModelGlbFn(MeshRenderer mr, Mesh mesh);

        /// <summary>Export a grass albedo texture as a standalone TextureBlobAsset; returns
        /// the engine asset-relative ref (e.g. "unity/glb/textures/Grass_01.hzt").</summary>
        public delegate string ExportGrassAlbedoFn(Texture2D tex, bool hasAlpha);

        // Resolved grass data, populated from Unity detail prototypes before the manifest write.
        sealed class GrassLodData
        {
            public string modelRef;       // null/empty => billboard LOD (model omitted)
            public bool generateBillboard;
            public float billboardWidth;
            public float billboardHeight;
            public Vector2 billboardUvMin;     // blade sub-rect in the albedo atlas
            public Vector2 billboardUvExtent;  // (0,0) extent => whole sheet
            public float minViewDistance;
            public float maxViewDistance;
            public uint instancesMax;
            public uint instancesMin;
            public float densityFalloffExp;
        }

        sealed class GrassTypeData
        {
            public string albedoRef;
            public string densityRef;
            public int densityChannel;
            public int sourceLayer;       // index into TerrainData detail layers for GetDetailLayer
            public float scaleXzMin, scaleXzMax;
            public float scaleYMin, scaleYMax;
            public float alphaCutoff;
            public float windResponse;
            public Vector3 rootColor;
            public Vector3 tipColor;
            public List<GrassLodData> lods = new List<GrassLodData>();
        }

        /// <summary>
        /// Export a single <see cref="Terrain"/>.
        /// </summary>
        /// <param name="terrain">Source Unity terrain. Must have a non-null TerrainData.</param>
        /// <param name="outDir">Absolute or project-relative directory; created if missing.</param>
        /// <param name="assetPrefix">
        /// Asset-relative prefix the engine will see (e.g. "unity/terrain/MyTerrain"). Used inside
        /// the .hzt files to reference sibling assets.
        /// </param>
        /// <param name="exportModel">Callback to export a grass prototype mesh as a shared GLB.
        /// When null, mesh-based grass LODs are skipped (billboard-only grass still works).</param>
        /// <param name="exportAlbedo">Callback to export a grass albedo texture. When null,
        /// grass types are emitted without an albedo ref.</param>
        /// <returns>Asset-relative path to terrain.hzt, or null on failure.</returns>
        public static string Export(Terrain terrain, string outDir, string assetPrefix,
            ExportModelGlbFn exportModel = null, ExportGrassAlbedoFn exportAlbedo = null)
        {
            if (terrain == null) { Debug.LogError("[HZTerrain] null terrain"); return null; }
            var td = terrain.terrainData;
            if (td == null)
            {
                Debug.LogError($"[HZTerrain] '{terrain.name}': no TerrainData; skipping");
                return null;
            }

            Directory.CreateDirectory(outDir);

            int layerCount = (td.terrainLayers != null) ? Math.Min(td.terrainLayers.Length, MAX_LAYERS) : 0;
            if (td.terrainLayers != null && td.terrainLayers.Length > MAX_LAYERS)
                Debug.LogWarning($"[HZTerrain] '{td.name}' has {td.terrainLayers.Length} layers; engine caps at {MAX_LAYERS}, truncating.");

            int neededSplats = (layerCount + 3) / 4;
            int splatmapCount = Math.Min(neededSplats, MAX_SPLATMAPS);
            if (neededSplats > MAX_SPLATMAPS)
                Debug.LogWarning($"[HZTerrain] '{td.name}' needs {neededSplats} splatmaps; engine caps at {MAX_SPLATMAPS}, truncating.");

            // Authoring knobs — defaults for now; add an HZTerrainAuthoring MonoBehaviour later
            // if scenes need per-terrain overrides.
            int tileSize = DEFAULT_TILE_SIZE;
            int maxLod = DEFAULT_MAX_LOD;

            int hmRes = td.heightmapResolution;
            int amRes = td.alphamapResolution;

            Debug.Log($"[HZTerrain] '{td.name}': hm={hmRes}x{hmRes} am={amRes}x{amRes} layers={layerCount} splatmaps={splatmapCount} tile={tileSize} maxLod={maxLod} size={td.size}");

            WriteHeightmap(td, hmRes, outDir, assetPrefix);

            for (int s = 0; s < splatmapCount; s++)
            {
                int first = s * 4;
                int last = Math.Min(first + 4, layerCount);
                WriteSplatmap(td, amRes, s, first, last, layerCount, outDir, assetPrefix);
            }

            for (int i = 0; i < layerCount; i++)
                WriteLayer(td.terrainLayers[i], i, td.name, outDir, assetPrefix);

            // Grass: map Unity detail prototypes → engine grass types. Models/albedo flow
            // through the injected scene callbacks; density maps are written terrain-local.
            var grassTypes = BuildGrassTypes(terrain, td, outDir, assetPrefix, exportModel, exportAlbedo);

            // Grass workload scale, pulled from Unity terrain settings (same fallbacks as
            // BuildGrassTypes). The engine treats 0 as "use default", but we always have a
            // concrete value here, so emit it.
            // grass_density_scale = 1.0: the scene's detailObjectDensity is already baked into
            // each type's instances_per_tile_max via the ground-truth ComputeDetailInstanceTransforms
            // measurement in BuildGrassTypes. Re-applying it here would double-scale (and Unity's
            // response is non-linear, so a straight passthrough wouldn't match anyway).
            float grassViewDistance = terrain.detailObjectDistance > 0f ? terrain.detailObjectDistance : 100f;
            float grassDensityScale = 1.0f;

            string terrainHztPath = Path.Combine(outDir, "terrain.hzt");
            WriteManifest(td, terrainHztPath, assetPrefix, layerCount, splatmapCount, tileSize, maxLod,
                grassViewDistance, grassDensityScale, grassTypes);

            string assetRef = $"{assetPrefix}/terrain.hzt";
            Debug.Log($"[HZTerrain]   → {terrainHztPath}  (engine ref: {assetRef})");
            return assetRef;
        }

        // ---- heightmap ----

        static void WriteHeightmap(TerrainData td, int res, string outDir, string assetPrefix)
        {
            var heights = td.GetHeights(0, 0, res, res);
            var bytes = new byte[res * res * 2];
            // Engine convention: X is mirrored scene-wide (negate X), Z is not.
            // The terrain geometry's world_origin already follows the X-mirror, so
            // the heightmap must be X-flipped to match — and NOT Z-flipped (Z passes
            // through unchanged). Unity GetHeights is [z, x], written here as raw
            // R16 (no implicit flip), so: file[y][x] = heights[y][res-1-x].
            for (int y = 0; y < res; y++)
            {
                int row = y * res * 2;
                for (int x = 0; x < res; x++)
                {
                    ushort v = (ushort)Mathf.Clamp(heights[y, res - 1 - x] * 65535f, 0f, 65535f);
                    bytes[row + x * 2]     = (byte)(v & 0xFF);
                    bytes[row + x * 2 + 1] = (byte)(v >> 8);
                }
            }
            File.WriteAllBytes(Path.Combine(outDir, "heightmap.r16"), bytes);

            WriteTextureSidecar(
                path:      Path.Combine(outDir, "heightmap.hzt"),
                imagePath: $"/{assetPrefix}/heightmap",
                format:    "TEX_FORMAT_RAW_R16",
                width:     res,
                height:    res,
                isSrgb:    false);
        }

        // ---- splatmaps ----

        static void WriteSplatmap(TerrainData td, int res, int splatIdx, int firstLayer, int lastLayer,
            int retainedLayers, string outDir, string assetPrefix)
        {
            var alpha = td.GetAlphamaps(0, 0, res, res);
            int alphaLayers = td.alphamapLayers;

            var pixels = new Color[res * res];
            // Must match the heightmap's flip so heights and splat weights line up
            // under the same sample uv: X-flipped, not Z-flipped. Unity's
            // Texture2D->EncodeToPNG path applies an implicit Y-flip, so keeping
            // srcY here (res-1-y) cancels it → net no Z-flip; srcX (res-1-x)
            // applies the X-flip. See WriteHeightmap.
            for (int y = 0; y < res; y++)
            {
                int srcY = res - 1 - y;   // cancels Unity's PNG y-flip → net no Z-flip
                for (int x = 0; x < res; x++)
                {
                    int srcX = res - 1 - x;   // X-flip to match engine X-mirror
                    float r = 0f, g = 0f, b = 0f, a = 0f;
                    if (firstLayer + 0 < lastLayer && firstLayer + 0 < alphaLayers) r = alpha[srcY, srcX, firstLayer + 0];
                    if (firstLayer + 1 < lastLayer && firstLayer + 1 < alphaLayers) g = alpha[srcY, srcX, firstLayer + 1];
                    if (firstLayer + 2 < lastLayer && firstLayer + 2 < alphaLayers) b = alpha[srcY, srcX, firstLayer + 2];
                    if (firstLayer + 3 < lastLayer && firstLayer + 3 < alphaLayers) a = alpha[srcY, srcX, firstLayer + 3];

                    // Renormalize against RETAINED layers (what's actually exported). Prevents coverage
                    // gaps when layer count is capped below the terrain's real count.
                    float total = 0f;
                    for (int li = 0; li < retainedLayers && li < alphaLayers; li++)
                        total += alpha[srcY, srcX, li];
                    if (total > 1e-3f)
                    {
                        float inv = 1f / total;
                        r *= inv; g *= inv; b *= inv; a *= inv;
                    }
                    else if (splatIdx == 0)
                    {
                        r = 1f;   // fully layer-0 as a sane fallback on zero-coverage pixels
                    }
                    pixels[y * res + x] = new Color(r, g, b, a);
                }
            }

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
                tex.SetPixels(pixels);
                tex.Apply(false, false);
                File.WriteAllBytes(Path.Combine(outDir, $"splatmap{splatIdx}.png"), tex.EncodeToPNG());
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }

            WriteTextureSidecar(
                path:      Path.Combine(outDir, $"splatmap{splatIdx}.hzt"),
                imagePath: $"/{assetPrefix}/splatmap{splatIdx}",
                format:    "TEX_FORMAT_RAW",
                width:     res,
                height:    res,
                isSrgb:    false,
                hasAlpha:  true);   // splatmap's .a channel carries layer (4n+3) weights; cooker would drop alpha without this (BC1 → BC3).
        }

        // ---- per-layer textures ----

        static void WriteLayer(TerrainLayer layer, int idx, string terrainName, string outDir, string assetPrefix)
        {
            if (layer == null)
            {
                Debug.LogWarning($"[HZTerrain]   layer{idx}: null TerrainLayer, skipping");
                return;
            }
            if (layer.tileSize.x != layer.tileSize.y)
                Debug.LogWarning($"[HZTerrain]   layer{idx} '{layer.name}': non-uniform tileSize ({layer.tileSize}); engine uses scalar, taking .x");

            WriteLayerTex(layer.diffuseTexture,   idx, "albedo", "TEX_FORMAT_BC1", isSrgb: true,  isNormal: false, outDir, assetPrefix);
            WriteLayerTex(layer.normalMapTexture, idx, "normal", "TEX_FORMAT_BC5", isSrgb: false, isNormal: true,  outDir, assetPrefix);
            if (layer.maskMapTexture != null)
            {
                var mm = layer.maskMapTexture;
                // A 1x1 mask map carries no spatial data — it's effectively "no mask".
                // Log it (with the terrain name) so the right terrain can be confirmed.
                if (mm.width <= 1 && mm.height <= 1)
                    Debug.Log($"[HZTerrain] '{terrainName}' layer{idx} '{layer.name}': mask map is a flat {mm.width}x{mm.height} texture ('{mm.name}') — basically no mask");
                WriteLayerTex(mm, idx, "mask", "TEX_FORMAT_BC3", isSrgb: false, isNormal: false, outDir, assetPrefix);
            }
            else
                WriteDefaultMask(idx, outDir, assetPrefix);
        }

        // Unity TerrainLayer mask is (R=metallic, G=AO, B=height, A=smoothness). When a layer has
        // no mask map, Unity's terrain shader treats those as sensible defaults — critically B=0
        // (no height). The engine's terrain shader uses mask.b for height-based blend; falling
        // through to the shader's default-white sampler gives height=1, making unmasked layers
        // dominate everywhere. Emit a 1×1 default mask to keep the math honest.
        static void WriteDefaultMask(int layerIdx, string outDir, string assetPrefix)
        {
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                // R=0 metal, G=1 AO, B=0 height, A=0.5 smoothness
                tex.SetPixel(0, 0, new Color(0f, 1f, 0f, 0.5f));
                tex.Apply(false, false);
                File.WriteAllBytes(Path.Combine(outDir, $"layer{layerIdx}_mask.png"), tex.EncodeToPNG());
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
            WriteTextureSidecar(
                path:      Path.Combine(outDir, $"layer{layerIdx}_mask.hzt"),
                imagePath: $"/{assetPrefix}/layer{layerIdx}_mask",
                format:    "TEX_FORMAT_BC3",
                width:     1, height: 1, isSrgb: false);
            Debug.Log($"[HZTerrain]   layer{layerIdx}_mask: 1x1 synthesized default (R=0 G=1 B=0 A=0.5)");
        }

        static void WriteLayerTex(Texture2D tex, int layerIdx, string kind, string format,
            bool isSrgb, bool isNormal, string outDir, string assetPrefix)
        {
            if (tex == null) return;
            Texture2D readable = null;
            try
            {
                // MakeReadable handles DXT5nm normal unpacking + non-readable source textures
                // via RenderTexture blit. Reuses the GLB path's helper.
                readable = HZGlbWriter.MakeReadable(tex, isNormal);
                byte[] png = readable.EncodeToPNG();
                string pngPath = Path.Combine(outDir, $"layer{layerIdx}_{kind}.png");
                File.WriteAllBytes(pngPath, png);
                WriteTextureSidecar(
                    path:      Path.Combine(outDir, $"layer{layerIdx}_{kind}.hzt"),
                    imagePath: $"/{assetPrefix}/layer{layerIdx}_{kind}",
                    format:    format,
                    width:     readable.width,
                    height:    readable.height,
                    isSrgb:    isSrgb);
                Debug.Log($"[HZTerrain]   layer{layerIdx}_{kind}: {readable.width}x{readable.height} ({tex.name})");
            }
            finally
            {
                if (readable != null && readable != tex) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        // ---- grass (detail prototypes) ----

        /// <summary>
        /// Map Unity's detail-grass system onto the engine's grass_types. Pulls as much real
        /// data from Unity as possible (scales, colors, wind, view distance, density) and falls
        /// back to documented defaults only for concepts Unity lacks (LOD chain, far billboard,
        /// falloff). Returns the resolved types; density maps are written as a side effect.
        /// </summary>
        static List<GrassTypeData> BuildGrassTypes(Terrain terrain, TerrainData td, string outDir,
            string assetPrefix, ExportModelGlbFn exportModel, ExportGrassAlbedoFn exportAlbedo)
        {
            var result = new List<GrassTypeData>();
            var protos = td.detailPrototypes;
            if (protos == null || protos.Length == 0) return result;

            // Terrain-wide knobs that drive view distance / density.
            float detailDistance = terrain.detailObjectDistance;   // Unity's detail render distance
            if (detailDistance <= 0f) detailDistance = 100f;
            float wind = td.wavingGrassStrength;                   // global waving-grass strength

            int dw = td.detailWidth, dh = td.detailHeight;
            int maxScatter = Mathf.Max(1, td.maxDetailScatterPerRes);

            // instances_per_tile_max is calibrated to GROUND TRUTH: we ask Unity's own scatterer
            // (ComputeDetailInstanceTransforms) how many instances it actually places for this
            // prototype at the scene's detailObjectDensity, then back out the full-coverage
            // per-tile count. Unity's density response is non-linear (≈ detailObjectDensity²) and
            // its absolute count isn't a documented formula, so measuring beats deriving.
            //
            // The engine renders ≈ instances_per_tile_max × grass_density_scale × coverage(tile),
            // and the coverage map averages to covAvg over the field, so:
            //     engine_total ≈ instances_per_tile_max × grass_density_scale × covAvg × (area/tile²)
            // We want engine_total == Unity's measured count N(d), with the SAME coverage map
            // driving spatial layout. Density is baked here and grass_density_scale emitted = 1.0:
            //     instances_per_tile_max = N(d) / (area × covAvg) × tile²
            float area = Mathf.Max(1e-3f, td.size.x * td.size.z);
            int patchesPerAxis = Mathf.Max(1, dw / Mathf.Max(1, td.detailResolutionPerPatch));
            float densityForCount = terrain.detailObjectDensity; // 0..1; what the scene renders

            float near = detailDistance;
            float far = Mathf.Min(near * GRASS_FAR_MULTIPLIER, GRASS_FAR_CAP);

            for (int i = 0; i < protos.Length; i++)
            {
                var dp = protos[i];

                // Unity's actual instance count for this prototype at the scene density.
                long unityCount = 0;
                for (int px = 0; px < patchesPerAxis; px++)
                    for (int py = 0; py < patchesPerAxis; py++)
                    {
                        var xforms = td.ComputeDetailInstanceTransforms(px, py, i, densityForCount, out _);
                        if (xforms != null) unityCount += xforms.Length;
                    }
                // Mean coverage for this layer (same normalization the density map uses).
                var covLayer = td.GetDetailLayer(0, 0, dw, dh, i);
                long covSum = 0;
                foreach (var v in covLayer) covSum += v;
                float covAvg = (float)covSum / (dw * dh) / maxScatter;

                uint instancesMax = (uint)Mathf.Clamp(
                    covAvg > 1e-5f
                        ? Mathf.RoundToInt((float)(unityCount / (area * covAvg)) * GRASS_TILE_SIZE * GRASS_TILE_SIZE * GRASS_DENSITY_CALIBRATION)
                        : 0,
                    1, GRASS_INSTANCES_PER_TILE_CAP);
                uint instancesMin = (uint)Mathf.Max(1, Mathf.RoundToInt(instancesMax * 0.15f));
                Debug.Log($"[HZTerrain]   grass proto{i}: unityCount={unityCount} covAvg={covAvg:F3} -> instances_per_tile_max={instancesMax}");

                var gt = new GrassTypeData
                {
                    sourceLayer = i,
                    scaleXzMin = dp.minWidth,
                    scaleXzMax = dp.maxWidth,
                    scaleYMin = dp.minHeight,
                    scaleYMax = dp.maxHeight,
                    rootColor = new Vector3(dp.dryColor.r, dp.dryColor.g, dp.dryColor.b),
                    tipColor = new Vector3(dp.healthyColor.r, dp.healthyColor.g, dp.healthyColor.b),
                    windResponse = wind > 0f ? wind : 1f,
                    alphaCutoff = GRASS_DEFAULT_ALPHA_CUTOFF,
                };

                bool isMesh = dp.usePrototypeMesh && dp.prototype != null;
                if (isMesh)
                {
                    var go = dp.prototype;
                    var mf = go.GetComponentInChildren<MeshFilter>();
                    var mr = go.GetComponentInChildren<MeshRenderer>();
                    if (mf == null || mf.sharedMesh == null || mr == null)
                    {
                        Debug.LogWarning($"[HZTerrain] grass proto {i} '{go.name}': usePrototypeMesh but no MeshFilter/MeshRenderer; treating as billboard");
                        isMesh = false;
                    }
                    else
                    {
                        var mat = mr.sharedMaterial;
                        gt.alphaCutoff = TryGetAlphaCutoff(mat, GRASS_DEFAULT_ALPHA_CUTOFF);
                        gt.albedoRef = ExportAlbedoFor(mat, exportAlbedo);
                        string modelRef = exportModel != null ? exportModel(mr, mf.sharedMesh) : null;

                        // Cross-billboard dims + atlas sub-rect derived from the near mesh, so
                        // the far card matches the blade's silhouette and samples just the
                        // blade's UV region (not the whole sheet). Emitted as concrete values so
                        // they can be hand-tweaked in the .hzt.
                        ComputeBillboard(mf.sharedMesh, out float bbW, out float bbH,
                                         out Vector2 uvMin, out Vector2 uvExtent);

                        // LOD0 = the Unity mesh (near band).
                        gt.lods.Add(new GrassLodData {
                            modelRef = modelRef,
                            generateBillboard = false,
                            minViewDistance = 0f,
                            maxViewDistance = near,
                            instancesMax = instancesMax,
                            instancesMin = instancesMin,
                            densityFalloffExp = GRASS_FALLOFF_EXP,
                        });
                        // LOD1 = engine-synthesized billboard (far band). Unity gives no far geometry.
                        gt.lods.Add(new GrassLodData {
                            generateBillboard = true,
                            billboardWidth = bbW,
                            billboardHeight = bbH,
                            billboardUvMin = uvMin,
                            billboardUvExtent = uvExtent,
                            minViewDistance = near,
                            maxViewDistance = far,
                            instancesMax = (uint)Mathf.Max(1, instancesMax / 2),
                            instancesMin = (uint)Mathf.Max(1, instancesMin / 2),
                            densityFalloffExp = GRASS_FALLOFF_EXP,
                        });
                    }
                }

                if (!isMesh)
                {
                    // Texture billboard prototype (renderMode Grass / GrassBillboard): single
                    // billboard LOD using the prototype texture as albedo.
                    var tex = dp.prototypeTexture;
                    if (tex != null && exportAlbedo != null)
                        gt.albedoRef = exportAlbedo(tex, true);   // grass textures are alpha-cut
                    gt.lods.Add(new GrassLodData {
                        generateBillboard = true,
                        billboardWidth = 0.5f * (dp.minWidth + dp.maxWidth),
                        billboardHeight = 0.5f * (dp.minHeight + dp.maxHeight),
                        minViewDistance = 0f,
                        maxViewDistance = near,
                        instancesMax = instancesMax,
                        instancesMin = instancesMin,
                        densityFalloffExp = GRASS_FALLOFF_EXP,
                    });
                }

                result.Add(gt);
            }

            WriteDensityMaps(td, result, outDir, assetPrefix);

            Debug.Log($"[HZTerrain] grass: {result.Count} type(s) (near={near:F0} far={far:F0}); instances/tile measured per type from Unity's scatterer");
            return result;
        }

        static string ExportAlbedoFor(Material mat, ExportGrassAlbedoFn exportAlbedo)
        {
            if (mat == null || exportAlbedo == null) return null;
            var tex = FindAlbedoTexture(mat);
            if (tex == null)
            {
                Debug.LogWarning($"[HZTerrain] grass material '{mat.name}' (shader '{mat.shader?.name}'): no albedo texture found; grass will be untextured");
                return null;
            }
            // Grass albedo virtually always has cutout alpha — keep it (BC3).
            return exportAlbedo(tex, true);
        }

        // Custom vegetation shaders (e.g. Idyllic Fantasy Nature) name their albedo slot
        // differently from URP's _BaseMap, so HasProperty("_BaseMap") fails even though the
        // material serializes one. Enumerate the shader's actual texture properties and pick
        // the best albedo candidate by name priority, falling back to the first bound texture.
        static Texture2D FindAlbedoTexture(Material mat)
        {
            string[] names = mat.GetTexturePropertyNames();
            if (names == null || names.Length == 0) return null;

            // Priority: explicit albedo/base/main/diffuse names first.
            string[] preferred = { "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_Texture", "_DiffuseMap", "_MainTexture" };
            foreach (var p in preferred)
                foreach (var n in names)
                    if (n == p && mat.GetTexture(n) is Texture2D t0 && t0 != null)
                        return t0;

            // Heuristic: any property whose name hints albedo.
            foreach (var n in names)
            {
                string l = n.ToLowerInvariant();
                bool albedoish = l.Contains("albedo") || l.Contains("basecolor") || l.Contains("diffuse")
                                 || l.Contains("main") || (l.Contains("base") && l.Contains("map"));
                bool exclude = l.Contains("normal") || l.Contains("bump") || l.Contains("mask")
                               || l.Contains("metallic") || l.Contains("specular") || l.Contains("occlusion")
                               || l.Contains("emission") || l.Contains("detail") || l.Contains("height");
                if (albedoish && !exclude && mat.GetTexture(n) is Texture2D t1 && t1 != null)
                    return t1;
            }

            // Last resort: first bound colour-ish texture that isn't obviously a data map.
            foreach (var n in names)
            {
                string l = n.ToLowerInvariant();
                bool exclude = l.Contains("normal") || l.Contains("bump") || l.Contains("mask")
                               || l.Contains("metallic") || l.Contains("specular") || l.Contains("occlusion")
                               || l.Contains("emission") || l.Contains("detail") || l.Contains("height");
                if (!exclude && mat.GetTexture(n) is Texture2D t2 && t2 != null)
                    return t2;
            }
            return null;
        }

        // Derive the engine cross-billboard's size + albedo sub-rect from the grass mesh:
        // width = the larger XZ footprint, height = the Y range (full card extents); the UV
        // rect is the bounding box of the mesh's UV0, so the billboard samples only the blade's
        // region of the atlas instead of the whole sheet. Falls back to a unit UV rect when the
        // mesh has no UVs.
        static void ComputeBillboard(Mesh mesh, out float width, out float height,
                                     out Vector2 uvMin, out Vector2 uvExtent)
        {
            var b = mesh.bounds;
            width = Mathf.Max(b.size.x, b.size.z);
            height = b.size.y;
            if (width <= 0f) width = 0.8f;
            if (height <= 0f) height = 1.5f;

            var uvs = mesh.uv;
            if (uvs == null || uvs.Length == 0)
            {
                uvMin = Vector2.zero;
                uvExtent = Vector2.one;
                return;
            }
            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            foreach (var uv in uvs)
            {
                if (uv.x < minU) minU = uv.x;
                if (uv.y < minV) minV = uv.y;
                if (uv.x > maxU) maxU = uv.x;
                if (uv.y > maxV) maxV = uv.y;
            }
            uvMin = new Vector2(minU, minV);
            uvExtent = new Vector2(maxU - minU, maxV - minV);
        }

        static float TryGetAlphaCutoff(Material mat, float fallback)
        {
            if (mat == null) return fallback;
            if (mat.HasProperty("_Cutoff")) return mat.GetFloat("_Cutoff");
            if (mat.HasProperty("_AlphaClipThreshold")) return mat.GetFloat("_AlphaClipThreshold");
            return fallback;
        }

        /// <summary>
        /// Pack each group of ≤4 grass types into one RGBA density map (R/G/B/A → density_channel
        /// 0/1/2/3), normalizing Unity's integer detail counts to [0,1]. Uses the exact splatmap
        /// flip (X-mirror, no net Z-flip) so coverage lines up with the heightmap/splatmaps.
        /// Mutates each type's densityRef/densityChannel.
        /// </summary>
        static void WriteDensityMaps(TerrainData td, List<GrassTypeData> types,
            string outDir, string assetPrefix)
        {
            if (types.Count == 0) return;
            int dw = td.detailWidth, dh = td.detailHeight;
            int maxScatter = Mathf.Max(1, td.maxDetailScatterPerRes);
            float inv = 1f / maxScatter;
            int groups = (types.Count + 3) / 4;

            for (int g = 0; g < groups; g++)
            {
                // Fetch the up-to-4 detail layers for this group.
                int[][,] layers = new int[4][,];
                for (int c = 0; c < 4; c++)
                {
                    int ti = g * 4 + c;
                    if (ti < types.Count)
                        layers[c] = td.GetDetailLayer(0, 0, dw, dh, types[ti].sourceLayer);
                }

                var pixels = new Color[dw * dh];
                for (int y = 0; y < dh; y++)
                {
                    int srcY = dh - 1 - y;   // cancels Unity's PNG y-flip → net no Z-flip
                    for (int x = 0; x < dw; x++)
                    {
                        int srcX = dw - 1 - x;   // X-flip to match engine X-mirror
                        float r = layers[0] != null ? Mathf.Clamp01(layers[0][srcY, srcX] * inv) : 0f;
                        float gg = layers[1] != null ? Mathf.Clamp01(layers[1][srcY, srcX] * inv) : 0f;
                        float b = layers[2] != null ? Mathf.Clamp01(layers[2][srcY, srcX] * inv) : 0f;
                        float a = layers[3] != null ? Mathf.Clamp01(layers[3][srcY, srcX] * inv) : 0f;
                        pixels[y * dw + x] = new Color(r, gg, b, a);
                    }
                }

                Texture2D tex = null;
                try
                {
                    tex = new Texture2D(dw, dh, TextureFormat.RGBA32, false);
                    tex.SetPixels(pixels);
                    tex.Apply(false, false);
                    File.WriteAllBytes(Path.Combine(outDir, $"grass_density{g}.png"), tex.EncodeToPNG());
                }
                finally
                {
                    if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                }

                WriteTextureSidecar(
                    path:      Path.Combine(outDir, $"grass_density{g}.hzt"),
                    imagePath: $"/{assetPrefix}/grass_density{g}",
                    format:    "TEX_FORMAT_RAW",
                    width:     dw,
                    height:    dh,
                    isSrgb:    false,
                    hasAlpha:  true);   // .a carries the 4th type's coverage; keep it (BC3 not BC1)

                string densityRef = $"{assetPrefix}/grass_density{g}.hzt";
                for (int c = 0; c < 4; c++)
                {
                    int ti = g * 4 + c;
                    if (ti < types.Count)
                    {
                        types[ti].densityRef = densityRef;
                        types[ti].densityChannel = c;
                    }
                }
            }
        }

        // ---- sidecars ----

        static void WriteTextureSidecar(string path, string imagePath, string format,
            int width, int height, bool isSrgb, bool hasAlpha = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("type TextureBlobAsset");
            sb.AppendLine("version 3");
            sb.AppendLine("---");
            sb.AppendLine($"image \"{imagePath}\" u8");
            sb.AppendLine($"format {format}");
            sb.AppendLine($"width {width}");
            sb.AppendLine($"height {height}");
            sb.AppendLine($"is_srgb {(isSrgb ? "true" : "false")}");
            if (hasAlpha) sb.AppendLine("has_alpha true");
            File.WriteAllText(path, sb.ToString());
        }

        static void WriteManifest(TerrainData td, string path, string assetPrefix,
            int layerCount, int splatmapCount, int tileSize, int maxLod,
            float grassViewDistance, float grassDensityScale,
            List<GrassTypeData> grassTypes)
        {
            var size = td.size;
            var sb = new StringBuilder();
            sb.AppendLine("type TerrainBlobAsset");
            sb.AppendLine("version 3");
            sb.AppendLine("---");
            sb.AppendLine($"heightmap \"{assetPrefix}/heightmap.hzt\" TextureBlobAsset");
            sb.AppendLine($"world_width {F(size.x)}");
            sb.AppendLine($"world_depth {F(size.z)}");
            sb.AppendLine($"height_scale {F(size.y)}");
            sb.AppendLine($"tile_size {tileSize}");
            sb.AppendLine($"max_lod {maxLod}");

            sb.AppendLine($"splatmaps {splatmapCount}");
            for (int s = 0; s < splatmapCount; s++)
            {
                sb.AppendLine($"  texture \"{assetPrefix}/splatmap{s}.hzt\" TextureBlobAsset");
                sb.AppendLine();
            }

            sb.AppendLine($"layers {layerCount}");
            for (int i = 0; i < layerCount; i++)
            {
                var layer = td.terrainLayers[i];
                sb.AppendLine($"  albedo \"{assetPrefix}/layer{i}_albedo.hzt\" TextureBlobAsset");
                if (layer != null && layer.normalMapTexture != null)
                    sb.AppendLine($"  normal \"{assetPrefix}/layer{i}_normal.hzt\" TextureBlobAsset");
                // mask is always emitted (either the Unity mask or a synthesized default)
                sb.AppendLine($"  mask \"{assetPrefix}/layer{i}_mask.hzt\" TextureBlobAsset");
                sb.AppendLine($"  uv_scale {F(layer != null ? layer.tileSize.x : 1f)}");
                sb.AppendLine();
            }

            // Grass (declaration order: ...layers, grass_tile_size, grass_types). Fields per
            // type/LOD are emitted in struct order; omitted fields default to zero (e.g. a
            // billboard LOD omits `model`). See engine terrain.h / terrain.gen.h.
            sb.AppendLine($"grass_tile_size {F(GRASS_TILE_SIZE)}");
            sb.AppendLine($"grass_view_distance {F(grassViewDistance)}");
            sb.AppendLine($"grass_density_scale {F(grassDensityScale)}");
            int grassCount = grassTypes != null ? grassTypes.Count : 0;
            sb.AppendLine($"grass_types {grassCount}");
            for (int t = 0; t < grassCount; t++)
            {
                var gt = grassTypes[t];
                if (!string.IsNullOrEmpty(gt.albedoRef))
                    sb.AppendLine($"  albedo \"{gt.albedoRef}\" TextureBlobAsset");
                if (!string.IsNullOrEmpty(gt.densityRef))
                    sb.AppendLine($"  density_map \"{gt.densityRef}\" TextureBlobAsset");
                sb.AppendLine($"  density_channel {gt.densityChannel}");
                sb.AppendLine($"  scale_xz_min {F(gt.scaleXzMin)}");
                sb.AppendLine($"  scale_xz_max {F(gt.scaleXzMax)}");
                sb.AppendLine($"  scale_y_min {F(gt.scaleYMin)}");
                sb.AppendLine($"  scale_y_max {F(gt.scaleYMax)}");
                sb.AppendLine($"  alpha_cutoff {F(gt.alphaCutoff)}");
                sb.AppendLine($"  wind_response {F(gt.windResponse)}");
                sb.AppendLine($"  root_color {F(gt.rootColor.x)} {F(gt.rootColor.y)} {F(gt.rootColor.z)}");
                sb.AppendLine($"  tip_color {F(gt.tipColor.x)} {F(gt.tipColor.y)} {F(gt.tipColor.z)}");
                sb.AppendLine($"  lods {gt.lods.Count}");
                foreach (var lod in gt.lods)
                {
                    // model first (struct order); omit on billboard LODs so it defaults to null.
                    if (!lod.generateBillboard && !string.IsNullOrEmpty(lod.modelRef))
                        sb.AppendLine($"    model \"{lod.modelRef}\" ModelBlobAsset");
                    sb.AppendLine($"    generate_billboard {(lod.generateBillboard ? "true" : "false")}");
                    if (lod.generateBillboard)
                    {
                        sb.AppendLine($"    billboard_width {F(lod.billboardWidth)}");
                        sb.AppendLine($"    billboard_height {F(lod.billboardHeight)}");
                        sb.AppendLine($"    billboard_uv_min {F(lod.billboardUvMin.x)} {F(lod.billboardUvMin.y)}");
                        sb.AppendLine($"    billboard_uv_extent {F(lod.billboardUvExtent.x)} {F(lod.billboardUvExtent.y)}");
                    }
                    sb.AppendLine($"    min_view_distance {F(lod.minViewDistance)}");
                    sb.AppendLine($"    max_view_distance {F(lod.maxViewDistance)}");
                    sb.AppendLine($"    instances_per_tile_max {lod.instancesMax}");
                    sb.AppendLine($"    instances_per_tile_min {lod.instancesMin}");
                    sb.AppendLine($"    density_falloff_exp {F(lod.densityFalloffExp)}");
                }
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }

        static string F(float v) => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
