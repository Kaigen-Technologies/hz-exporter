using System;
using System.Collections.Generic;

namespace HZExporter.ShaderGraph
{
    // ===== values & contexts ===================================================

    // A value flowing along an edge during lowering. The Rhs is either an
    // SgRhsRef (when the producer was emitted to a temp) or an inline literal /
    // raw expression (for slot defaults).
    struct SgValue
    {
        public SgRhs Rhs;
        public SgType Type;
        public bool IsTemp;
        // unity gradient mode tag: 0=blend, 1=fixed, 2=perceptual. null for non-gradient values.
        public int? GradientMode;
        public string Glsl => Rhs.ToGlsl();   // convenience for callers that want the rendered form

        public static SgValue Temp(string name, SgType t) =>
            new SgValue { Rhs = Sg.Ref(name, t), Type = t, IsTemp = true };
        public static SgValue Inline(SgRhs rhs, SgType t) =>
            new SgValue { Rhs = rhs, Type = t, IsTemp = false };
    }

    // Owns the running statement list and the per-output-slot temp cache. Each
    // node's output is emitted at most once — subsequent reads return the
    // cached temp.
    class LowerCtx
    {
        public readonly SgGraph Graph;
        public readonly List<SgStatement> Statements = new List<SgStatement>();
        public readonly List<SgFunctionDef> Functions = new List<SgFunctionDef>();

        // Stage marker — vertex-stage-only nodes (PositionNode in object/world
        // space) check this and reject in fragment context. Subgraphs inherit
        // this from their parent context via SubGraphNodeHandler.
        public bool IsVertexStage;

        // True when this context is lowering inside a subgraph function
        // (rather than the parent shader's main). Used by PropertyNodeHandler
        // to emit subgraph-property reads as bare parameter names instead of
        // `mat.<field>` (since `mat` isn't in scope inside a function).
        public bool IsSubGraph;

        // Prefix for newly-emitted temp names. Defaults to `_n`. Per-branch
        // KeywordNode contexts override this to `_b<idx>_n` so branch-local
        // temps never collide with outer or sibling-branch names.
        public string TempPrefix = "_n";

        // Set of keyword IDs referenced by this context's lowering — used by
        // codegen to emit `// @keyword <name>` header comments.
        public readonly HashSet<string> UsedKeywords = new HashSet<string>();

        readonly Dictionary<string, SgValue> _cache = new Dictionary<string, SgValue>();
        int _tempCounter;

        // The node currently being lowered — set in Output() around the handler
        // call so EmitTemp can stamp emitted statements with the node's GroupId
        // (which codegen uses to inject `// === <group title> ===` headers).
        string _currentNodeGroupId;

        // Number of conditional-branch sub-contexts this ctx has spawned —
        // used to give branch-local temps unique prefixes (`_b0_n0`, `_b1_n0`).
        int _branchCounter;
        public int NextBranchIndex() => _branchCounter++;

        public LowerCtx(SgGraph g) { Graph = g; }

        public SgValue Output(string nodeId, int outSlotNum)
        {
            string key = nodeId + ":" + outSlotNum;
            if (_cache.TryGetValue(key, out var cached)) return cached;

            if (!Graph.Nodes.TryGetValue(nodeId, out var node))
                throw new Exception($"unknown node '{nodeId}'");

            var handler = SgNodes.Lookup(node.Type);
            var saved = _currentNodeGroupId;
            _currentNodeGroupId = node.GroupId;
            var v = handler.Lower(node, outSlotNum, this);
            _currentNodeGroupId = saved;
            _cache[key] = v;
            return v;
        }

        public SgValue Input(string nodeId, int inSlotNum)
        {
            if (!Graph.Nodes.TryGetValue(nodeId, out var node)
                || !node.SlotByNum.TryGetValue(inSlotNum, out var slotId)
                || !Graph.Slots.TryGetValue(slotId, out var consumerSlot))
                throw new Exception($"slot {nodeId}:{inSlotNum} cannot be resolved");

            // Resolve the upstream value (or a literal default).
            SgValue v;
            var edge = Graph.FindEdgeIntoSlot(nodeId, inSlotNum);
            if (edge != null) v = Output(edge.OutputNodeId, edge.OutputSlotIdNum);
            else v = SgLiterals.From(consumerSlot, IsVertexStage);

            // Coerce to the consumer slot's declared type when it's strongly
            // typed (Vector1/2/3/4, ColorRGB/A, Boolean, Texture2D, …) and
            // differs from what the upstream produced. This mirrors Unity
            // Shader Graph's silent-truncation/broadcast at every connection
            // and is what lets vec4 → vec2 (UV), vec4 → vec3 (color), etc.
            // happen automatically. DynamicValue/DynamicVector slots have
            // StaticType=Unknown so we leave their values alone.
            var want = consumerSlot.StaticType();
            if (want != SgType.Unknown && want != v.Type
                && v.Type != SgType.Unknown    // upstream was opaque (e.g. Raw) — nothing to coerce
                && want != SgType.Texture2D    // can't coerce arbitrary value into a texture
                && want != SgType.SamplerState
                && v.Type != SgType.Texture2D
                && v.Type != SgType.SamplerState)
            {
                v.Rhs = SgCodegen.CoerceRhsForReturn(v.Rhs, v.Type, want);
                v.Type = want;
            }
            return v;
        }

        public SgValue InputByName(SgNode node, string slotName)
        {
            if (!node.SlotByName.TryGetValue(slotName, out int num))
                throw new Exception($"node '{node.Type}' has no input slot named '{slotName}'");
            return Input(node.Id, num);
        }

        public SgValue EmitTemp(SgType t, SgRhs rhs)
        {
            string name = TempPrefix + (_tempCounter++);
            Statements.Add(new SgStatement { Name = name, Type = t, Rhs = rhs, GroupId = _currentNodeGroupId });
            return SgValue.Temp(name, t);
        }

        // Emits a temp whose GLSL type is a custom name (e.g. struct type) —
        // bypasses the SgType→GLSL mapping. Returns an SgValue carrying a Ref
        // to the temp; downstream consumers swizzle into it via SgRhsSwizzle
        // (which formats `<base>.<field>`).
        public SgValue EmitStructTemp(string structTypeName, SgRhs rhs)
        {
            string name = TempPrefix + (_tempCounter++);
            Statements.Add(new SgStatement { Name = name, Type = SgType.Unknown, TypeName = structTypeName, Rhs = rhs });
            return SgValue.Temp(name, SgType.Unknown);
        }

        // Forward-declares an outer-scope temp and follows it with one or
        // more `#if defined(...)`-gated assignment branches. Used by
        // KeywordNodeHandler.
        public SgValue EmitConditional(SgType t, List<SgConditionalBranch> branches)
        {
            string name = TempPrefix + (_tempCounter++);
            Statements.Add(new SgStatement { Name = name, Type = t, Branches = branches });
            return SgValue.Temp(name, t);
        }

        // Per-instance cache for SubGraphNode call sites — first output query
        // emits the call statement for the whole subgraph; subsequent queries
        // reuse the same call temp, just under a different field-access. The
        // cached SgFunctionDef tells us whether to swizzle into the temp
        // (struct-return) or use the temp directly (single-output).
        public readonly Dictionary<string, (SgValue temp, SgFunctionDef fn)> SubGraphCallTemps =
            new Dictionary<string, (SgValue, SgFunctionDef)>();

        // CombineNode caches its 4-input vec4 packing temp so each requested
        // output slot (RGBA/RGB/RG) reuses the same emit instead of repacking.
        public readonly Dictionary<string, SgValue> CombineTemps = new Dictionary<string, SgValue>();

        // CustomFunctionNode caches its struct-return call temp so each output
        // slot of a single node hits the same `<EngineStruct> _tN = hz_fn(…);`.
        public readonly Dictionary<string, SgValue> CustomFunctionTemps = new Dictionary<string, SgValue>();

        // Helper functions (name, body) emitted once per stage at the top of
        // the body. Set used to dedupe across multiple node lowerings.
        public readonly HashSet<(string name, string body)> HelperFunctions =
            new HashSet<(string, string)>();
    }

    // ===== slot-default literals ==============================================

    // Convert a slot's stored m_Value into an SgValue. Foldable types (scalar/
    // vec) become SgRhsLit; non-foldable (mat4) fall back to SgRhsRaw with the
    // string form so codegen still works but the optimizer skips them.
    static class SgLiterals
    {
        public static SgValue From(SgSlot slot, bool isVertexStage = false)
        {
            var t = slot.StaticType();
            if (t == SgType.Texture2D || t == SgType.SamplerState)
                throw new NotImplementedException(
                    $"slot '{slot.DisplayName}' of type {slot.Type} cannot have a literal default — must be wired");
            // UVMaterialSlot's m_Value is (0,0) which literally means "no
            // input" in Unity — the runtime substitutes mesh UV0 (or UVn per
            // m_Channel). Source differs by stage: vertex reads `v.uv` (raw
            // mesh attribute), fragment reads `v_uv` (interpolated varying).
            if (slot.Type == "UVMaterialSlot")
                return SgValue.Inline(Sg.Raw(isVertexStage ? "v.uv" : "v_uv"), SgType.Vec2);
            // ScreenPositionMaterialSlot unwired → same as Unity's
            // ScreenPositionNode in Default mode: (screen_uv.x, screen_uv.y, 0, 0).
            // SceneColor/SceneDepth take .xy directly (no perspective divide),
            // matching Unity HLSL which samples with UV.xy. Fragment stage only.
            // Divide by dpr first: gl_FragCoord is in framebuffer pixels but
            // cluster.screen_width/height are logical (pre-dpr) — without the
            // dpr divide the UV exceeds 1 and the sampler wraps the texture,
            // producing quincunx-style ghost copies in scene_color samples.
            if (slot.Type == "ScreenPositionMaterialSlot")
                return SgValue.Inline(Sg.Raw(
                    "vec4((gl_FragCoord.xy / hz_scene.cluster.dpr) / vec2(float(hz_scene.cluster.screen_width), float(hz_scene.cluster.screen_height)), 0.0, 0.0)"),
                    SgType.Vec4);
            // GradientInputMaterialSlot's m_Value is an inline gradient.
            // Serialized layout (matches UnityEngine.Gradient):
            //   keyN  : Color  — RGB is the stop's color, `a` is the stop's
            //                    ALPHA (not the position).
            //   ctimeN: u16    — color-stop position, normalized to [0,1]
            //                    via x/65535. THIS is the time, not `key.a`.
            //   atimeN: u16    — alpha-stop position (unused here; the engine
            //                    macro doesn't carry alpha).
            //   m_NumColorKeys: how many of the 8 slots are actually used.
            // The engine ships GRADIENT_4 taking 4 (rgb, t) pairs — pack the
            // first four real color keys, padding by repeating the last.
            if (slot.Type == "GradientInputMaterialSlot")
            {
                if (slot.Value is Dictionary<string, object> gv)
                {
                    int numKeys = 0;
                    if (gv.TryGetValue("m_NumColorKeys", out var nko) && nko is double nkd) numKeys = (int)nkd;
                    if (numKeys <= 0) numKeys = 1;
                    if (numKeys > 8) numKeys = 8;
                    if (numKeys > 4)
                        UnityEngine.Debug.LogWarning($"[SgLiterals] gradient on slot '{slot.DisplayName}' has {numKeys} color stops — engine GRADIENT_4 only carries 4; extra stops truncated.");

                    var keys = new List<(double r, double g, double b, double t)>();
                    for (int i = 0; i < numKeys; i++)
                    {
                        if (!gv.TryGetValue($"key{i}", out var ko) || !(ko is Dictionary<string, object> kd))
                            continue;
                        double r = kd.TryGetValue("r", out var rv) && rv is double rd ? rd : 0;
                        double g = kd.TryGetValue("g", out var gv2) && gv2 is double gd ? gd : 0;
                        double b = kd.TryGetValue("b", out var bv) && bv is double bd ? bd : 0;
                        double tval = 0;
                        if (gv.TryGetValue($"ctime{i}", out var to) && to is double td) tval = td / 65535.0;
                        keys.Add((r, g, b, tval));
                    }
                    if (keys.Count == 0) keys.Add((1, 1, 1, 1));
                    var parts = new List<string>();
                    for (int i = 0; i < 4; i++)
                    {
                        var k = keys[System.Math.Min(i, keys.Count - 1)];
                        parts.Add($"vec3({SgTypes.FormatFloat(k.r)}, {SgTypes.FormatFloat(k.g)}, {SgTypes.FormatFloat(k.b)})");
                        parts.Add(SgTypes.FormatFloat(k.t));
                    }
                    int mode = 0;
                    if (gv.TryGetValue("m_Mode", out var mo) && mo is double mdv) mode = (int)mdv;
                    var litVal = SgValue.Inline(Sg.Raw($"GRADIENT_4({string.Join(", ", parts)})"), SgType.Unknown);
                    litVal.GradientMode = mode;
                    return litVal;
                }
            }
            if (t == SgType.Unknown)
            {
                // DynamicValue/DynamicVector default to scalar — see SgRhs.cs
                // commentary on the Matrix4x4 backing field.
                if (slot.Type == "DynamicValueMaterialSlot" || slot.Type == "DynamicVectorMaterialSlot")
                    t = SgType.Float;
                else
                    throw new NotImplementedException($"slot type '{slot.Type}' has no default-literal rule");
            }

            if (TryParseConst(slot.Value, t, out var c))
                return SgValue.Inline(Sg.LitConst(c), t);

            // Non-foldable fallback (currently mat4 only).
            string raw = LiteralAtAsString(slot.Value, t);
            return SgValue.Inline(Sg.Raw(raw), t);
        }

        static bool TryParseConst(object value, SgType t, out SgConst c)
        {
            c = default;
            if (value is double d)
            {
                switch (t)
                {
                    case SgType.Float: c = SgConst.Scalar(d); return true;
                    case SgType.Vec2:  c = SgConst.Vec(t, d, d); return true;
                    case SgType.Vec3:  c = SgConst.Vec(t, d, d, d); return true;
                    case SgType.Vec4:  c = SgConst.Vec(t, d, d, d, d); return true;
                }
                return false;
            }
            if (value is Dictionary<string, object> obj)
            {
                if (obj.ContainsKey("e00"))
                {
                    switch (t)
                    {
                        case SgType.Float: c = SgConst.Scalar(F(obj, "e00")); return true;
                        case SgType.Vec2:  c = SgConst.Vec(t, F(obj, "e00"), F(obj, "e01")); return true;
                        case SgType.Vec3:  c = SgConst.Vec(t, F(obj, "e00"), F(obj, "e01"), F(obj, "e02")); return true;
                        case SgType.Vec4:  c = SgConst.Vec(t, F(obj, "e00"), F(obj, "e01"), F(obj, "e02"), F(obj, "e03")); return true;
                    }
                    return false;   // mat4 → not const
                }
                if (obj.ContainsKey("x"))
                {
                    // x/y/z/w storage — usable as Float (read .x) or as a vec_n.
                    switch (t)
                    {
                        case SgType.Float: c = SgConst.Scalar(F(obj, "x")); return true;
                        case SgType.Vec2:  c = SgConst.Vec(t, F(obj, "x"), F(obj, "y")); return true;
                        case SgType.Vec3:  c = SgConst.Vec(t, F(obj, "x"), F(obj, "y"), F(obj, "z")); return true;
                        case SgType.Vec4:  c = SgConst.Vec(t, F(obj, "x"), F(obj, "y"), F(obj, "z"), F(obj, "w")); return true;
                    }
                    return false;
                }
                if (obj.ContainsKey("r"))
                {
                    switch (t)
                    {
                        case SgType.Float: c = SgConst.Scalar(F(obj, "r")); return true;
                        case SgType.Vec3:  c = SgConst.Vec(t, F(obj, "r"), F(obj, "g"), F(obj, "b")); return true;
                        case SgType.Vec4:  c = SgConst.Vec(t, F(obj, "r"), F(obj, "g"), F(obj, "b"), F(obj, "a")); return true;
                    }
                    return false;
                }
            }
            return false;
        }

        // Fallback for the rare non-foldable cases (mat4 slot defaults).
        static string LiteralAtAsString(object value, SgType t)
        {
            if (value is Dictionary<string, object> obj && obj.ContainsKey("e00") && t == SgType.Mat4)
            {
                var sb = new System.Text.StringBuilder("mat4(");
                bool first = true;
                for (int c = 0; c < 4; c++)
                    for (int r = 0; r < 4; r++)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append(SgTypes.FormatFloat(F(obj, $"e{r}{c}")));
                    }
                sb.Append(")");
                return sb.ToString();
            }
            return Zero(t);
        }

        static string Zero(SgType t)
        {
            switch (t)
            {
                case SgType.Float: return "0.0";
                case SgType.Vec2: return "vec2(0.0)";
                case SgType.Vec3: return "vec3(0.0)";
                case SgType.Vec4: return "vec4(0.0)";
                case SgType.Mat4: return "mat4(0.0)";
                case SgType.Bool: return "false";
            }
            return "0.0";
        }

        static double F(Dictionary<string, object> o, string k) =>
            o.TryGetValue(k, out var v) && v is double d ? d : 0.0;
    }

    // ===== node handlers ======================================================

    abstract class NodeHandler
    {
        public abstract SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx);
    }

    // Pattern-driven handler: data + a builder lambda. Most arithmetic and
    // builtin nodes fit this — see the registry below. The Build callback
    // gets full SgValues so type-aware ops (e.g. Multiply truncating vec4 to
    // match a vec3 sibling, mirroring Unity Shader Graph's HLSL semantics)
    // can introspect ranks at build time.
    class PatternHandler : NodeHandler
    {
        public string[] InputNames;
        public Func<SgValue[], SgRhs> Build;
        public Func<SgType[], SgType> OutType;

        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            var values = new SgValue[InputNames.Length];
            for (int i = 0; i < InputNames.Length; i++)
                values[i] = ctx.InputByName(node, InputNames[i]);

            var tArr = new SgType[values.Length];
            for (int i = 0; i < values.Length; i++) tArr[i] = values[i].Type;

            var rhs = Build(values);
            var outT = OutType(tArr);
            return ctx.EmitTemp(outT, rhs);
        }
    }

    // ----- specific custom handlers ----------------------------------------

    class UVNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            int channel = 0;
            if (node.Raw.TryGetValue("m_OutputChannel", out var ch) && ch is double d) channel = (int)d;
            string source;
            // In vertex stage we read raw mesh attributes (`v.uv`); in fragment
            // stage we read the interpolated varyings (`v_uv`). UV1 has a
            // y-flip applied in the vertex shader's varying assignment for the
            // fragment side — the raw `v.uv1` matches Unity's V convention.
            string uv0 = ctx.IsVertexStage ? "v.uv" : "v_uv";
            string uv1 = ctx.IsVertexStage ? "vec2(v.uv1.x, 1.0 - v.uv1.y)" : "v_uv1";
            switch (channel)
            {
                case 0: source = uv0; break;
                case 1: source = uv1; break;
                // UV2/UV3 aren't carried by the engine vertex layout, so they
                // fall back to UV0. Emitting zero instead collapses anything
                // that reads them (e.g. vegetation's UV3-driven color overlay)
                // to a constant, which is more visibly broken than reusing UV0.
                // LogWarning stays loud so we notice — if a mesh ever carries
                // real UV2/UV3 data, that data is lost (HZGlbWriter logs the
                // dropped channel separately).
                case 2: case 3:
                    UnityEngine.Debug.LogWarning($"[SgLowering] UV channel {channel} not plumbed in engine vertex layout — falling back to UV0 in UVNode '{node.Id}' (stage={(ctx.IsVertexStage ? "vs" : "fs")}). If a mesh carries real UV{channel}, that data is lost.");
                    source = $"{uv0} /* TODO: UV channel {channel} not plumbed; falling back to UV0 */";
                    break;
                default: throw new NotImplementedException($"UV channel {channel} not wired");
            }
            // Varyings aren't constants — wrap as Raw so the optimizer leaves
            // them alone but DCE still sees no inbound refs.
            return ctx.EmitTemp(SgType.Vec4, Sg.Raw($"vec4({source}, 0.0, 0.0)"));
        }
    }

    // Vector1Node forwards the literal in its X port; no statement of its own.
    class Vector1NodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            return ctx.InputByName(node, "X");
        }
    }

    // Vector2/3/4 nodes pack scalar inputs into a vector temp. Unity defaults
    // unwired inputs to their stored m_Value via slot literals — same mechanism
    // as MultiplyNode operands, no special handling needed beyond Input().
    class VectorPackNodeHandler : NodeHandler
    {
        readonly int _rank;
        public VectorPackNodeHandler(int rank) { _rank = rank; }
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            var args = new List<SgRhs>(_rank);
            string[] names = { "X", "Y", "Z", "W" };
            for (int i = 0; i < _rank; i++) args.Add(ctx.InputByName(node, names[i]).Rhs);
            string ctor = _rank == 2 ? "vec2" : _rank == 3 ? "vec3" : "vec4";
            SgType t = _rank == 2 ? SgType.Vec2 : _rank == 3 ? SgType.Vec3 : SgType.Vec4;
            return ctx.EmitTemp(t, new SgRhsCall { Func = ctor, Args = args });
        }
    }

    // SubGraphNode: load the referenced .shadersubgraph, lower every declared
    // output into a single GLSL function on the same file, and emit one call
    // statement at the call site. Subsequent reads of any output of the same
    // SubGraphNode hit the cache and become field-access swizzles into the
    // call temp.
    //
    // Two output forms:
    //   • 1 output  → simple value-return function, call temp typed as the
    //                 output type. Consumers reference the temp directly.
    //   • 2+ outputs → struct-return function. Call temp has the struct type;
    //                  consumers reference fields via swizzle.
    class SubGraphNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            if (!node.SlotByNum.TryGetValue(outSlotNum, out var slotId))
                throw new System.Exception($"SubGraphNode: invalid output slot {outSlotNum}");
            var requestedSlot = ctx.Graph.Slots[slotId];
            var requestedType = requestedSlot.StaticType();
            if (requestedType == SgType.Unknown)
                throw new System.NotImplementedException($"SubGraphNode output '{requestedSlot.OutputName}' has unmapped slot type '{requestedSlot.Type}'");

            // First touch of this SubGraphNode → emit the call site once.
            if (!ctx.SubGraphCallTemps.TryGetValue(node.Id, out var entry))
            {
                entry = EmitCallSite(node, ctx);
                ctx.SubGraphCallTemps[node.Id] = entry;
            }

            // For single-output functions, the call temp IS the value.
            // For multi-output struct-return, swizzle into the field.
            if (entry.fn.StructName == null)
                return new SgValue { Rhs = entry.temp.Rhs, Type = requestedType, IsTemp = false };
            return new SgValue
            {
                Rhs = Sg.Swizzle(entry.temp.Rhs, SgNames.ToSnakeCase(requestedSlot.OutputName)),
                Type = requestedType,
                IsTemp = false,
            };
        }

        (SgValue temp, SgFunctionDef fn) EmitCallSite(SgNode node, LowerCtx ctx)
        {
            string guid = ExtractGuid(node.Raw);
            if (string.IsNullOrEmpty(guid))
                throw new System.Exception("SubGraphNode missing m_SerializedSubGraph guid");

            var sg = SgSubGraphLoader.Load(guid);
            string fnName = SgSubGraphLoader.FunctionName(guid);

            // Reuse an already-emitted function for this subgraph (if some
            // other SubGraphNode instance already created it).
            SgFunctionDef fn = null;
            foreach (var f in ctx.Functions) if (f.Name == fnName) { fn = f; break; }
            if (fn == null)
                fn = BuildFunction(node, ctx, sg, guid, fnName);

            // Emit `<retType> _nN = fnName(args…);` — struct-typed for
            // multi-output, primitive-typed for single-output. Args come from
            // the SubGraphNode's input slots, mapped to subgraph properties.
            var callArgs = ResolveCallArgs(node, sg, ctx);
            var callRhs = Sg.Call(fnName, callArgs.ToArray());
            var temp = fn.StructName != null
                ? ctx.EmitStructTemp(fn.StructName, callRhs)
                : ctx.EmitTemp(fn.Outputs[0].Type, callRhs);
            return (temp, fn);
        }

        // Lowers every declared output of `node` (in the order the
        // SubGraphNode declares them) into an SgFunctionOutput. The function
        // body comes from one shared LowerCtx so outputs share intermediate
        // statements when the optimizer runs over them.
        SgFunctionDef BuildFunction(SgNode node, LowerCtx ctx, SgGraph sg, string guid, string fnName)
        {
            SgNode sgOut = null;
            foreach (var n in sg.Nodes.Values) if (n.Type == "SubGraphOutputNode") { sgOut = n; break; }
            if (sgOut == null) throw new System.Exception($"subgraph {guid} has no SubGraphOutputNode");

            var fnCtx = new LowerCtx(sg) { IsVertexStage = ctx.IsVertexStage, IsSubGraph = true };

            // Subgraph properties become function parameters. PropertyNode
            // inside the subgraph body will reference them as bare names.
            // Texture-typed properties aren't supported as subgraph inputs
            // here yet — error if encountered.
            var fnParams = new List<SgFunctionParam>();
            foreach (var prop in sg.Properties)
            {
                if (prop.Type == SgType.Texture2D)
                    throw new System.NotImplementedException(
                        $"subgraph {guid}: Texture2D properties as subgraph inputs not yet supported");
                fnParams.Add(new SgFunctionParam { Name = prop.FieldName, Type = prop.Type });
            }

            var outputs = new List<SgFunctionOutput>();

            // Iterate the SubGraphNode's slots in their numeric m_Id order so
            // the function's outputs/struct fields match the user's
            // declaration order.
            var slotNums = new List<int>(node.SlotByNum.Keys);
            slotNums.Sort();
            foreach (var num in slotNums)
            {
                var slot = ctx.Graph.Slots[node.SlotByNum[num]];
                if (!slot.IsOutput) continue;   // SubGraphNode property inputs (not yet supported)

                int sgInSlot = -1;
                foreach (var skv in sgOut.SlotByNum)
                    if (sg.Slots[skv.Value].OutputName == slot.OutputName) { sgInSlot = skv.Key; break; }
                if (sgInSlot == -1)
                    throw new System.Exception($"subgraph {guid} has no output named '{slot.OutputName}'");

                var v = fnCtx.Input(sgOut.Id, sgInSlot);
                var coerced = SgCodegen.CoerceRhsForReturn(v.Rhs, v.Type, slot.StaticType());
                outputs.Add(new SgFunctionOutput
                {
                    Name = SgNames.ToSnakeCase(slot.OutputName),
                    Type = slot.StaticType(),
                    Rhs = coerced,
                });
            }

            // Promote nested SubGraphs' functions into the parent shader.
            ctx.Functions.AddRange(fnCtx.Functions);

            var fn = new SgFunctionDef
            {
                Name = fnName,
                StructName = outputs.Count > 1 ? fnName + "_outputs" : null,
                Params = fnParams,
                Outputs = outputs,
                Body = fnCtx.Statements,
            };
            ctx.Functions.Add(fn);
            return fn;
        }

        // For each subgraph property (in declaration order), find the
        // SubGraphNode input slot whose m_ShaderOutputName matches the
        // property's reference name, lower the value flowing in, and produce
        // an SgRhs to pass as the call argument.
        List<SgRhs> ResolveCallArgs(SgNode node, SgGraph sg, LowerCtx ctx)
        {
            var args = new List<SgRhs>();
            foreach (var prop in sg.Properties)
            {
                // Unity's m_Id can be any 32-bit signed integer (often a hash
                // — negative is common for subgraph property slots). Use a
                // separate `found` flag instead of a sentinel value so a
                // legitimate slot with id < 0 doesn't trip the "not found"
                // branch.
                int slotNum = 0;
                bool found = false;
                foreach (var kv in node.SlotByNum)
                {
                    if (!ctx.Graph.Slots.TryGetValue(kv.Value, out var s)) continue;
                    if (s.IsOutput) continue;
                    if (s.OutputName == prop.ReferenceName) { slotNum = kv.Key; found = true; break; }
                }
                if (!found)
                {
                    var available = new List<string>();
                    foreach (var kv in node.SlotByNum)
                    {
                        if (!ctx.Graph.Slots.TryGetValue(kv.Value, out var s)) continue;
                        available.Add($"[{kv.Key}] '{s.OutputName}' IsOutput={s.IsOutput}");
                    }
                    throw new System.Exception(
                        $"SubGraphNode '{node.Id}' missing input slot for subgraph property '{prop.ReferenceName}' (type={prop.Type}). " +
                        $"Available slots: {string.Join(", ", available)}");
                }

                var v = ctx.Input(node.Id, slotNum);
                args.Add(SgCodegen.CoerceRhsForReturn(v.Rhs, v.Type, prop.Type));
            }
            return args;
        }

        // m_SerializedSubGraph is a JSON string nested inside the outer JSON:
        //   { subGraph: { fileID, guid, type } }
        static string ExtractGuid(Dictionary<string, object> raw)
        {
            if (!raw.TryGetValue("m_SerializedSubGraph", out var v) || !(v is string s)) return null;
            int i = 0;
            try
            {
                var inner = TinyJson.ParseObject(s, ref i);
                if (inner.TryGetValue("subGraph", out var sub) && sub is Dictionary<string, object> sd
                    && sd.TryGetValue("guid", out var g) && g is string gs)
                    return gs;
            }
            catch { }
            return null;
        }
    }

    // Resolves Unity asset GUIDs to .shadersubgraph paths and parses them. Also
    // mints stable function names from the subgraph file name.
    static class SgSubGraphLoader
    {
        static readonly Dictionary<string, SgGraph> _graphs = new Dictionary<string, SgGraph>();
        static readonly Dictionary<string, string> _fnNames = new Dictionary<string, string>();

        public static SgGraph Load(string guid)
        {
            if (_graphs.TryGetValue(guid, out var g)) return g;
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
                throw new System.Exception($"subgraph guid '{guid}' not resolvable to a path");
            string text = System.IO.File.ReadAllText(path);
            var parsed = SgParser.Parse(text);
            _graphs[guid] = parsed;
            return parsed;
        }

        public static string FunctionName(string guid)
        {
            if (_fnNames.TryGetValue(guid, out var n)) return n;
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            n = string.IsNullOrEmpty(path)
                ? "subgraph_" + guid.Substring(0, 8)
                : "subgraph_" + SgNames.ToSnakeCase(System.IO.Path.GetFileNameWithoutExtension(path));
            _fnNames[guid] = n;
            return n;
        }
    }

    // NormalVectorNode: per-fragment surface normal in the requested space.
    // Engine's @fs already has world-space `v_normal` as a varying; we reject
    // other spaces until the engine plumbs them.
    class NormalVectorNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            int space = 0;
            if (node.Raw.TryGetValue("m_Space", out var s) && s is double d) space = (int)d;
            switch (space)
            {
                case 0: // Object — raw mesh normal in vertex stage; not available in @fs without an extra varying
                    if (ctx.IsVertexStage)
                        return new SgValue { Rhs = Sg.Raw("v.normal"), Type = SgType.Vec3, IsTemp = false };
                    throw new System.NotImplementedException("NormalVectorNode space=Object in fragment stage needs an object-space varying");
                case 2: // World
                    if (ctx.IsVertexStage)
                        return new SgValue { Rhs = Sg.Raw("normalize((v.model * vec4(v.normal, 0.0)).xyz)"), Type = SgType.Vec3, IsTemp = false };
                    return new SgValue { Rhs = Sg.Raw("v_normal"), Type = SgType.Vec3, IsTemp = false };
                default:
                    throw new System.NotImplementedException($"NormalVectorNode space {space} not supported");
            }
        }
    }

    // MainLightDirectionNode: the engine's primary directional light, points
    // FROM the light. Unity's convention is the same.
    class MainLightDirectionNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
            => new SgValue { Rhs = Sg.Raw("hz_scene.lights.directional[0].direction"), Type = SgType.Vec3, IsTemp = false };
    }

    // GradientNode: emit a `GRADIENT_N(rgb1, t1, rgb2, t2, …)` literal. The
    // node serializes color stops (rgb in xyz, position in w) in
    // `m_SerializableColorKeys`; the engine macro expects up to 4 stops, so
    // we clamp/pad to 4. Alpha keys are ignored — the engine's gradient
    // helpers don't carry alpha.
    class GradientNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            var keys = node.Raw.TryGetValue("m_SerializableColorKeys", out var k) ? k as List<object> : null;
            if (keys == null || keys.Count == 0)
                throw new System.Exception("GradientNode has no color keys");

            // Engine only ships GRADIENT_4 today; if fewer keys, pad by
            // repeating the last; if more, truncate.
            const int N = 4;
            var parts = new List<string>();
            for (int i = 0; i < N; i++)
            {
                int srcIdx = System.Math.Min(i, keys.Count - 1);
                var key = keys[srcIdx] as Dictionary<string, object>;
                double r = key.TryGetValue("x", out var v0) && v0 is double xx ? xx : 0;
                double g = key.TryGetValue("y", out var v1) && v1 is double yy ? yy : 0;
                double b = key.TryGetValue("z", out var v2) && v2 is double zz ? zz : 0;
                double t = key.TryGetValue("w", out var v3) && v3 is double ww ? ww : 0;
                parts.Add($"vec3({SgTypes.FormatFloat(r)}, {SgTypes.FormatFloat(g)}, {SgTypes.FormatFloat(b)})");
                parts.Add(SgTypes.FormatFloat(t));
            }
            string macro = $"GRADIENT_4({string.Join(", ", parts)})";

            // gradient mode lives at the GradientNode root, not inside the keys.
            int mode = 0;
            if (node.Raw.TryGetValue("m_SerializableMode", out var mo) && mo is double mdv) mode = (int)mdv;

            // Type is "Gradient" (engine struct). Emit a temp with TypeName
            // override so subsequent SampleGradient consumers can read it.
            var tmp = ctx.EmitStructTemp("Gradient", Sg.Raw(macro));
            tmp.GradientMode = mode;
            return tmp;
        }
    }

    // SampleGradient: takes a Gradient + a Time scalar, returns vec4 (sampled
    // color). Engine helper is `gradient_sample_classic`.
    // Slot numbers: m_DisplayName tells us — we look up by name to be
    // robust against ordering changes.
    class SampleGradientHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            // Inputs typically named "Gradient" and "Time"; output "Out".
            var grad = ctx.InputByName(node, "Gradient");
            var t    = ctx.InputByName(node, "Time");
            // dispatch by Unity Gradient mode: 0=Blend, 1=Fixed (step), 2=PerceptualBlend (OkLab).
            string fn = "gradient_sample_classic";
            if (grad.GradientMode == 1) fn = "gradient_sample_fixed";
            else if (grad.GradientMode == 2) fn = "gradient_sample_perceptual";
            return ctx.EmitTemp(SgType.Vec4, Sg.RawTemplate(fn + "({0}, {1})", grad.Rhs, t.Rhs));
        }
    }

    // RedirectNodeData: visual-only graph reroute. Two slots — one input,
    // one output, both same type. Forward the input verbatim.
    class RedirectNodeDataHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            // The input slot is whichever has m_SlotType=0 (input). Find it
            // and pull the value through.
            foreach (var kv in node.SlotByNum)
            {
                if (!ctx.Graph.Slots.TryGetValue(kv.Value, out var slot)) continue;
                if (!slot.IsOutput) return ctx.Input(node.Id, kv.Key);
            }
            throw new System.Exception("RedirectNodeData has no input slot");
        }
    }

    // KeywordNode: a `#ifdef`-driven compile-time branch on a shader keyword.
    // For each variant input on the node, we lower the chain feeding it in
    // a fresh nested LowerCtx whose temps live inside the branch's `{ }`
    // block (separate prefix prevents collisions). The result is an
    // SgConditionalStatement that declares one outer-scope temp and assigns
    // it inside each `#if defined(...)` branch.
    class KeywordNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            if (!node.Raw.TryGetValue("m_Keyword", out var k) || !(k is Dictionary<string, object> kd)
                || !kd.TryGetValue("m_Id", out var kid) || !(kid is string keywordId))
                throw new System.Exception("KeywordNode missing m_Keyword reference");
            if (!ctx.Graph.KeywordsById.TryGetValue(keywordId, out var keyword))
                throw new System.Exception($"KeywordNode references unknown keyword '{keywordId}'");

            ctx.UsedKeywords.Add(keyword.Id);

            var branches = new List<SgConditionalBranch>();
            SgType outType = SgType.Unknown;

            if (keyword.IsBoolean)
            {
                int onSlot = node.SlotByName.TryGetValue("On", out var os) ? os : 1;
                int offSlot = node.SlotByName.TryGetValue("Off", out var fs) ? fs : 2;
                branches.Add(LowerBranch(node, onSlot,  $"{keyword.ReferenceName}_ON", ctx));
                branches.Add(LowerBranch(node, offSlot, null,                          ctx));
            }
            else
            {
                int defaultIdx = System.Math.Max(0, System.Math.Min(keyword.DefaultValue, keyword.Entries.Count - 1));
                for (int i = 0; i < keyword.Entries.Count; i++)
                {
                    var entry = keyword.Entries[i];
                    if (!node.SlotByName.TryGetValue(entry.DisplayName, out var slot))
                        throw new System.Exception(
                            $"KeywordNode '{keyword.Name}' has no slot for entry '{entry.DisplayName}'");
                    string define = i == defaultIdx ? null : $"{keyword.ReferenceName}_{entry.ReferenceName}";
                    branches.Add(LowerBranch(node, slot, define, ctx));
                }
                int elseIdx = branches.FindIndex(b => b.Define == null);
                if (elseIdx >= 0 && elseIdx != branches.Count - 1)
                {
                    var def = branches[elseIdx];
                    branches.RemoveAt(elseIdx);
                    branches.Add(def);
                }
            }

            // Result type: the highest-rank type across all branches. With
            // DynamicValue slots one branch may be e.g. vec4 (wired) while
            // another is float (default-of-unconnected). GLSL won't accept
            // assigning a float to a vec4 temp, so coerce each branch's
            // Rhs to the unified outer type.
            if (branches.Count == 0) outType = SgType.Vec4;
            else
            {
                outType = branches[0].RhsType;
                for (int i = 1; i < branches.Count; i++)
                    outType = SgTypes.Promote(outType, branches[i].RhsType);
            }
            for (int i = 0; i < branches.Count; i++)
            {
                if (branches[i].RhsType != outType)
                {
                    branches[i].Rhs = SgCodegen.CoerceRhsForReturn(branches[i].Rhs, branches[i].RhsType, outType);
                    branches[i].RhsType = outType;
                }
            }
            return ctx.EmitConditional(outType, branches);
        }

        SgConditionalBranch LowerBranch(SgNode node, int slotNum, string define, LowerCtx parentCtx)
        {
            int idx = parentCtx.NextBranchIndex();
            // Build a tree-pathed prefix so nested KeywordNodes get unique
            // temp names. Parent `_n` → `_b0_n`. Parent `_b0_n` → `_b0_b1_n`
            // (with idx=1). Without this, a nested branch counter restarts
            // at 0 and emits `_b0_n0` again, shadowing the outer branch's
            // already-emitted `_b0_n0` and producing GLSL type errors when
            // the conditional's result is assigned back.
            string parentPath = parentCtx.TempPrefix.EndsWith("_n")
                ? parentCtx.TempPrefix.Substring(0, parentCtx.TempPrefix.Length - 2)
                : "";
            var branchCtx = new LowerCtx(parentCtx.Graph)
            {
                IsVertexStage = parentCtx.IsVertexStage,
                IsSubGraph = parentCtx.IsSubGraph,
                TempPrefix = $"{parentPath}_b{idx}_n",
            };
            // Lower the value flowing into the branch's input slot. This may
            // recursively emit temps into branchCtx.Statements (branch-local).
            var v = branchCtx.Input(node.Id, slotNum);
            // Promote any subgraph functions surfaced inside the branch.
            parentCtx.Functions.AddRange(branchCtx.Functions);
            // Propagate keyword usage too.
            foreach (var kw in branchCtx.UsedKeywords) parentCtx.UsedKeywords.Add(kw);
            // Helper functions (e.g. _hz_gradient_noise) bubble up so they're
            // emitted once at the top of the stage instead of inside the
            // branch's `{ }` block (which would shadow same-named helpers in
            // sibling branches).
            foreach (var h in branchCtx.HelperFunctions) parentCtx.HelperFunctions.Add(h);
            return new SgConditionalBranch
            {
                Define = define,
                RhsType = v.Type,
                Body = branchCtx.Statements,
                Rhs = v.Rhs,
            };
        }
    }

    // TransformNode: coordinate-space conversion. m_Conversion.{from,to}
    // identifies the spaces (0=Object, 1=View, 2=World, 3=Tangent,
    // 4=AbsoluteWorld, 5=Screen); m_ConversionType is 0=Position, 1=Direction,
    // 2=Normal. We support the conversions we've actually hit; others throw
    // until they come up so we add them deliberately.
    class TransformNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            int from = ReadInt(node, "m_Conversion", "from");
            int to   = ReadInt(node, "m_Conversion", "to");
            int kind = 0;
            if (node.Raw.TryGetValue("m_ConversionType", out var ct) && ct is double d) kind = (int)d;

            var input = ctx.InputByName(node, "In");

            if (from == to)
                return new SgValue { Rhs = input.Rhs, Type = SgType.Vec3, IsTemp = false };

            // Tangent → World direction/normal: build worldDir = T*x + B*y + N*z
            // where B is the per-vertex-handed cross product. Engine's @fs
            // exposes v_tangent, v_normal, v_bitangent_sign for this exact
            // basis. Fragment stage only.
            if (from == 3 && to == 2 && (kind == 1 || kind == 2))
            {
                if (ctx.IsVertexStage)
                    throw new System.NotImplementedException("Tangent→World transform requires fragment-stage TBN");
                // Normalize: tangent and bitangent aren't guaranteed orthonormal
                // to v_normal post-interpolation, and downstream coverage masks
                // take `.y` to test "is this face pointing up?" — without
                // normalization the scalar isn't bounded to [-1, 1] and
                // pow/contrast curves end up in nonsense regimes.
                var rhs = Sg.RawTemplate(
                    "normalize(v_tangent * {0}.x + cross(v_normal, v_tangent) * v_bitangent_sign * {0}.y + v_normal * {0}.z)",
                    input.Rhs);
                return ctx.EmitTemp(SgType.Vec3, rhs);
            }

            // Object → World: model matrix; w-component depends on kind
            // (1.0 for positions, 0.0 for directions/normals).
            if (from == 0 && to == 2)
            {
                string w = (kind == 0) ? "1.0" : "0.0";
                var rhs = Sg.RawTemplate("(v.model * vec4({0}, " + w + ")).xyz", input.Rhs);
                return ctx.EmitTemp(SgType.Vec3, rhs);
            }

            // World → Object: inverse model matrix. Used by vegetation shaders
            // to convert a world-space wind direction into object space so the
            // bend math runs on local vertex positions. For directions/normals
            // (kind 1 or 2) inverse-transpose would be technically correct, but
            // for uniformly-scaled objects (the common case) inverse is fine.
            if (from == 2 && to == 0)
            {
                if (!ctx.IsVertexStage)
                    throw new System.NotImplementedException("World→Object transform supported in vertex stage only");
                string w = (kind == 0) ? "1.0" : "0.0";
                var rhs = Sg.RawTemplate("(inverse(v.model) * vec4({0}, " + w + ")).xyz", input.Rhs);
                return ctx.EmitTemp(SgType.Vec3, rhs);
            }

            throw new System.NotImplementedException(
                $"TransformNode space {from}->{to} (kind={kind}) not supported");
        }

        static int ReadInt(SgNode node, string outer, string inner)
        {
            if (!node.Raw.TryGetValue(outer, out var o) || !(o is Dictionary<string, object> d)) return 0;
            if (!d.TryGetValue(inner, out var v) || !(v is double dv)) return 0;
            return (int)dv;
        }
    }

    // ColorNode: a literal color baked into the graph (different from
    // PropertyNode for ColorShaderProperty — that one's an exposed material
    // input). Emits the inspector-set rgba as an inline vec4. The output
    // slot's type decides whether we drop the alpha (Color/RGB → vec3) or
    // keep it (Color/RGBA → vec4).
    class ColorNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            if (!node.Raw.TryGetValue("m_Color", out var mc) || !(mc is Dictionary<string, object> colorObj)
                || !colorObj.TryGetValue("color", out var c) || !(c is Dictionary<string, object> rgba))
                throw new System.Exception("ColorNode missing m_Color.color");

            double r = rgba.TryGetValue("r", out var v0) && v0 is double rr ? rr : 0;
            double g = rgba.TryGetValue("g", out var v1) && v1 is double gg ? gg : 0;
            double b = rgba.TryGetValue("b", out var v2) && v2 is double bb ? bb : 0;
            double a = rgba.TryGetValue("a", out var v3) && v3 is double aa ? aa : 1;

            // Slot type drives Vec3 vs Vec4 (some ColorNodes have an RGBA
            // output, others RGB).
            var slot = ctx.Graph.Slots[node.SlotByNum[outSlotNum]];
            var t = slot.StaticType();
            if (t == SgType.Vec3)
                return new SgValue { Rhs = Sg.LitConst(SgConst.Vec(SgType.Vec3, r, g, b)), Type = SgType.Vec3, IsTemp = false };
            return new SgValue { Rhs = Sg.LitConst(SgConst.Vec(SgType.Vec4, r, g, b, a)), Type = SgType.Vec4, IsTemp = false };
        }
    }

    // BlendNode: 22 modes selectable via m_BlendMode (UnityEditor.ShaderGraph.BlendMode).
    // Each mode computes Out from Base/Blend, then `Out = lerp(Base, Out, Opacity)`.
    // Slot order: 0 = Base, 1 = Blend, 2 = Out, 3 = Opacity.
    class BlendNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            int mode = 16; // Overlay (Unity default) when missing
            if (node.Raw.TryGetValue("m_BlendMode", out var m) && m is double d) mode = (int)d;

            var baseV  = ctx.InputByName(node, "Base");
            var blendV = ctx.InputByName(node, "Blend");
            var opV    = ctx.InputByName(node, "Opacity");

            // Result type = promote(Base, Blend) — same rule as PromoteAB.
            int rb = SgTypes.Rank(baseV.Type), rbl = SgTypes.Rank(blendV.Type);
            SgType outT = rb >= rbl ? baseV.Type : blendV.Type;

            // Inner = formula(Base, Blend). Final = mix(Base, Inner, Opacity).
            // Operands are rank-aligned so vec4/vec3/float mixes obey HLSL
            // truncation semantics (matches the engine GLSL compiler).
            string inner;
            switch (mode)
            {
                case 0:  inner = "(1.0 - (1.0 - {1})/({0} + 1e-12))"; break;                    // Burn
                case 1:  inner = "min({1}, {0})"; break;                                         // Darken
                case 2:  inner = "abs({1} - {0})"; break;                                        // Difference
                case 3:  inner = "({0} / (1.0 - clamp({1}, 1e-6, 0.999999)))"; break;            // Dodge
                case 4:  inner = "({0} / ({1} + 1e-12))"; break;                                 // Divide
                case 5:  inner = "({1} + {0} - 2.0 * {1} * {0})"; break;                         // Exclusion
                case 8:  inner = "max({1}, {0})"; break;                                         // Lighten
                case 9:  inner = "({0} + {1} - 1.0)"; break;                                     // LinearBurn
                case 10: inner = "({0} + {1})"; break;                                           // LinearDodge
                case 12: inner = "({1} + 2.0 * {0} - 1.0)"; break;                               // LinearLightAddSub
                case 13: inner = "({0} * {1})"; break;                                           // Multiply
                case 14: inner = "(1.0 - abs(1.0 - {1} - {0}))"; break;                          // Negation
                case 17: inner = "(step(0.5, {1}) * max(2.0 * ({0} - 0.5), {1}) + (1.0 - step(0.5, {1})) * min(2.0 * {0}, {1}))"; break; // PinLight
                case 15: inner = "(1.0 - (1.0 - {1}) * (1.0 - {0}))"; break;                     // Screen
                case 19: inner = "({0} - {1})"; break;                                           // Subtract
                case 21:                                                                          // Overwrite — passes through to mix(Base, Blend, Opacity)
                    return ctx.EmitTemp(outT, Sg.CallPromote("mix", baseV, blendV, opV));
                default:
                    // Unsupported modes (HardLight, HardMix, LinearLight, Overlay,
                    // SoftLight, VividLight) need step()-driven branching that's
                    // tedious; emit Screen as a visible fallback and a TODO.
                    inner = "(1.0 - (1.0 - {1}) * (1.0 - {0})) /* TODO BlendMode " + mode + " */";
                    break;
            }

            // Build the final mix(Base, formula, Opacity) with rank-aligned args.
            // Steps: emit a temp for `formula(Base, Blend)`, then mix(Base, that, Opacity).
            // Doing it as one nested expression keeps the optimizer happy.
            // Align Base + Blend to a common rank. Use MAX rank: vectors stay,
            // scalars get broadcast via vec_n(s). GLSL's min/max/clamp require
            // matching genType OR (genType, float) overloads in that order —
            // broadcasting upfront avoids both `min(float, vec)` and asymmetric-
            // operand bugs (Burn, Dodge, Subtract are non-commutative).
            int rankBB = System.Math.Max(SgTypes.Rank(baseV.Type), SgTypes.Rank(blendV.Type));
            string vecCtor = rankBB == 2 ? "vec2" : rankBB == 3 ? "vec3" : rankBB == 4 ? "vec4" : null;
            SgRhs Broadcast(SgValue v)
            {
                int r = SgTypes.Rank(v.Type);
                if (r == rankBB || vecCtor == null) return v.Rhs;
                if (r == 1) return new SgRhsCall { Func = vecCtor, Args = new List<SgRhs> { v.Rhs } };
                // r > 1 && r < rankBB: truncate (HLSL-style)
                if (r > rankBB) return Sg.Swizzle(v.Rhs, "xyzw".Substring(0, rankBB));
                // r < rankBB but r > 1: pad with zeros via vec_n(v, 0, ...). Rare.
                return new SgRhsCall { Func = vecCtor, Args = new List<SgRhs> { v.Rhs } };
            }
            var bAligned  = Broadcast(baseV);
            var blAligned = Broadcast(blendV);
            // Opacity stays scalar — mix(genType, genType, float) is a valid overload.
            var opAligned = opV.Rhs;

            var formula = Sg.RawTemplate(inner, bAligned, blAligned);
            var rhs = Sg.RawTemplate("mix({0}, {1}, {2})", bAligned, formula, opAligned);
            return ctx.EmitTemp(outT, rhs);
        }
    }

    // PositionNode: per-vertex position in the requested coordinate space.
    // Vertex-stage only. Object space → `v.pos`; World space → the model
    // matrix applied to v.pos. Other spaces (View, Tangent, AbsoluteWorld,
    // Screen) would need engine-side support and are unsupported for now.
    class PositionNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            int space = 0;
            if (node.Raw.TryGetValue("m_Space", out var s) && s is double d) space = (int)d;

            // In the fragment stage we only have the world-space position
            // varying (`v_world_pos` — Lit template only). Object/View/etc.
            // would need additional varyings; fall back to v_world_pos with
            // a comment so the user can spot it if the visual is off.
            // AbsoluteWorld (4) matches World (2) in URP without camera-relative
            // rendering, which is the engine's setup.
            if (!ctx.IsVertexStage)
            {
                if (space == 0)
                    return new SgValue { Rhs = Sg.Raw("v_object_pos"), Type = SgType.Vec3, IsTemp = false };
                if (space == 2 || space == 4)
                    return new SgValue { Rhs = Sg.Raw("v_world_pos"), Type = SgType.Vec3, IsTemp = false };
                return new SgValue
                {
                    Rhs = Sg.Raw($"v_world_pos /* TODO: PositionNode space {space} not implemented in @fs */"),
                    Type = SgType.Vec3,
                    IsTemp = false,
                };
            }

            switch (space)
            {
                case 0: // Object
                    return new SgValue { Rhs = Sg.Raw("v.pos"), Type = SgType.Vec3, IsTemp = false };
                case 2: // World
                case 4: // AbsoluteWorld — same as World w/o camera-relative rendering
                    return new SgValue { Rhs = Sg.Raw("(v.model * vec4(v.pos, 1.0)).xyz"), Type = SgType.Vec3, IsTemp = false };
                default:
                    throw new System.NotImplementedException($"PositionNode space {space} not supported");
            }
        }
    }

    // ScreenPositionNode: returns a homogeneous-form screen position so
    // downstream `.xy / .w` recovers the same UV/NDC Unity's shader would
    // produce. We reconstruct from gl_FragCoord (post-rasterizer) since the
    // engine doesn't carry clip-space position as a varying.
    //
    // m_ScreenSpaceType values (Unity ScreenSpaceType enum):
    //   0 Default — UV in [0,1] across the screen after perspective divide
    //   1 Raw     — NDC in [-1,1] after perspective divide
    //   2 Center  — same as Raw (Unity's "Center" maps clip.xy/clip.w to [-1,1])
    //   3 Tiled, 4 Pixel — not used by anything we ship; throw if hit.
    //
    // The engine's fragment-stage hz_scene UBO carries cluster.screen_width/
    // height (declared whenever HZ_RENDERER_LIGHTING_CLUSTERS is set, which
    // the LIT template does).
    class ScreenPositionNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            if (ctx.IsVertexStage)
                throw new NotImplementedException("ScreenPositionNode only supported in fragment stage");

            int mode = 0;
            if (node.Raw.TryGetValue("m_ScreenSpaceType", out var m) && m is double d) mode = (int)d;

            // Default matches Unity's HLSL output `(screen_uv.x, screen_uv.y,
            // 0, 0)` — already post-perspective-divide. Downstream SceneColor/
            // SceneDepth sample `.xy` directly. Raw/Center emit the homogeneous
            // (pre-divide) form so `.w = w_clip = view_distance` is usable
            // (DepthFade uses ScreenPos.w as view distance for the depth-gap
            // subtract).
            // gl_FragCoord is in framebuffer (dpr-scaled) pixels, but
            // cluster.screen_width/height are logical pixels — must divide by
            // dpr to land UVs in [0,1] (otherwise sampler wraps).
            string template;
            switch (mode)
            {
                case 0: // Default
                    template =
                        "vec4((gl_FragCoord.xy / hz_scene.cluster.dpr) / vec2(float(hz_scene.cluster.screen_width), float(hz_scene.cluster.screen_height)), 0.0, 0.0)";
                    break;
                case 1: // Raw
                case 2: // Center (same homogeneous form as Raw)
                    template =
                        "vec4((((gl_FragCoord.xy / hz_scene.cluster.dpr) / vec2(float(hz_scene.cluster.screen_width), float(hz_scene.cluster.screen_height))) * 2.0 - 1.0) / gl_FragCoord.w, " +
                        "gl_FragCoord.z / gl_FragCoord.w, 1.0 / gl_FragCoord.w)";
                    break;
                default:
                    throw new NotImplementedException($"ScreenPositionNode mode {mode} not supported");
            }
            return ctx.EmitTemp(SgType.Vec4, Sg.Raw(template));
        }
    }

    // TimeNode: scene-wide time uniform. Engine exposes a single `hz_scene.time`
    // float; we synthesise sin/cos from it. Delta-time variants are unsupported
    // (engine doesn't track them today).
    class TimeNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            switch (outSlotNum)
            {
                case 0: return Inline("hz_scene.time");
                case 1: return Inline("sin(hz_scene.time)");
                case 2: return Inline("cos(hz_scene.time)");
                default:
                    throw new System.NotImplementedException($"TimeNode slot {outSlotNum} not supported (engine has no delta time)");
            }
        }
        static SgValue Inline(string expr) =>
            new SgValue { Rhs = Sg.Raw(expr), Type = SgType.Float, IsTemp = false };
    }

    // PropertyNode: resolves to `mat.<fieldName>` for value-typed properties,
    // or to an SgRhsTextureRef for Texture2D properties (so SampleTexture2D
    // can extract the field name structurally).
    class PropertyNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            if (!node.Raw.TryGetValue("m_Property", out var pr) || !(pr is Dictionary<string, object> prd)
                || !prd.TryGetValue("m_Id", out var pid) || !(pid is string propId))
                throw new System.Exception("PropertyNode missing m_Property reference");
            if (!ctx.Graph.PropertiesById.TryGetValue(propId, out var prop))
                throw new System.Exception($"PropertyNode references unknown property '{propId}'");

            if (prop.Type == SgType.Texture2D)
                return new SgValue
                {
                    Rhs = new SgRhsTextureRef { FieldName = prop.FieldName, IsNormalMap = prop.IsNormalMap },
                    Type = SgType.Texture2D,
                    IsTemp = false,
                };

            // Inside a subgraph, properties are function parameters — emit
            // a bare identifier. Outside (main shader), they're reads of the
            // MaterialFS struct. Bool properties live in the engine struct
            // as `float` (engine constraint) — wrap with `> 0.5` so the
            // value flows back as a real bool.
            string baseExpr = ctx.IsSubGraph ? prop.FieldName : "mat." + prop.FieldName;
            if (prop.Type == SgType.Bool)
                return new SgValue
                {
                    Rhs = Sg.Raw("(" + baseExpr + " > 0.5)"),
                    Type = SgType.Bool,
                    IsTemp = false,
                };

            return new SgValue
            {
                Rhs = Sg.Raw(baseExpr),
                Type = prop.Type,
                IsTemp = false,
            };
        }
    }

    // SampleTexture2DNode: emits `texture_sample(<name>, mat.<name>, uv)`. The
    // texture name comes from inspecting the Texture input — it must trace
    // back to a Texture2D PropertyNode (we don't yet support inline texture
    // resources). UV defaults to v_uv when nothing is wired.
    //
    // Slot layout: 0=RGBA(out), 1=Texture(in), 2=UV(in), 3=Sampler(in),
    //              4=R(out), 5=G(out), 6=B(out), 7=A(out).
    class SampleTexture2DNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            if (outSlotNum == 0) return EmitSample(node, ctx);
            if (outSlotNum >= 4 && outSlotNum <= 7)
            {
                // Force-cache RGBA at slot 0 (LowerCtx caches by output slot
                // already; this is a recursive call into Output()) and pull a
                // component out via swizzle.
                var rgba = ctx.Output(node.Id, 0);
                string s; switch (outSlotNum)
                {
                    case 4: s = "x"; break; case 5: s = "y"; break;
                    case 6: s = "z"; break; case 7: s = "w"; break;
                    default: throw new System.Exception();
                }
                return ctx.EmitTemp(SgType.Float, Sg.Swizzle(rgba.Rhs, s));
            }
            throw new System.Exception($"SampleTexture2DNode: invalid output slot {outSlotNum}");
        }

        SgValue EmitSample(SgNode node, LowerCtx ctx)
        {
            var texInput = ctx.Input(node.Id, 1);
            if (!(texInput.Rhs is SgRhsTextureRef texRef))
                throw new System.NotImplementedException(
                    "SampleTexture2D: Texture input must come from a Texture2D PropertyNode");

            // UV: pull through ctx.Input so the slot's Vec2 static type
            // coerces vec4/vec3 upstreams via .xy. Default to v_uv when
            // unconnected (UVMaterialSlot's m_Value is (0,0) which would
            // produce vec2(0) literally — same as Unity's "no input" intent
            // is to use the per-vertex UV0 varying).
            SgRhs uvRhs;
            var uvEdge = ctx.Graph.FindEdgeIntoSlot(node.Id, 2);
            if (uvEdge != null)
            {
                var uv = ctx.Input(node.Id, 2);
                uvRhs = uv.Rhs;
            }
            else
            {
                uvRhs = Sg.Raw("v_uv");
            }

            // Use RawTemplate so the UV's structural ref survives optimizer
            // renumbering — Sg.Raw bakes the temp name into a string and goes
            // stale. For tangent-space normal-map properties (detected at
            // parse time, see SgProperty.IsNormalMap), route through the
            // engine's unpack_normal_bc5 helper. The engine cooks normal-map
            // textures as BC5 (RG-only, linear) so the B channel reads as 0 —
            // a naïve `.xyz * 2 - 1` would produce z = -1 (normals pointing
            // INTO the surface) and break every downstream TBN multiply.
            // unpack_normal_bc5 reads .rg, decodes, and reconstructs Z.
            string template = texRef.IsNormalMap
                ? $"vec4(unpack_normal_bc5(texture_sample({texRef.FieldName}, mat.{texRef.FieldName}, {{0}}).rg), 1.0)"
                : $"texture_sample({texRef.FieldName}, mat.{texRef.FieldName}, {{0}})";
            return ctx.EmitTemp(SgType.Vec4, Sg.RawTemplate(template, uvRhs));
        }
    }

    class SplitNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            if (outSlotNum < 1 || outSlotNum > 4)
                throw new Exception($"SplitNode: invalid output slot {outSlotNum}");
            var inV = ctx.Input(node.Id, 0);
            int rank = SgTypes.Rank(inV.Type);
            if (rank == 0) throw new NotImplementedException($"Split input type {inV.Type}");

            string suffix; switch (outSlotNum) { case 1: suffix = "x"; break; case 2: suffix = "y"; break;
                                                  case 3: suffix = "z"; break; case 4: suffix = "w"; break;
                                                  default: throw new Exception(); }

            if (outSlotNum > rank) return ctx.EmitTemp(SgType.Float, Sg.Lit(0.0));
            if (rank == 1)        return ctx.EmitTemp(SgType.Float, inV.Rhs);
            return ctx.EmitTemp(SgType.Float, Sg.Swizzle(inV.Rhs, suffix));
        }
    }

    // VertexColorNode: per-vertex color attribute. Single Vector4 output (RGBA).
    // The engine exposes `v_color` (vec4) as a fragment-stage varying — vertex
    // unpack fills (1,1,1,1) when the cooked mesh has no COLOR_0 attribute,
    // matching Unity's default for unauthored vertex colors.
    class VertexColorNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
            => new SgValue { Rhs = Sg.Raw(ctx.IsVertexStage ? "v.color" : "v_color"), Type = SgType.Vec4, IsTemp = false };
    }

    // BakedGINode: diffuse indirect irradiance. Maps to the engine's
    // `hz_baked_gi(world_pos, world_normal)` helper which samples the diffuse
    // probe (skybox cubemap @ max mip). Raw irradiance — NOT premultiplied by
    // albedo — matching Unity's BakedGINode contract.
    //
    // Slot layout per Unity's BakedGINode.cs:
    //   0=NormalWS (input), 1=Out (output), 2=PositionWS (input),
    //   3=StaticUV (input, ignored — no lightmap path), 4=DynamicUV (input, ignored).
    class BakedGINodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            // Inputs default to v_world_pos / v_normal when unconnected. Pull
            // them through ctx.Input so wired overrides flow correctly.
            SgRhs pos = HasEdge(node, 2, ctx) ? ctx.Input(node.Id, 2).Rhs : Sg.Raw("v_world_pos");
            SgRhs nrm = HasEdge(node, 0, ctx) ? ctx.Input(node.Id, 0).Rhs : Sg.Raw("v_normal");
            return ctx.EmitTemp(SgType.Vec3,
                Sg.RawTemplate("hz_baked_gi({0}, {1})", pos, nrm));
        }

        static bool HasEdge(SgNode node, int slotNum, LowerCtx ctx)
            => ctx.Graph.FindEdgeIntoSlot(node.Id, slotNum) != null;
    }

    // CombineNode: 4 scalar inputs (R/G/B/A) → 3 outputs (RGBA vec4, RGB vec3,
    // RG vec2). We emit a single vec4 temp once per CombineNode and route
    // each requested output slot to a swizzle into it. Cached on `ctx` keyed
    // by node id so repeated output reads share the temp.
    //
    // Slot ids per Unity: 0=R, 1=G, 2=B, 3=A, 4=RGBA, 5=RGB, 6=RG.
    class CombineNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            if (!ctx.CombineTemps.TryGetValue(node.Id, out var rgba))
            {
                var r = ctx.Input(node.Id, 0);
                var g = ctx.Input(node.Id, 1);
                var b = ctx.Input(node.Id, 2);
                var a = ctx.Input(node.Id, 3);
                rgba = ctx.EmitTemp(SgType.Vec4,
                    new SgRhsCall { Func = "vec4", Args = new List<SgRhs> { r.Rhs, g.Rhs, b.Rhs, a.Rhs } });
                ctx.CombineTemps[node.Id] = rgba;
            }
            switch (outSlotNum)
            {
                case 4: return new SgValue { Rhs = rgba.Rhs,                       Type = SgType.Vec4, IsTemp = false };
                case 5: return new SgValue { Rhs = Sg.Swizzle(rgba.Rhs, "xyz"),   Type = SgType.Vec3, IsTemp = false };
                case 6: return new SgValue { Rhs = Sg.Swizzle(rgba.Rhs, "xy"),    Type = SgType.Vec2, IsTemp = false };
                default: throw new Exception($"CombineNode: invalid output slot {outSlotNum}");
            }
        }
    }

    // GradientNoiseNode: 2D Perlin-style gradient noise. Single scalar output.
    // Unity provides two hash variants (Deterministic, LegacyMod) but the
    // visual difference is minor — we use a single implementation matching
    // Unity's "Deterministic" form, emitting a one-shot helper function the
    // first time this node is lowered in a stage.
    //
    // Slot ids: 0=UV (vec2, default v_uv), 1=Scale (float, default 10), 2=Out (float).
    class GradientNoiseNodeHandler : NodeHandler
    {
        const string HelperName = "_hz_gradient_noise";
        const string HelperBody =
@"vec2 _hz_gnoise_dir(vec2 p) {
    p = mod(p, vec2(289.0));
    float x = mod((34.0 * p.x + 1.0) * p.x, 289.0) + p.y;
    x = mod((34.0 * x + 1.0) * x, 289.0);
    x = fract(x / 41.0) * 2.0 - 1.0;
    return normalize(vec2(x - floor(x + 0.5), abs(x) - 0.5));
}
float _hz_gradient_noise(vec2 uv, float scale) {
    vec2 p = uv * scale;
    vec2 ip = floor(p);
    vec2 fp = fract(p);
    float d00 = dot(_hz_gnoise_dir(ip),                 fp);
    float d01 = dot(_hz_gnoise_dir(ip + vec2(0.0, 1.0)), fp - vec2(0.0, 1.0));
    float d10 = dot(_hz_gnoise_dir(ip + vec2(1.0, 0.0)), fp - vec2(1.0, 0.0));
    float d11 = dot(_hz_gnoise_dir(ip + vec2(1.0, 1.0)), fp - vec2(1.0, 1.0));
    fp = fp * fp * fp * (fp * (fp * 6.0 - 15.0) + 10.0);
    return mix(mix(d00, d01, fp.y), mix(d10, d11, fp.y), fp.x) + 0.5;
}";

        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            ctx.HelperFunctions.Add((HelperName, HelperBody));
            var uv = ctx.Input(node.Id, 0);
            var scale = ctx.Input(node.Id, 1);
            return ctx.EmitTemp(SgType.Float,
                Sg.RawTemplate($"{HelperName}({{0}}, {{1}})", uv.Rhs, scale.Rhs));
        }
    }

    // CustomFunctionNode: Unity HLSL plug-in node (e.g. `MainLight(WorldPos)`).
    // We can't translate arbitrary HLSL, so the export is gated by a
    // per-function-name mapping in shader_mapping.json `customFunctions` →
    // engine struct-returning helper. The struct's field names match the
    // node's output slot DisplayNames (snake-cased).
    //
    // First touch emits `<EngineStruct> _tN = hz_xxx(args…);`. Subsequent
    // touches for other output slots swizzle into the cached temp.
    class CustomFunctionNodeHandler : NodeHandler
    {
        public override SgValue Lower(SgNode node, int outSlotNum, LowerCtx ctx)
        {
            string fnName = node.Raw.TryGetValue("m_FunctionName", out var fnObj) ? fnObj as string : null;
            if (string.IsNullOrEmpty(fnName))
                throw new Exception("CustomFunctionNode has no m_FunctionName");

            var map = HZShaderMap.LookupCustomFunction(fnName);
            if (map == null)
                throw new NotImplementedException(
                    $"CustomFunctionNode '{fnName}' has no entry in shader_mapping.json `customFunctions` — add an `engineCall`/`structType`/`structFields` mapping or the function name will go through as-is is not supported");

            // Lookup or emit the call site once for this node.
            if (!ctx.CustomFunctionTemps.TryGetValue(node.Id, out var temp))
            {
                // Build the input argument list in slot-id order. Each "input"
                // slot in the node corresponds to an HLSL function parameter
                // (the node's output slots are out-params that we project as
                // struct fields).
                var inputSlots = new List<int>();
                foreach (var kv in node.SlotByNum)
                {
                    if (!ctx.Graph.Slots.TryGetValue(kv.Value, out var slot)) continue;
                    if (!slot.IsOutput) inputSlots.Add(kv.Key);
                }
                inputSlots.Sort();

                var args = new List<SgRhs>();
                foreach (var slotNum in inputSlots) args.Add(ctx.Input(node.Id, slotNum).Rhs);

                var callRhs = Sg.Call(map.engineCall, args.ToArray());
                temp = ctx.EmitStructTemp(map.structType, callRhs);
                ctx.CustomFunctionTemps[node.Id] = temp;
            }

            // Project the requested output slot via the mapped field name.
            if (!node.SlotByNum.TryGetValue(outSlotNum, out var slotId))
                throw new Exception($"CustomFunctionNode: slot {outSlotNum} not in node");
            var outSlot = ctx.Graph.Slots[slotId];
            string fieldName = SgNames.ToSnakeCase(outSlot.OutputName);
            if (map.structFields != null && !map.structFields.Contains(fieldName))
                throw new Exception(
                    $"CustomFunctionNode '{fnName}': slot '{outSlot.OutputName}' (-> '{fieldName}') not in mapping.structFields=[{string.Join(",", map.structFields)}]");

            return new SgValue
            {
                Rhs = Sg.Swizzle(temp.Rhs, fieldName),
                Type = outSlot.StaticType(),
                IsTemp = false,
            };
        }
    }

    // ===== registry ===========================================================

    static class SgNodes
    {
        static readonly Dictionary<string, NodeHandler> _handlers
            = new Dictionary<string, NodeHandler>();

        static SgNodes()
        {
            // ----- bulk-registered patterns (auto-derived from Unity's
            //       ShaderGraph package via tools/generate_sg_nodes.py) -------
            SgGeneratedNodes.RegisterAll(Pattern);

            // ----- manual patterns for nodes the auto-generator can't reach --
            // MultiplyNode isn't a CodeFunctionNode (it picks `*` vs `mul()`
            // based on operand types in C#); we always pick `*` since GLSL's
            // `*` does the right thing for vec*vec, vec*scalar, mat*vec, and
            // mat*mat (the operand-order convention also matches Unity's
            // matrix*matrix output, since both pre-multiply column vectors).
            Pattern("MultiplyNode", v => Sg.BinPromote("*", v[0], v[1]), ArithPromote, "A", "B");

            // BlendNode is mode-driven (m_BlendMode) — handled by a custom
            // handler below that reads the mode and emits the per-mode
            // formula. Auto-pattern would always emit Screen.

            // ContrastNode: HLSL body uses a `$precision midpoint = pow(0.5, 2.2)`
            // local — auto-gen skipped due to multi-statement. The midpoint
            // value (≈ 0.21763764) is constant; precompute and inline.
            Pattern("ContrastNode",
                v => Sg.RawTemplate("(({0} - 0.21763764) * {1} + 0.21763764)", v[0].Rhs, v[1].Rhs),
                SameAsIn, "In", "Contrast");

            // NormalBlendNode (default mode): HLSL uses Unity's SafeNormalize +
            // $precision3 macro. Functional GLSL form is normalize(vec3(rg
            // sum, b multiply)).
            Pattern("NormalBlendNode",
                v => Sg.RawTemplate("normalize(vec3({0}.rg + {1}.rg, {0}.b * {1}.b))", v[0].Rhs, v[1].Rhs),
                _ => SgType.Vec3, "A", "B");

            // NormalStrengthNode: HLSL uses $precision3 + lerp/saturate. GLSL
            // equivalent: vec3(rg * strength, mix(1, b, clamp(strength, 0, 1))).
            Pattern("NormalStrengthNode",
                v => Sg.RawTemplate("vec3({0}.rg * {1}, mix(1.0, {0}.b, clamp({1}, 0.0, 1.0)))", v[0].Rhs, v[1].Rhs),
                _ => SgType.Vec3, "In", "Strength");

            // ----- custom handlers --------------------------------------------
            // (these override any auto-generated entry with the same name)
            _handlers["UVNode"]              = new UVNodeHandler();
            _handlers["SplitNode"]           = new SplitNodeHandler();
            _handlers["Vector1Node"]         = new Vector1NodeHandler();
            _handlers["Vector2Node"]         = new VectorPackNodeHandler(2);
            _handlers["Vector3Node"]         = new VectorPackNodeHandler(3);
            _handlers["Vector4Node"]         = new VectorPackNodeHandler(4);
            _handlers["SubGraphNode"]        = new SubGraphNodeHandler();
            _handlers["PropertyNode"]            = new PropertyNodeHandler();
            _handlers["SampleTexture2DNode"]     = new SampleTexture2DNodeHandler();
            _handlers["PositionNode"]            = new PositionNodeHandler();
            _handlers["TimeNode"]                = new TimeNodeHandler();
            _handlers["NormalVectorNode"]        = new NormalVectorNodeHandler();
            _handlers["MainLightDirectionNode"]  = new MainLightDirectionNodeHandler();
            _handlers["GradientNode"]            = new GradientNodeHandler();
            _handlers["SampleGradient"]          = new SampleGradientHandler();
            _handlers["RedirectNodeData"]        = new RedirectNodeDataHandler();
            _handlers["ColorNode"]               = new ColorNodeHandler();
            _handlers["TransformNode"]           = new TransformNodeHandler();
            _handlers["KeywordNode"]             = new KeywordNodeHandler();
            _handlers["BlendNode"]               = new BlendNodeHandler();
            _handlers["VertexColorNode"]         = new VertexColorNodeHandler();
            _handlers["BakedGINode"]             = new BakedGINodeHandler();
            _handlers["CombineNode"]             = new CombineNodeHandler();
            _handlers["GradientNoiseNode"]       = new GradientNoiseNodeHandler();
            _handlers["CustomFunctionNode"]      = new CustomFunctionNodeHandler();
            _handlers["ScreenPositionNode"]      = new ScreenPositionNodeHandler();

            // Override SgGeneratedNodes' SceneColor / SceneDepth patterns that
            // emit Unity URP HLSL helpers (SHADERGRAPH_SAMPLE_SCENE_COLOR /
            // SHADERGRAPH_SAMPLE_SCENE_DEPTH / Linear01Depth) with engine-side
            // helpers. The UV input is expected to be the .xy of a
            // ScreenPosition output that's already been divided by .w (the
            // canonical Unity wiring); we just forward whatever vec2 was
            // wired in.
            Pattern("SceneColorNode",
                v => Sg.RawTemplate("hz_scene_color({0}.xy)", v[0].Rhs),
                _ => SgType.Vec3, "UV");
            Pattern("SceneDepthNode",
                v => Sg.RawTemplate("hz_scene_depth_linear({0}.xy)", v[0].Rhs),
                _ => SgType.Float, "UV");
        }

        public static NodeHandler Lookup(string nodeType)
        {
            if (_handlers.TryGetValue(nodeType, out var h)) return h;
            throw new NotImplementedException($"no handler registered for '{nodeType}'");
        }

        static void Pattern(string nodeType, Func<SgValue[], SgRhs> build,
                            Func<SgType[], SgType> outType, params string[] inputs)
        {
            _handlers[nodeType] = new PatternHandler
            {
                InputNames = inputs,
                Build = build,
                OutType = outType,
            };
        }

        static SgType PromoteAB(SgType[] inT) => SgTypes.Promote(inT[0], inT[1]);
        static SgType SameAsIn(SgType[] inT) => inT[0];

        // For arithmetic with mismatched-rank vectors, mimic HLSL's
        // mul-truncation — result is min-rank. Otherwise behave like Promote.
        static SgType ArithPromote(SgType[] inT)
        {
            int ra = SgTypes.Rank(inT[0]), rb = SgTypes.Rank(inT[1]);
            if (ra > 1 && rb > 1 && ra != rb)
                return SgTypes.FromRank(System.Math.Min(ra, rb));
            return SgTypes.Promote(inT[0], inT[1]);
        }

        // For multi-arg builtins routed through Sg.CallPromote (mix, smoothstep,
        // clamp, step, min, max, pow, mod), the call aligns all vector operands
        // to the minimum vector rank present. The output type must match —
        // i.e. min-rank over every operand with rank > 1. Scalars broadcast
        // and don't pull the rank down.
        static SgType PromoteAll(SgType[] inT)
        {
            int minRank = int.MaxValue;
            int maxRank = 0;
            foreach (var t in inT)
            {
                int r = SgTypes.Rank(t);
                if (r <= 0) continue;          // matrix / unknown — out of scope
                if (r > 1 && r < minRank) minRank = r;
                if (r > maxRank) maxRank = r;
            }
            if (minRank == int.MaxValue) return SgTypes.FromRank(maxRank > 0 ? maxRank : 1);
            return SgTypes.FromRank(minRank);
        }
    }
}
