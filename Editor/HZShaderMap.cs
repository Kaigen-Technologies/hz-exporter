using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Declarative Unity-shader → engine-shader + property mapping, loaded from
    /// Assets/Editor/HZExporter/shader_mapping.json. Consumed by HZGlbWriter to decide
    /// which Unity material properties feed which glTF PBR slot (and eventually which
    /// engine-shader uniform for custom shaders).
    /// </summary>
    public static class HZShaderMap
    {
        // ---- Config schema (matches shader_mapping.json) ----
        [System.Serializable]
        public class PropertyMap
        {
            public string unity;
            public string slot;
            public string kind;
            // Node-embedded resources: Unity's generated property name cannot be reconstructed
            // reliably (a subgraph names them after the SubGraphNode), so bind by asset guid.
            public string guid;
        }

        [System.Serializable]
        public class ShaderEntry
        {
            public string unityShader;
            public string engineShader;
            public string engineSkinnedShader;
            // When true, the shader uses non-PBR custom uniforms (slot names are engine
            // uniform names, not glTF PBR roles). The exporter routes these to a separate
            // material .hzt instead of the GLB-embedded PBR path.
            public bool customShader;
            // Set on shaders whose Lit Master Node has SurfaceType=Transparent.
            // Drives MaterialBlobAsset.is_transparent (routes the draw into the
            // transparent batch after scene_color copy + depth read-only). Blend
            // mode is separate — see `blend` below.
            public bool isTransparent;
            // Optional explicit pipeline blend state. When omitted (null), the
            // cooker defaults isTransparent=true → standard alpha blend
            // (SRC_ALPHA/ONE_MINUS_SRC_ALPHA), isTransparent=false → disabled.
            // Set explicitly for manual-composite shaders (ONE/ZERO) or
            // additive (ONE/ONE).
            public BlendState blend;
            public List<PropertyMap> properties;
            // Optional per-material shadow caster. When set, the cooked
            // material gets a shadow_shader + shadow_keywords block so the
            // engine builds a dedicated shadow pipeline (e.g. alpha-clipped
            // foliage). Omit entirely to fall back to the engine's global
            // standard_shadow pipeline.
            public string shadowShader;
            public List<string> shadowKeywords;
            // Shader-level cull default; per-material `_Cull` wins if present.
            public string cullMode;
        }

        /// <summary>Per-shader override for MaterialBlobAsset.blend (GpuBlendState).
        /// Field names match the engine struct exactly so they round-trip through
        /// the cooker. Factor/op enums use the engine token names without the
        /// `GPU_BLEND_` prefix (e.g. "SRC_ALPHA", "ONE_MINUS_SRC_ALPHA", "ADD").</summary>
        [System.Serializable]
        public class BlendState
        {
            public bool enabled;
            public string src_factor;
            public string dst_factor;
            public string op;
            public string src_factor_alpha;
            public string dst_factor_alpha;
            public string op_alpha;
        }

        /// <summary>
        /// Maps a Unity ShaderGraph CustomFunctionNode (identified by its
        /// m_FunctionName, e.g. "MainLight") onto an engine-side helper that
        /// returns a struct. The exporter emits
        ///   <structType> _tN = <engineCall>(args…);
        /// and projects each of the node's output slots onto the struct's
        /// fields. `structFields` lists the field names in slot-OutputName
        /// (snake-cased) form, used only for validation.
        /// </summary>
        [System.Serializable]
        public class CustomFunctionMap
        {
            public string name;            // Unity function name (m_FunctionName)
            public string engineCall;      // GLSL function the engine provides
            public string structType;      // GLSL struct returned by engineCall
            public List<string> structFields;  // snake_case field names
            // Optional GLSL definition emitted into the generated shader, for functions the
            // engine does not provide. Lines are joined with newlines (JSON has no block strings).
            public List<string> glsl;
            // Texture-typed output fields with no engine texture behind them; sampling one
            // yields the default white texture instead of a binding.
            public List<string> whiteTextureFields;
            // Outputs sourced from Shader.SetGlobal* values rather than computed. They are
            // scene constants, so the exporter reads them once and binds them per material.
            public List<GlobalMap> globals;
        }

        [System.Serializable]
        public class GlobalMap
        {
            public string field;   // custom-function output, snake_cased
            public string unity;   // Unity global name, e.g. "_gEastColor"
            public string slot;    // MaterialFS field the value binds to
            public string kind;    // color4 | float | texture_cube
        }

        [System.Serializable]
        class RawConfig
        {
            public string @default;
            public List<ShaderEntry> shaders;
            public List<CustomFunctionMap> customFunctions;
        }

        // ---- Loader (reloads automatically when the JSON's mtime changes) ----
        static Dictionary<string, ShaderEntry> s_byShader;
        static Dictionary<string, CustomFunctionMap> s_customFunctions;
        static ShaderEntry s_default;
        static System.DateTime s_mtime;

        /// <summary>Path of a sibling config file, for tools that write one.</summary>
        public static string ConfigPathFor(string filename) => GetConfigPath(filename);

        static string GetConfigPath(string filename)
        {
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(HZShaderMap).Assembly);
            return pkg != null
                ? Path.Combine(pkg.resolvedPath, "Editor", filename)
                : Path.GetFullPath($"Assets/Editor/HZExporter/{filename}");
        }

        static void EnsureLoaded()
        {
            string path = GetConfigPath("shader_mapping.json");
            bool exists = File.Exists(path);
            System.DateTime mtime = exists ? File.GetLastWriteTimeUtc(path) : System.DateTime.MinValue;
            if (s_byShader != null && mtime == s_mtime) return;

            s_byShader = new Dictionary<string, ShaderEntry>();
            s_customFunctions = new Dictionary<string, CustomFunctionMap>();
            s_mtime = mtime;
            if (!exists)
            {
                Debug.LogWarning($"[HZShaderMap] config not found at {path}; all materials will fall back to empty mapping.");
                s_default = new ShaderEntry { properties = new List<PropertyMap>() };
                return;
            }
            string text = File.ReadAllText(path);
            var raw = JsonUtility.FromJson<RawConfig>(text);
            if (raw?.shaders != null)
            {
                foreach (var e in raw.shaders)
                {
                    if (!string.IsNullOrEmpty(e.unityShader))
                        s_byShader[e.unityShader] = e;
                }
            }
            if (raw?.customFunctions != null)
            {
                foreach (var f in raw.customFunctions)
                {
                    if (!string.IsNullOrEmpty(f.name)) s_customFunctions[f.name] = f;
                }
            }
            if (!string.IsNullOrEmpty(raw?.@default) && s_byShader.TryGetValue(raw.@default, out var def))
                s_default = def;
            else
                s_default = new ShaderEntry { properties = new List<PropertyMap>() };
        }

        /// <summary>Lookup an engine helper mapping for a Unity custom-function name. Null if unmapped.</summary>
        public static CustomFunctionMap LookupCustomFunction(string functionName)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(functionName)) return null;
            return s_customFunctions.TryGetValue(functionName, out var m) ? m : null;
        }

        /// <summary>Force a reload on the next lookup.</summary>
        public static void Reload() { s_byShader = null; s_customFunctions = null; s_default = null; s_mtime = System.DateTime.MinValue; s_cubemaps.Clear(); s_overrides.Clear(); }

        // One equirect .hdr per distinct source cubemap per editor session: the blit+readback
        // is the expensive part and hundreds of materials share the same handful of cubemaps.
        static readonly Dictionary<int, string> s_cubemaps = new Dictionary<int, string>();

        /// <summary>Write a Unity cubemap out as the equirect .hdr + TextureBlobAsset .hzt pair the
        /// engine's cube texture cook consumes, and return the engine asset path of the .hzt.
        /// Returns null when the texture is absent or is not cube-dimension.</summary>
        static string ExportCubemapAsset(Texture cube, string fallbackName)
        {
            if (cube == null) return null;
            if (cube.dimension != UnityEngine.Rendering.TextureDimension.Cube)
            {
                Debug.LogWarning($"[HZShaderMap] '{cube.name}' bound to a cubemap slot is {cube.dimension}, not Cube — skipping");
                return null;
            }
            int key = cube.GetInstanceID();
            if (s_cubemaps.TryGetValue(key, out var cached)) return cached;

            var cfg = HZExporter.HZExporterConfig.Instance;
            string name = HZGlbWriter.SanitizeName(string.IsNullOrEmpty(cube.name) ? fallbackName : cube.name);
            string dir = cfg.ResolvedOutputDir() + "/cubemaps";
            string enginePath = cfg.EngineAssetPrefix() + "/cubemaps/" + name;

            // face_size is fixed at ENV_CUBEMAP_FACE_SIZE on the engine side, so the equirect only
            // needs enough resolution to feed it: width = 4 * face_size is lossless for that cook.
            if (!HZSkyboxExporter.ExportCubemap(cube, dir + "/" + name + ".hdr", 1024))
            {
                s_cubemaps[key] = null;
                return null;
            }

            var hzt = new StringBuilder();
            hzt.AppendLine("type TextureBlobAsset");
            hzt.AppendLine("version 21");
            hzt.AppendLine("---");
            hzt.AppendLine("image \"" + enginePath + "\" u8");
            hzt.AppendLine("format TEX_FORMAT_RAW");
            hzt.AppendLine("width 0");
            hzt.AppendLine("height 0");
            hzt.AppendLine("is_srgb false");
            hzt.AppendLine("has_alpha false");
            hzt.AppendLine("has_alpha_cutoff false");
            hzt.AppendLine("max_size 0");
            hzt.AppendLine("is_cubemap true");
            File.WriteAllText(dir + "/" + name + ".hzt", hzt.ToString());

            string assetPath = enginePath + ".hzt";
            s_cubemaps[key] = assetPath;
            Debug.Log($"[HZShaderMap] exported cubemap '{cube.name}' → {assetPath}");
            return assetPath;
        }

        /// <summary>All Unity shader names present in shader_mapping.json.</summary>
        public static IEnumerable<string> MappedShaderNames() { EnsureLoaded(); return s_byShader.Keys; }

        /// <summary>Mapping entry for a Unity shader name, or null when unmapped.</summary>
        public static ShaderEntry LookupByName(string unityShaderName)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(unityShaderName)) return null;
            return s_byShader.TryGetValue(unityShaderName, out var e) ? e : null;
        }

        /// <summary>Lookup the mapping entry for a material's shader, falling back to the default.
        /// A mapped shader whose translation failed has no .hz.glsl on disk; pointing a material at
        /// it would cook to a dangling shader reference, so those fall back too.</summary>
        public static ShaderEntry Lookup(Material mat)
        {
            EnsureLoaded();
            if (mat != null && mat.shader != null && s_byShader.TryGetValue(mat.shader.name, out var e))
                return EngineShaderExists(e) ? e : s_default;
            return s_default;
        }

        /// <summary>The material property to read for this mapping entry: its declared name when the
        /// shader has it, else the texture property holding the same asset guid. null = skip.</summary>
        static string ResolveTexturePropertyName(Material mat, PropertyMap p)
        {
            if (mat.HasProperty(p.unity)) return p.unity;
            if (string.IsNullOrEmpty(p.guid) || mat.shader == null) return null;
            int n = mat.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (mat.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                string name = mat.shader.GetPropertyName(i);
                var tex = mat.GetTexture(name);
                if (tex == null) continue;
                string path = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(path)) continue;
                if (AssetDatabase.AssetPathToGUID(path) == p.guid) return name;
            }
            return null;
        }

        static readonly HashSet<string> s_missingReported = new HashSet<string>();

        static bool EngineShaderExists(ShaderEntry e)
        {
            if (e == null || string.IsNullOrEmpty(e.engineShader)) return true;
            string root = HZExporterConfig.Instance.ResolvedShaderRootDir();
            if (string.IsNullOrEmpty(root)) return true;   // staging-only run; nothing to verify against
            const string prefix = "/shaders/";
            if (!e.engineShader.StartsWith(prefix, System.StringComparison.Ordinal)) return true;
            string full = Path.Combine(root, e.engineShader.Substring(prefix.Length));
            if (File.Exists(full)) return true;
            if (s_missingReported.Add(e.unityShader))
                Debug.LogWarning($"[HZShaderMap] '{e.unityShader}' is mapped to {e.engineShader}, which was not "
                                 + "translated — materials fall back to the default shader.");
            return false;
        }

        // ---- Material → factors + textures ----
        public struct Factors
        {
            public Color baseColor;     // color4 (slot "baseColor")
            public float metallic;      // float  (slot "metallic")
            public float roughness;     // float  (slot "roughness", via 1-smoothness)
            public Color emissive;      // color3 (slot "emissive")
        }

        /// <summary>Record describing an embedded texture. role: 0=albedo, 1=normal, 2=orm, 3=emissive, -1=non-PBR (custom).</summary>
        public struct ImageRecord
        {
            public int off;
            public int size;
            public string name;
            public int role;
        }

        /// <summary>One property in a custom-shader material — destined for the GLB's
        /// `materials[i].extras.hz_material.properties` block.</summary>
        public struct CustomProperty
        {
            public string slot;        // engine uniform name (snake_case)
            public string kind;        // "float" | "vec2" | "vec3" | "color4" | "texture"
            public float f;
            public Vector2 v2;
            public Vector3 v3;
            public Color c4;
            public int textureIndex;   // index into glTF textures[] (= imageEntries index)
            // MAT_PROP_TEXTURE only, and it wins over textureIndex: an engine asset path for a
            // texture the glb does not own (cubemaps cook from a standalone equirect .hdr).
            public string texturePath;
            public bool textureLinear;  // Unity sRGBTexture == false: sampled as data
        }

        /// <summary>The full custom-shader material payload that gets serialized into the GLB
        /// material's `extras.hz_material` block. Field shape mirrors `MaterialBlobAsset`.</summary>
        public class CustomMaterial
        {
            public string shaderPath;                  // engine shader path (e.g. shaders/lofi_basic_material.hza)
            public bool isTransparent;                 // mirrors MaterialBlobAsset.is_transparent
            public BlendState blend;                   // optional, null → cooker default from isTransparent
            public List<CustomProperty> properties = new List<CustomProperty>();
            // Engine MaterialKeywordBlob list. Populated from the material's
            // shadergraph keyword catalog at export time. Boolean keywords
            // appear only when ON; enum keywords always appear with their
            // currently-selected entry.
            public List<HZKeywordReader.KeywordKV> keywords = new List<HZKeywordReader.KeywordKV>();
            // Optional per-material shadow caster. When shadowShaderPath is
            // null/empty the engine falls back to the global standard_shadow
            // pipeline. Most often shadowShaderPath == shaderPath with a
            // different keyword set (e.g. ["HZ_SHADOW_PASS"]).
            public string shadowShaderPath;
            public List<HZKeywordReader.KeywordKV> shadowKeywords;
            // Resolved GpuCullMode token (e.g. "GPU_CULL_BACK"). Always populated.
            public string cullMode;
        }

        // Unity URP `_Cull` (0=Off, 1=Front, 2=Back) → engine GpuCullMode token.
        // Material `_Cull` wins when present; else shader_mapping `cullMode`; else GPU_CULL_BACK.
        static string ResolveCullMode(Material mat, ShaderEntry entry)
        {
            if (mat != null && mat.HasProperty("_Cull"))
            {
                int v = (int)mat.GetFloat("_Cull");
                if (v == 0) return "GPU_CULL_NONE";
                if (v == 1) return "GPU_CULL_FRONT";
                if (v == 2) return "GPU_CULL_BACK";
            }
            if (entry != null && !string.IsNullOrEmpty(entry.cullMode)) return entry.cullMode;
            return "GPU_CULL_BACK";
        }

        // Slot → PBR role. Expanded later when custom engine shaders need non-PBR routing.
        static int SlotToRole(string slot)
        {
            switch (slot)
            {
                case "albedoTex":   return 0;
                case "normalTex":   return 1;
                case "ormTex":      return 2;
                case "emissiveTex": return 3;
                default:            return -1;
            }
        }

        // Read a material color property in the engine's expected space (sRGB — it
        // applies srgb->linear internally). Material.GetColor returns LDR Color props
        // already in sRGB (the inspector/gamma value), so those pass through. HDR
        // color props come out of Unity in LINEAR, so re-encode those to sRGB. (Unity
        // is not consistent about which colors it gamma-encodes.)
        // Material.GetFloat/GetColor return a hardcoded fallback (0 / white) for a property the
        // material stores no override for, while the GPU is fed the shader's own default. Only
        // m_SavedProperties says which is which.
        static readonly Dictionary<int, HashSet<string>> s_overrides = new Dictionary<int, HashSet<string>>();

        static HashSet<string> StoredProperties(Material mat)
        {
            int key = mat.GetInstanceID();
            if (s_overrides.TryGetValue(key, out var cached)) return cached;
            var set = new HashSet<string>();
            var so = new SerializedObject(mat);
            var saved = so.FindProperty("m_SavedProperties");
            if (saved != null)
            {
                string[] buckets = { "m_TexEnvs", "m_Floats", "m_Colors", "m_Ints" };
                foreach (var b in buckets)
                {
                    var arr = saved.FindPropertyRelative(b);
                    for (int i = 0; arr != null && i < arr.arraySize; i++)
                    {
                        var name = arr.GetArrayElementAtIndex(i).FindPropertyRelative("first");
                        if (name != null && !string.IsNullOrEmpty(name.stringValue)) set.Add(name.stringValue);
                    }
                }
            }
            s_overrides[key] = set;
            return set;
        }

        /// <summary>The float the GPU actually receives: the material's override when it has one,
        /// otherwise the shader property's own default.</summary>
        static float ReadFloat(Material mat, string prop)
        {
            if (StoredProperties(mat).Contains(prop)) return mat.GetFloat(prop);
            int idx = mat.shader != null ? mat.shader.FindPropertyIndex(prop) : -1;
            if (idx < 0) return mat.GetFloat(prop);
            var t = mat.shader.GetPropertyType(idx);
            if (t == UnityEngine.Rendering.ShaderPropertyType.Range ||
                t == UnityEngine.Rendering.ShaderPropertyType.Float)
                return mat.shader.GetPropertyDefaultFloatValue(idx);
            return mat.shader.GetPropertyDefaultVectorValue(idx).x;
        }

        /// <summary>The vector the GPU actually receives (see <see cref="ReadFloat"/>).</summary>
        static Vector4 ReadVector(Material mat, string prop)
        {
            if (StoredProperties(mat).Contains(prop)) return mat.GetVector(prop);
            int idx = mat.shader != null ? mat.shader.FindPropertyIndex(prop) : -1;
            if (idx < 0) return mat.GetVector(prop);
            return mat.shader.GetPropertyDefaultVectorValue(idx);
        }

        /// <summary>Unity's own sRGB import flag for a texture asset. A packed data texture
        /// (normal.xy + AO + mask) is linear without being a two-channel normal map, so the
        /// property kind cannot stand in for this.</summary>
        static bool IsLinearTexture(Texture tex)
        {
            if (tex == null) return false;
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return false;
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            return ti != null && !ti.sRGBTexture;
        }

        static Color ReadColor(Material mat, string prop)
        {
            Vector4 v = ReadVector(mat, prop);
            Color c = new Color(v.x, v.y, v.z, v.w);
            if (IsHdrColorProperty(mat, prop))
                c = new Color(LinearToSrgbF(c.r), LinearToSrgbF(c.g), LinearToSrgbF(c.b), c.a);
            return c;
        }

        static bool IsHdrColorProperty(Material mat, string prop)
        {
            var sh = mat != null ? mat.shader : null;
            if (sh == null) return false;
            int idx = sh.FindPropertyIndex(prop);
            if (idx < 0) return false;
            return (sh.GetPropertyFlags(idx) & UnityEngine.Rendering.ShaderPropertyFlags.HDR) != 0;
        }

        static float LinearToSrgbF(float x)
        {
            if (x <= 0.0031308f) return x * 12.92f;
            return 1.055f * Mathf.Pow(x, 1f / 2.4f) - 0.055f;
        }

        /// <summary>
        /// Embed all textures referenced by a material per its shader mapping, plus read factor
        /// values. Invokes <paramref name="onImage"/> for every PNG embedded (caller records it).
        /// Handles the ORM special case (two Unity textures → one combined ORM PNG).
        /// </summary>
        public static Factors EmbedMaterial(
            Material mat,
            MemoryStream bin,
            System.Action<ImageRecord> onImage)
        {
            var f = new Factors
            {
                baseColor = Color.white,
                metallic = 0f,
                roughness = 0.5f,  // 1 - default smoothness 0.5
                emissive = Color.black,
            };
            if (mat == null) return f;

            var entry = Lookup(mat);
            var props = entry.properties ?? new List<PropertyMap>();

            // Track which slots are already filled so the first matching Unity property wins
            // (matches old behavior where e.g. _BaseColor wins over _Color).
            var filled = new HashSet<string>();

            Texture2D ormMetalSmooth = null;
            Texture2D ormOcclusion = null;

            foreach (var p in props)
            {
                if (string.IsNullOrEmpty(p.unity) || string.IsNullOrEmpty(p.slot) || string.IsNullOrEmpty(p.kind)) continue;
                if (!mat.HasProperty(p.unity)) continue;
                if (filled.Contains(p.slot)) continue;

                switch (p.kind)
                {
                    case "color4":
                    {
                        if (p.slot == "baseColor") { f.baseColor = ReadColor(mat, p.unity); filled.Add(p.slot); }
                        break;
                    }
                    case "color3":
                    {
                        if (p.slot == "emissive") { f.emissive = ReadColor(mat, p.unity); filled.Add(p.slot); }
                        break;
                    }
                    case "float":
                    {
                        float v = mat.GetFloat(p.unity);
                        if (p.slot == "metallic")  { f.metallic = v; filled.Add(p.slot); }
                        else if (p.slot == "roughness") { f.roughness = v; filled.Add(p.slot); }
                        break;
                    }
                    case "float_oneMinus":
                    {
                        float v = mat.GetFloat(p.unity);
                        if (p.slot == "roughness") { f.roughness = 1f - v; filled.Add(p.slot); }
                        else if (p.slot == "metallic") { f.metallic = 1f - v; filled.Add(p.slot); }
                        break;
                    }
                    case "texture":
                    case "texture_normal":
                    {
                        var tex = mat.GetTexture(p.unity) as Texture2D;
                        if (tex == null) break;
                        int role = SlotToRole(p.slot);
                        if (role < 0) break;
                        bool isNormal = p.kind == "texture_normal";
                        bool linear = isNormal || IsLinearTexture(tex);
                        byte[] png = HZGlbWriter.EncodeAndCachePng(tex, isNormal, linear);
                        HZGlbWriter.PadTo4(bin);
                        int off = (int)bin.Position;
                        bin.Write(png, 0, png.Length);
                        HZGlbWriter.PadTo4(bin);
                        onImage(new ImageRecord { off = off, size = png.Length, name = HZGlbWriter.SanitizeName(tex.name ?? $"tex_role{role}"), role = role });
                        filled.Add(p.slot);
                        break;
                    }
                    case "texture_orm_metalsmooth":
                    {
                        if (ormMetalSmooth == null) ormMetalSmooth = mat.GetTexture(p.unity) as Texture2D;
                        break;
                    }
                    case "texture_orm_occlusion":
                    {
                        if (ormOcclusion == null) ormOcclusion = mat.GetTexture(p.unity) as Texture2D;
                        break;
                    }
                }
            }

            // ORM combining — only if at least one input texture was found.
            if (!filled.Contains("ormTex") && (ormMetalSmooth != null || ormOcclusion != null))
            {
                byte[] png = HZGlbWriter.EncodeAndCacheOrmPng(ormMetalSmooth, ormOcclusion);
                if (png != null)
                {
                    string ormName = HZGlbWriter.SanitizeName((mat.name ?? "material") + "_ORM");
                    HZGlbWriter.PadTo4(bin);
                    int off = (int)bin.Position;
                    bin.Write(png, 0, png.Length);
                    HZGlbWriter.PadTo4(bin);
                    onImage(new ImageRecord { off = off, size = png.Length, name = ormName, role = 2 });
                    filled.Add("ormTex");
                }
            }

            return f;
        }

        // ---- MToon (VRM 1.0) -------------------------------------------------
        // The engine auto-detects MToon from the GLB material's VRMC_materials_mtoon
        // extension (vrm_mtoon.c) and switches that material to the MToon shader,
        // name-filling shade/matcap/rim from the extension. Base color / normal /
        // emissive come from the standard glTF material (EmbedMaterial), so MToon
        // materials run BOTH EmbedMaterial (PBR fields) and EmbedMToon (the extras).

        public static bool IsMToon(Material mat) =>
            mat != null && mat.shader != null && mat.shader.name.Contains("MToon10");

        public class MToonExtras
        {
            public Color shadeColor = Color.white;       // shadeColorFactor (rgb)
            public float shadingShift = -0.05f;          // shadingShiftFactor
            public float shadingToony = 0.9f;            // shadingToonyFactor
            public float giEqualization = 0.9f;          // giEqualizationFactor
            public Color matcapFactor = Color.black;     // matcapFactor (rgb)
            public Color rimColor = Color.black;          // parametricRimColorFactor (rgb)
            public float rimFresnelPower = 5f;           // parametricRimFresnelPowerFactor
            public float rimLift = 0f;                   // parametricRimLiftFactor
            public float rimLightingMix = 1f;            // rimLightingMixFactor
            public bool shadeUsesAlbedo = false;         // shadeMultiplyTexture == baseColorTexture
            public int alphaMode = 0;                    // 0 opaque, 1 mask, 2 blend
            public float alphaCutoff = 0.5f;
            public bool doubleSided = false;
            // non-spec extensions (forked MToon10): Highlight third tone + Face Shading
            public Color highlightColor = Color.white;   // mtoon_ext.highlightColorFactor (rgb)
            public float highlightShift = -1f;           // mtoon_ext.highlightShiftFactor
            public float highlightToony = 0.95f;         // mtoon_ext.highlightToonyFactor
            public float highlightEnabled = 0f;          // mtoon_ext.highlightEnabledFactor
            public float highlightUseTexture = 0f;       // mtoon_ext.highlightUseTexture
            public float faceNormalBlend = 0f;           // mtoon_ext.faceShadingNormalBlend
            public Vector3 faceCenter = Vector3.zero;    // mtoon_ext.faceShadingCenter
            // outline (VRM mtoon spec)
            public Color outlineColor = Color.black;     // outlineColorFactor (rgb)
            public float outlineWidth = 0f;              // outlineWidthFactor
            public int outlineMode = 0;                  // outlineWidthMode: 0 none, 1 world, 2 screen
            public float outlineLightingMix = 1f;        // outlineLightingMixFactor
            // non-spec extensions (forked MToon10): Blush and Nervous tints
            public Color blushTint = Color.white;        // mtoon_ext.blushTintFactor (rgb)
            public Color nervousTint = Color.white;      // mtoon_ext.nervousTintFactor (rgb)
            public float blushValue = 0f;                // mtoon_ext.blushValueFactor
            public float nervousValue = 0f;               // mtoon_ext.nervousValueFactor
            // non-spec extensions: StarEyes (RGBA decal) + Teary (UV-warp cry)
            public Color starEyesTint = Color.white;      // mtoon_ext.starEyesTintFactor (rgb)
            public float starEyesValue = 0f;             // mtoon_ext.starEyesValueFactor
            public float tearyEnabled = 0f;              // mtoon_ext.tearyEnabled
            public float tearyValue = 1f;                // mtoon_ext.tearyValueFactor
            public float tearyFrequency = 3f;            // mtoon_ext.tearyFrequencyFactor
            public float tearySpeed = 0.3f;              // mtoon_ext.tearySpeedFactor
            public float tearyAmplitude = 0.008f;        // mtoon_ext.tearyAmplitudeFactor
            public float rimVertexColorMask = 0f;         // mtoon_ext.rimVertexColorMaskEnabled
            public float outlineVertexColorMask = 0f;     // mtoon_ext.outlineVertexColorMaskEnabled
        }

        /// <summary>Read MToon10 params and embed the MToon-specific textures (shade role 4,
        /// matcap role 5, rim role 6). When the shade texture is the same asset as the base
        /// albedo, no new image is embedded — shadeUsesAlbedo flags the writer to reference the
        /// existing baseColorTexture index instead (matches the source VRoid).</summary>
        public static MToonExtras EmbedMToon(Material mat, MemoryStream bin, System.Action<ImageRecord> onImage)
        {
            var e = new MToonExtras();
            if (mat == null) return e;

            // every @color engine prop -> sRGB so the engine's srgb->linear recovers
            // the authored color (shade/rim/matcap/highlight are all @color now).
            if (mat.HasProperty("_ShadeColor")) e.shadeColor = ReadColor(mat, "_ShadeColor");
            if (mat.HasProperty("_ShadingShiftFactor")) e.shadingShift = mat.GetFloat("_ShadingShiftFactor");
            if (mat.HasProperty("_ShadingToonyFactor")) e.shadingToony = mat.GetFloat("_ShadingToonyFactor");
            if (mat.HasProperty("_GiEqualization")) e.giEqualization = mat.GetFloat("_GiEqualization");
            if (mat.HasProperty("_MatcapColor")) e.matcapFactor = ReadColor(mat, "_MatcapColor");
            if (mat.HasProperty("_RimColor")) e.rimColor = ReadColor(mat, "_RimColor");
            if (mat.HasProperty("_RimFresnelPower")) e.rimFresnelPower = mat.GetFloat("_RimFresnelPower");
            if (mat.HasProperty("_RimLift")) e.rimLift = mat.GetFloat("_RimLift");
            if (mat.HasProperty("_RimLightingMix")) e.rimLightingMix = mat.GetFloat("_RimLightingMix");
            if (mat.HasProperty("_AlphaMode")) e.alphaMode = Mathf.RoundToInt(mat.GetFloat("_AlphaMode"));
            if (mat.HasProperty("_Cutoff")) e.alphaCutoff = mat.GetFloat("_Cutoff");
            if (mat.HasProperty("_DoubleSided")) e.doubleSided = mat.GetFloat("_DoubleSided") > 0.5f;
            if (mat.HasProperty("_HighlightColor")) e.highlightColor = ReadColor(mat, "_HighlightColor");
            if (mat.HasProperty("_HighlightShift")) e.highlightShift = mat.GetFloat("_HighlightShift");
            if (mat.HasProperty("_HighlightToony")) e.highlightToony = mat.GetFloat("_HighlightToony");
            if (mat.HasProperty("_HighlightEnabled")) e.highlightEnabled = mat.GetFloat("_HighlightEnabled");
            if (mat.HasProperty("_HighlightUseTexture")) e.highlightUseTexture = mat.GetFloat("_HighlightUseTexture");
            if (mat.HasProperty("_FaceShadingNormalBlend")) e.faceNormalBlend = mat.GetFloat("_FaceShadingNormalBlend");
            if (mat.HasProperty("_OutlineColor")) e.outlineColor = ReadColor(mat, "_OutlineColor");
            if (mat.HasProperty("_OutlineWidth")) e.outlineWidth = mat.GetFloat("_OutlineWidth");
            if (mat.HasProperty("_OutlineWidthMode")) e.outlineMode = Mathf.RoundToInt(mat.GetFloat("_OutlineWidthMode"));
            if (mat.HasProperty("_OutlineLightingMix")) e.outlineLightingMix = mat.GetFloat("_OutlineLightingMix");
            if (mat.HasProperty("_BlushTint")) e.blushTint = ReadColor(mat, "_BlushTint");
            if (mat.HasProperty("_NervousTint")) e.nervousTint = ReadColor(mat, "_NervousTint");
            if (mat.HasProperty("_BlushValue")) e.blushValue = mat.GetFloat("_BlushValue");
            if (mat.HasProperty("_NervousValue")) e.nervousValue = mat.GetFloat("_NervousValue");
            if (mat.HasProperty("_StarEyesTint")) e.starEyesTint = ReadColor(mat, "_StarEyesTint");
            if (mat.HasProperty("_StarEyesValue")) e.starEyesValue = mat.GetFloat("_StarEyesValue");
            if (mat.HasProperty("_TearyEnabled")) e.tearyEnabled = mat.GetFloat("_TearyEnabled");
            if (mat.HasProperty("_TearyValue")) e.tearyValue = mat.GetFloat("_TearyValue");
            if (mat.HasProperty("_TearyFrequency")) e.tearyFrequency = mat.GetFloat("_TearyFrequency");
            if (mat.HasProperty("_TearySpeed")) e.tearySpeed = mat.GetFloat("_TearySpeed");
            if (mat.HasProperty("_TearyAmplitude")) e.tearyAmplitude = mat.GetFloat("_TearyAmplitude");
            if (mat.HasProperty("_RimVertexColorMask")) e.rimVertexColorMask = mat.GetFloat("_RimVertexColorMask");
            if (mat.HasProperty("_OutlineVertexColorMask")) e.outlineVertexColorMask = mat.GetFloat("_OutlineVertexColorMask");
            // object-space position: must use the SAME axis conversion as the
            // vertices the engine subtracts it from (HZGlbWriter.MirrorX).
            if (mat.HasProperty("_FaceShadingCenter")) { var c = mat.GetVector("_FaceShadingCenter"); e.faceCenter = HZGlbWriter.MirrorX(new Vector3(c.x, c.y, c.z)); }

            Texture2D baseTex = (mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null) as Texture2D;
            if (baseTex == null && mat.HasProperty("_BaseMap")) baseTex = mat.GetTexture("_BaseMap") as Texture2D;

            var shadeTex = (mat.HasProperty("_ShadeTex") ? mat.GetTexture("_ShadeTex") : null) as Texture2D;
            if (shadeTex != null)
            {
                if (shadeTex == baseTex) e.shadeUsesAlbedo = true;
                else EmbedRoleTexture(shadeTex, false, 4, bin, onImage);
            }
            var matcapTex = (mat.HasProperty("_MatcapTex") ? mat.GetTexture("_MatcapTex") : null) as Texture2D;
            if (matcapTex != null) EmbedRoleTexture(matcapTex, false, 5, bin, onImage);
            var rimTex = (mat.HasProperty("_RimTex") ? mat.GetTexture("_RimTex") : null) as Texture2D;
            if (rimTex != null) EmbedRoleTexture(rimTex, false, 6, bin, onImage);
            var highlightTex = (mat.HasProperty("_HighlightTex") ? mat.GetTexture("_HighlightTex") : null) as Texture2D;
            if (highlightTex != null) EmbedRoleTexture(highlightTex, false, 7, bin, onImage);
            var blushTex = (mat.HasProperty("_BlushMask") ? mat.GetTexture("_BlushMask") : null) as Texture2D;
            if (blushTex != null) EmbedRoleTexture(blushTex, false, 8, bin, onImage);
            var nervousTex = (mat.HasProperty("_NervousMask") ? mat.GetTexture("_NervousMask") : null) as Texture2D;
            if (nervousTex != null) EmbedRoleTexture(nervousTex, false, 9, bin, onImage);
            var starEyesTex = (mat.HasProperty("_StarEyesMask") ? mat.GetTexture("_StarEyesMask") : null) as Texture2D;
            if (starEyesTex != null) EmbedRoleTexture(starEyesTex, false, 10, bin, onImage);
            var tearyNoiseTex = (mat.HasProperty("_TearyNoiseTex") ? mat.GetTexture("_TearyNoiseTex") : null) as Texture2D;
            if (tearyNoiseTex != null) EmbedRoleTexture(tearyNoiseTex, false, 11, bin, onImage);
            var tearyMaskTex = (mat.HasProperty("_TearyMask") ? mat.GetTexture("_TearyMask") : null) as Texture2D;
            if (tearyMaskTex != null) EmbedRoleTexture(tearyMaskTex, false, 12, bin, onImage);

            return e;
        }

        static void EmbedRoleTexture(Texture2D tex, bool isNormal, int role, MemoryStream bin, System.Action<ImageRecord> onImage)
        {
            byte[] png = HZGlbWriter.EncodeAndCachePng(tex, isNormal);
            HZGlbWriter.PadTo4(bin);
            int off = (int)bin.Position;
            bin.Write(png, 0, png.Length);
            HZGlbWriter.PadTo4(bin);
            onImage(new ImageRecord { off = off, size = png.Length, name = HZGlbWriter.SanitizeName(tex.name ?? $"tex_role{role}"), role = role });
        }

        /// <summary>Walk a custom-shader material's mapping properties, embed each referenced
        /// texture in the GLB binary chunk via `onImage` (which returns the assigned glTF
        /// texture index), and return a `CustomMaterial` describing all properties for the
        /// `extras.hz_material` block. Returns null if the material's shader entry is not
        /// flagged `customShader: true` in shader_mapping.json.</summary>
        public static CustomMaterial EmbedCustomMaterial(
            Material mat,
            bool skinned,
            MemoryStream bin,
            System.Func<ImageRecord, int> onImage)
        {
            EnsureLoaded();
            if (mat == null) return null;
            var entry = Lookup(mat);
            if (entry == null || !entry.customShader) return null;

            var cm = new CustomMaterial
            {
                shaderPath = (skinned && !string.IsNullOrEmpty(entry.engineSkinnedShader))
                             ? entry.engineSkinnedShader : entry.engineShader,
                isTransparent = entry.isTransparent,
                blend = entry.blend,
                properties = new List<CustomProperty>(),
                keywords = HZKeywordReader.ReadEnabled(mat),
                cullMode = ResolveCullMode(mat, entry),
            };

            // Optional shadow caster — copy declaratively from shader_mapping.json.
            // When the entry omits shadowShader, the cooked material gets no
            // shadow_shader block and the engine uses standard_shadow.
            if (!string.IsNullOrEmpty(entry.shadowShader))
            {
                cm.shadowShaderPath = entry.shadowShader;
                cm.shadowKeywords = new List<HZKeywordReader.KeywordKV>();
                if (entry.shadowKeywords != null)
                    foreach (var name in entry.shadowKeywords)
                        cm.shadowKeywords.Add(new HZKeywordReader.KeywordKV { name = name, value = "" });
                // the shadow caster clips exactly like the forward pass
                foreach (var kw in cm.keywords)
                    if (kw.name == "_ALPHATEST_ON") cm.shadowKeywords.Add(kw);
            }

            foreach (var p in entry.properties ?? new List<PropertyMap>())
            {
                if (string.IsNullOrEmpty(p.unity) || string.IsNullOrEmpty(p.slot) || string.IsNullOrEmpty(p.kind)) continue;
                // globals live on Shader, not the material, so HasProperty says no
                if (p.kind.StartsWith("global_"))
                {
                    switch (p.kind)
                    {
                        case "global_float":
                            cm.properties.Add(new CustomProperty { slot = p.slot, kind = "float", f = Shader.GetGlobalFloat(p.unity) });
                            break;
                        case "global_color4":
                            cm.properties.Add(new CustomProperty { slot = p.slot, kind = "color4", c4 = Shader.GetGlobalColor(p.unity) });
                            break;
                        case "global_texture_cube":
                        {
                            string cubePath = ExportCubemapAsset(Shader.GetGlobalTexture(p.unity), p.unity);
                            if (cubePath != null)
                                cm.properties.Add(new CustomProperty { slot = p.slot, kind = "texture", texturePath = cubePath });
                            break;
                        }
                        default:
                            Debug.LogWarning($"[HZShaderMap] unrecognized global kind '{p.kind}' for '{p.unity}'");
                            break;
                    }
                    continue;
                }
                string unityName = ResolveTexturePropertyName(mat, p);
                if (unityName == null) continue;
                switch (p.kind)
                {
                    case "float":
                        cm.properties.Add(new CustomProperty { slot = p.slot, kind = "float", f = ReadFloat(mat, unityName) });
                        break;
                    case "vec2":
                    {
                        var v = ReadVector(mat, unityName);
                        cm.properties.Add(new CustomProperty { slot = p.slot, kind = "vec2", v2 = new Vector2(v.x, v.y) });
                        break;
                    }
                    case "vec3":
                    {
                        var v = ReadVector(mat, unityName);
                        cm.properties.Add(new CustomProperty { slot = p.slot, kind = "vec3", v3 = new Vector3(v.x, v.y, v.z) });
                        break;
                    }
                    case "color4":
                        cm.properties.Add(new CustomProperty { slot = p.slot, kind = "color4", c4 = ReadColor(mat, unityName) });
                        break;
                    case "texture":
                    case "texture_normal":
                    {
                        var tex = mat.GetTexture(unityName) as Texture2D;
                        if (tex == null)
                        {
                            Debug.LogWarning($"[HZShaderMap] '{mat.name}' has no Texture2D bound to '{p.unity}' (slot '{p.slot}') — skipping");
                            break;
                        }
                        // Normal maps must be unpacked from Unity's DXT5nm layout
                        // (X in alpha, Y in green) and blitted in linear space —
                        // otherwise sampling returns gamma-corrected garbage and
                        // the shader's [0,1]→[-1,1] decode produces meaningless
                        // perturbations. Plain textures stay sRGB.
                        bool isNormal = p.kind == "texture_normal";
                        bool linear = isNormal || IsLinearTexture(tex);
                        byte[] png = HZGlbWriter.EncodeAndCachePng(tex, isNormal, linear);
                        HZGlbWriter.PadTo4(bin);
                        int off = (int)bin.Position;
                        bin.Write(png, 0, png.Length);
                        HZGlbWriter.PadTo4(bin);
                        int idx = onImage(new ImageRecord {
                            off = off, size = png.Length,
                            name = HZGlbWriter.SanitizeName(tex.name ?? p.slot),
                            role = -1,
                        });
                        cm.properties.Add(new CustomProperty { slot = p.slot, kind = "texture", textureIndex = idx, textureLinear = linear });
                        break;
                    }
                    case "texture_cube":
                    {
                        string cubePath = ExportCubemapAsset(mat.GetTexture(unityName), p.slot);
                        if (cubePath == null)
                        {
                            Debug.LogWarning($"[HZShaderMap] '{mat.name}' has no Cubemap bound to '{p.unity}' (slot '{p.slot}') — skipping");
                            break;
                        }
                        cm.properties.Add(new CustomProperty { slot = p.slot, kind = "texture", texturePath = cubePath });
                        break;
                    }
                    default:
                        Debug.LogWarning($"[HZShaderMap] '{mat.name}' / slot '{p.slot}': unrecognized kind '{p.kind}' — property skipped. Add a handler in EmbedCustomMaterial or fix shader_mapping.json.");
                        break;
                }
            }
            return cm;
        }

        // Engine shader for grass that uses a default/PBR Unity material (no custom mapping).
        const string GRASS_TERRAIN_SHADER = "/shaders/renderer/grass_terrain.hz.glsl";

        /// <summary>
        /// Synthesize a grass_terrain custom material for a default/PBR material on a grass mesh.
        /// The exporter routes default grass materials here (instead of the PBR path) so they
        /// point shader_source at grass_terrain.hz.glsl — the grass analogue of the engine's
        /// standard_static default. grass_terrain's MaterialFS is {albedo, alpha_cutoff,
        /// root_color, tip_color}; only albedo + alpha_cutoff are pulled from Unity. root_color
        /// and tip_color are supplied by the grass system at runtime from the terrain asset's
        /// grass_types, so they're left at struct-default zero here.
        /// </summary>
        public static CustomMaterial EmbedGrassTerrainMaterial(
            Material mat,
            MemoryStream bin,
            System.Func<ImageRecord, int> onImage)
        {
            EnsureLoaded();
            var cm = new CustomMaterial
            {
                shaderPath = GRASS_TERRAIN_SHADER,
                isTransparent = false,            // alpha-cut foliage is opaque (in-shader discard)
                properties = new List<CustomProperty>(),
                // foliage is double-sided; respect the material's _Cull but default to NONE
                cullMode = ResolveCullMode(mat, new ShaderEntry { cullMode = "GPU_CULL_NONE" }),
            };
            if (mat == null) return cm;

            // Albedo: reuse the shader mapping's albedoTex slot to find the Unity texture
            // (same lookup the PBR path uses), then embed it as a custom texture property.
            var entry = Lookup(mat);
            Texture2D albedo = null;
            foreach (var p in entry.properties ?? new List<PropertyMap>())
            {
                if (p.slot != "albedoTex") continue;
                if (string.IsNullOrEmpty(p.unity) || !mat.HasProperty(p.unity)) continue;
                albedo = mat.GetTexture(p.unity) as Texture2D;
                if (albedo != null) break;
            }
            if (albedo != null)
            {
                byte[] png = HZGlbWriter.EncodeAndCachePng(albedo, false);
                HZGlbWriter.PadTo4(bin);
                int off = (int)bin.Position;
                bin.Write(png, 0, png.Length);
                HZGlbWriter.PadTo4(bin);
                int idx = onImage(new ImageRecord {
                    off = off, size = png.Length,
                    name = HZGlbWriter.SanitizeName(albedo.name ?? "albedo"), role = -1 });
                cm.properties.Add(new CustomProperty { slot = "albedo", kind = "texture", textureIndex = idx });
            }
            else
            {
                Debug.LogWarning($"[HZShaderMap] grass material '{mat.name}' (shader '{mat.shader?.name}'): no albedo texture found; grass_terrain material emitted without albedo (engine @white fallback).");
            }

            // alpha_cutoff: Unity's clip threshold; default 0.5 when absent.
            float cutoff = 0.5f;
            if (mat.HasProperty("_Cutoff")) cutoff = mat.GetFloat("_Cutoff");
            else if (mat.HasProperty("_AlphaClipThreshold")) cutoff = mat.GetFloat("_AlphaClipThreshold");
            cm.properties.Add(new CustomProperty { slot = "alpha_cutoff", kind = "float", f = cutoff });

            return cm;
        }
    }
}
