using System;
using System.Collections.Generic;
using System.Globalization;

namespace HZExporter.ShaderGraph
{
    // ===== variants & target ===================================================

    enum SgVariant { Unknown, Unlit, Lit }

    // ===== concrete types =====================================================

    // Engine-side GLSL types we care about. Shader Graph's static slot types map
    // to these directly; DynamicValue/DynamicVector slots resolve to one of
    // these during lowering based on what flows in.
    enum SgType
    {
        Unknown,
        Float,
        Vec2,
        Vec3,
        Vec4,
        Mat4,
        Bool,
        Texture2D,        // sampleable texture (engine binds to a uint handle)
        SamplerState,     // sampler — not yet emitted as a value
    }

    static class SgTypes
    {
        public static string Glsl(SgType t)
        {
            switch (t)
            {
                case SgType.Float: return "float";
                case SgType.Vec2:  return "vec2";
                case SgType.Vec3:  return "vec3";
                case SgType.Vec4:  return "vec4";
                case SgType.Mat4:      return "mat4";
                case SgType.Bool:      return "bool";
                case SgType.Texture2D: return "uint";    // texture handles are uint indices in MaterialFS
            }
            throw new Exception($"no GLSL name for type {t}");
        }

        // Numeric rank for vector promotion. Mat4/Bool are excluded — promotion
        // involving them is handled by callers.
        public static int Rank(SgType t)
        {
            switch (t)
            {
                case SgType.Float: return 1;
                case SgType.Vec2:  return 2;
                case SgType.Vec3:  return 3;
                case SgType.Vec4:  return 4;
            }
            return 0;
        }

        public static SgType FromRank(int r)
        {
            switch (r)
            {
                case 1: return SgType.Float;
                case 2: return SgType.Vec2;
                case 3: return SgType.Vec3;
                case 4: return SgType.Vec4;
            }
            throw new Exception($"no scalar/vector type for rank {r}");
        }

        // Unity Shader Graph promotion rule for arithmetic with dynamic slots:
        // pick the higher-rank vector. Mat4 only valid in matrix-aware ops
        // (handled by the node, not here).
        public static SgType Promote(SgType a, SgType b)
        {
            // Bool is treated as a "rank-zero" type — defer to the other
            // operand. Used by BranchNode where the predicate is bool but
            // the value branches drive the result type.
            if (a == SgType.Bool) return b;
            if (b == SgType.Bool) return a;
            if (a == SgType.Mat4 || b == SgType.Mat4) return SgType.Mat4;
            int ra = Rank(a), rb = Rank(b);
            if (ra == 0 || rb == 0) throw new Exception($"cannot promote {a} and {b}");
            return FromRank(Math.Max(ra, rb));
        }

        // Convert an expression of `from` type into `to` type. Handles the
        // conversions we actually hit; throws on anything novel so we add it
        // explicitly.
        public static string Coerce(string expr, SgType from, SgType to)
        {
            if (from == to) return expr;

            // scalar → vec2/3/4: broadcast
            if (from == SgType.Float)
            {
                switch (to)
                {
                    case SgType.Vec2: return $"vec2({expr})";
                    case SgType.Vec3: return $"vec3({expr})";
                    case SgType.Vec4: return $"vec4({expr})";
                }
            }
            // vec_n → smaller via swizzle, → larger via constructor
            int rf = Rank(from), rt = Rank(to);
            if (rf > 0 && rt > 0)
            {
                if (rt < rf) return $"({expr}).{Swizzle(rt)}";
                if (rt > rf)
                {
                    // pad missing channels with zeros (or 1 for alpha?). Use 0;
                    // alpha=1 is shader-specific and best decided by the caller.
                    string pad = string.Join(", ", PadZeros(rt - rf));
                    return $"{Glsl(to)}({expr}, {pad})";
                }
            }
            throw new NotImplementedException($"coerce {from} -> {to}");
        }

        static string Swizzle(int rank)
        {
            switch (rank)
            {
                case 1: return "x";
                case 2: return "xy";
                case 3: return "xyz";
                case 4: return "xyzw";
            }
            throw new Exception($"bad swizzle rank {rank}");
        }

        static IEnumerable<string> PadZeros(int n)
        {
            for (int i = 0; i < n; i++) yield return "0.0";
        }

        // ----- formatting helpers -------------------------------------------

        public static string FormatFloat(double d)
        {
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            return s.Contains(".") || s.Contains("e") || s.Contains("E") ? s : s + ".0";
        }
    }

    // Identifier conversion. Used everywhere we mint a GLSL name from a
    // user-typed Unity name (property reference names, subgraph file names,
    // SubGraphOutputNode slot names, …). Rules:
    //   • lower → upper:                BaseColor    → base_color
    //   • upper → upper-then-lower:     XMLParser    → xml_parser
    //   • letter → digit:               Texture2D    → texture_2d
    //                                   OutVector1   → out_vector_1
    //   • digit → upper-then-lower:     MyVec3Comp   → my_vec_3_comp
    //   • non-alnum becomes _, runs of _ collapsed, leading/trailing _ stripped
    static class SgNames
    {
        public static string ToSnakeCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var sb = new System.Text.StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                char prev = i > 0 ? s[i - 1] : '\0';
                char next = i + 1 < s.Length ? s[i + 1] : '\0';

                if (i > 0)
                {
                    if (char.IsLower(prev) && char.IsUpper(c)) sb.Append('_');
                    else if (char.IsUpper(prev) && char.IsUpper(c) && char.IsLower(next)) sb.Append('_');
                    else if (char.IsLetter(prev) && char.IsDigit(c)) sb.Append('_');
                    else if (char.IsDigit(prev) && char.IsUpper(c) && char.IsLower(next)) sb.Append('_');
                }

                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(char.ToLowerInvariant(c));
                else sb.Append('_');
            }
            var result = sb.ToString();
            while (result.Contains("__")) result = result.Replace("__", "_");
            return result.Trim('_');
        }
    }

    // ===== graph nodes/slots/edges/properties =================================

    class SgNode
    {
        public string Id;
        public string Type;                              // "MultiplyNode","UVNode","BlockNode",...
        public string SerializedDescriptor;              // BlockNode only ("SurfaceDescription.BaseColor")
        public Dictionary<int, string> SlotByNum         // numeric m_Id → slot ObjectId
            = new Dictionary<int, string>();
        public Dictionary<string, int> SlotByName        // m_DisplayName → numeric m_Id
            = new Dictionary<string, int>();
        public Dictionary<string, object> Raw;           // raw JSON for handler-specific fields
        public string GroupId;                           // m_Group.m_Id (empty when ungrouped)
    }

    class SgSlot
    {
        public string Id;
        public string Type;          // raw Unity slot type ("ColorRGBMaterialSlot","DynamicValueMaterialSlot",...)
        public int NumId;            // node-local m_Id
        public string OutputName;    // m_ShaderOutputName
        public string DisplayName;   // m_DisplayName (used to look up slots by name from handlers)
        public bool IsOutput;        // m_SlotType: 0=input, 1=output
        public object Value;         // m_Value (preferred over m_DefaultValue — see SgParser)

        // Best-effort mapping from a static slot type to an SgType. Returns
        // Unknown for dynamic slots — those resolve at lowering time.
        public SgType StaticType()
        {
            switch (Type)
            {
                case "Vector1MaterialSlot": return SgType.Float;
                case "Vector2MaterialSlot": return SgType.Vec2;
                case "Vector3MaterialSlot":
                case "PositionMaterialSlot":
                case "NormalMaterialSlot":
                case "TangentMaterialSlot":
                case "ColorRGBMaterialSlot":
                    return SgType.Vec3;
                case "Vector4MaterialSlot":
                case "ColorRGBAMaterialSlot":
                    return SgType.Vec4;
                case "BooleanMaterialSlot":
                    return SgType.Bool;
                case "UVMaterialSlot":
                    return SgType.Vec2;
                case "Texture2DMaterialSlot":
                case "Texture2DInputMaterialSlot":
                    return SgType.Texture2D;
                case "SamplerStateMaterialSlot":
                    return SgType.SamplerState;
            }
            return SgType.Unknown;
        }
    }

    // A shader keyword (boolean toggle or enum variant). Drives `#ifdef`
    // compile-time branches in the generated shader.
    class SgKeyword
    {
        public string Id;
        public string Name;             // m_Name (display)
        public string ReferenceName;    // m_DefaultReferenceName, e.g. "_BLEND_NORMALS"
        public bool IsBoolean;          // m_KeywordType: 0=Bool, 1=Enum
        public int DefaultValue;        // m_Value (boolean: 0=Off/1=On; enum: entry index)
        public List<SgKeywordEntry> Entries = new List<SgKeywordEntry>();
    }

    class SgKeywordEntry
    {
        public int Id;
        public string DisplayName;
        public string ReferenceName;    // entry tag, e.g. "VERTEX_POSITION"
    }

    // A material-exposed input. Mirrors Unity's ShaderProperty objects in
    // m_Properties — these become fields of the engine's MaterialFS struct.
    class SgProperty
    {
        public string Id;             // m_ObjectId — used by PropertyNode m_Property.m_Id
        public string Name;           // m_Name (display)
        public string ReferenceName;  // m_DefaultReferenceName ("_Color", "_Texture2D")
        public string FieldName;      // GLSL field in MaterialFS — derived from ReferenceName
        public SgType Type;
        public object DefaultValue;   // m_Value (for non-texture types)
        // True for Texture2D properties that hold a tangent-space normal map.
        // Unity does the [0,1]→[-1,1] decode transparently at the importer
        // level; we replicate it at sample time. Detected via the reference
        // name containing "normal" or "bump" (heuristic — covers _Normal_Map,
        // _Coverage_Normal_Map, _BumpMap, _NormalTex etc.).
        public bool IsNormalMap;
    }

    class SgStickyNote
    {
        public string Title;
        public string Content;
        public string GroupId;     // optional — non-null when sticky belongs to a group
    }

    class SgEdge
    {
        public string OutputNodeId;
        public int OutputSlotIdNum;
        public string InputNodeId;
        public int InputSlotIdNum;
    }

    class SgGraph
    {
        public SgVariant Variant;
        public bool AlphaClip;                 // from active target's m_AlphaClip
        // True when this shader is listed in grass_shaders.json — the codegen then
        // emits a sibling `<name>_grass.hz.glsl` whose @fs/@material are byte-identical
        // to the forward shader but whose @vs pulls from the grass SSBOs. Set by the
        // exporter (config lookup), not parsed from the graph itself.
        public bool IsGrassCapable;
        public List<string> VertexBlockIds = new List<string>();
        public List<string> FragmentBlockIds = new List<string>();
        public Dictionary<string, SgNode> Nodes = new Dictionary<string, SgNode>();
        public Dictionary<string, SgSlot> Slots = new Dictionary<string, SgSlot>();
        public List<SgEdge> Edges = new List<SgEdge>();

        // Properties in declaration order; PropertiesById indexes them by
        // m_ObjectId for PropertyNode lookups.
        public List<SgProperty> Properties = new List<SgProperty>();
        public Dictionary<string, SgProperty> PropertiesById = new Dictionary<string, SgProperty>();

        public List<SgKeyword> Keywords = new List<SgKeyword>();
        public Dictionary<string, SgKeyword> KeywordsById = new Dictionary<string, SgKeyword>();

        // Editor annotations — purely cosmetic, emitted as // comments in GLSL
        // so generated shaders are easier to navigate against the original graph.
        public Dictionary<string, string> GroupTitles = new Dictionary<string, string>();  // m_ObjectId → m_Title
        public List<SgStickyNote> StickyNotes = new List<SgStickyNote>();

        public SgNode FindFragmentBlock(string serializedDescriptor)
        {
            foreach (var id in FragmentBlockIds)
                if (Nodes.TryGetValue(id, out var b) && b.SerializedDescriptor == serializedDescriptor)
                    return b;
            return null;
        }

        public SgNode FindVertexBlock(string serializedDescriptor)
        {
            foreach (var id in VertexBlockIds)
                if (Nodes.TryGetValue(id, out var b) && b.SerializedDescriptor == serializedDescriptor)
                    return b;
            return null;
        }

        public SgEdge FindEdgeIntoSlot(string nodeId, int numSlotId)
        {
            foreach (var e in Edges)
                if (e.InputNodeId == nodeId && e.InputSlotIdNum == numSlotId) return e;
            return null;
        }
    }
}
