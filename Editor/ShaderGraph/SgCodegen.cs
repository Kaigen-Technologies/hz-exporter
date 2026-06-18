using System.Collections.Generic;
using System.Text;

namespace HZExporter.ShaderGraph
{
    // Picks a stage template per (variant, …) and substitutes the per-block
    // expressions. Templates are kept as embedded constants here so the
    // skeleton lives in one place and isn't smeared across the orchestrator.
    static class SgCodegen
    {
        // Output of Emit. `Forward` is always populated; `Shadow` is non-null
        // only when the graph has alpha clip (so its shadow caster needs the
        // alpha test). Each is a full .hz.glsl source ready to write.
        public struct EmittedShaders {
            public string Forward;
            public string Shadow; // null when no separate shadow caster needed
            public string ForwardGrass; // non-null only for grass-capable Lit graphs
        }

        public static EmittedShaders Emit(SgGraph g, string sourceName)
        {
            var fwd = new StringBuilder();
            fwd.AppendLine($"// Generated from {sourceName}.shadergraph by HZShaderGraphExporter");

            // Keyword manifest. Engine compiler reads these to generate the
            // `#define` permutations / variant matrix. We list every keyword
            // declared on the graph (regardless of whether the lowering
            // ended up referencing it — variants the user may rely on
            // downstream) plus an explicit `referenced=` flag.
            foreach (var kw in g.Keywords)
            {
                if (kw.IsBoolean)
                    fwd.AppendLine($"// @keyword {kw.ReferenceName} Boolean default={(kw.DefaultValue == 0 ? "Off" : "On")}");
                else
                {
                    var entries = string.Join(",", kw.Entries.ConvertAll(e => e.ReferenceName));
                    string def = kw.DefaultValue >= 0 && kw.DefaultValue < kw.Entries.Count
                        ? kw.Entries[kw.DefaultValue].ReferenceName : "?";
                    fwd.AppendLine($"// @keyword {kw.ReferenceName} Enum entries={entries} default={def}");
                }
            }

            fwd.AppendLine("#include \"renderer.glsl\"");
            fwd.AppendLine();

            switch (g.Variant)
            {
                case SgVariant.Unlit: fwd.Append(EmitFromTemplate(g, UNLIT_TEMPLATE, isLit: false)); break;
                case SgVariant.Lit:   fwd.Append(EmitFromTemplate(g, LIT_TEMPLATE,   isLit: true));  break;
                default:
                    throw new System.NotImplementedException($"variant '{g.Variant}' not supported");
            }

            // Sibling shadow caster — only when the material is alpha-clipped.
            // Opaque materials use the engine's global standard_shadow pipeline
            // and don't need a per-material file.
            string shadow = null;
            if (g.AlphaClip)
            {
                var sh = new StringBuilder();
                sh.AppendLine($"// Generated from {sourceName}.shadergraph by HZShaderGraphExporter (shadow caster)");
                // Forward the graph's keyword manifest so the shadow shader's
                // variant matrix matches the forward shader's. Keywords that
                // don't affect the shadow body just compile to identical
                // bytecode in every variant — the cooker dedups by hash.
                foreach (var kw in g.Keywords)
                {
                    if (kw.IsBoolean)
                        sh.AppendLine($"// @keyword {kw.ReferenceName} Boolean default={(kw.DefaultValue == 0 ? "Off" : "On")}");
                    else
                    {
                        var entries = string.Join(",", kw.Entries.ConvertAll(e => e.ReferenceName));
                        string def = kw.DefaultValue >= 0 && kw.DefaultValue < kw.Entries.Count
                            ? kw.Entries[kw.DefaultValue].ReferenceName : "?";
                        sh.AppendLine($"// @keyword {kw.ReferenceName} Enum entries={entries} default={def}");
                    }
                }
                // HZ_RENDERER_SHADOW_PASS must be defined BEFORE the include
                // so renderer_shaders.glsl exposes hz_shadow_indices and
                // unpack_shadow_pass_instance_data() under that gate.
                sh.AppendLine("#define HZ_RENDERER_SHADOW_PASS");
                sh.AppendLine("#include \"renderer.glsl\"");
                sh.AppendLine();
                sh.Append(EmitShadowFromTemplate(g));
                shadow = sh.ToString();
            }

            // Grass sibling — same @fs/@material as the forward Lit shader, but a
            // grass-SSBO @vs (LIT_GRASS_TEMPLATE). Only for grass-capable Lit graphs.
            string grass = null;
            if (g.IsGrassCapable && g.Variant == SgVariant.Lit)
            {
                var fwdG = new StringBuilder();
                fwdG.AppendLine($"// Generated from {sourceName}.shadergraph by HZShaderGraphExporter (grass)");
                foreach (var kw in g.Keywords)
                {
                    if (kw.IsBoolean)
                        fwdG.AppendLine($"// @keyword {kw.ReferenceName} Boolean default={(kw.DefaultValue == 0 ? "Off" : "On")}");
                    else
                    {
                        var entries = string.Join(",", kw.Entries.ConvertAll(e => e.ReferenceName));
                        string def = kw.DefaultValue >= 0 && kw.DefaultValue < kw.Entries.Count
                            ? kw.Entries[kw.DefaultValue].ReferenceName : "?";
                        fwdG.AppendLine($"// @keyword {kw.ReferenceName} Enum entries={entries} default={def}");
                    }
                }
                fwdG.AppendLine("#include \"renderer.glsl\"");
                fwdG.AppendLine();
                fwdG.Append(EmitFromTemplate(g, LIT_GRASS_TEMPLATE, isLit: true));
                grass = fwdG.ToString();
            }

            return new EmittedShaders { Forward = fwd.ToString(), Shadow = shadow, ForwardGrass = grass };
        }

        // A surface assignment we'll emit at the end of main() — base_color =
        // …; metallic = …; etc. Carries an SgRhs so the optimizer's DCE can
        // count temp uses across both the statement list and these assigns.
        class Assign : SgOptimize.IAssignView
        {
            public string Symbol;
            public SgType Type;
            public SgRhs Rhs { get; set; }
            public string Format() => $"{SgTypes.Glsl(Type)} {Symbol} = {Rhs.ToGlsl()};";
        }

        static string EmitFromTemplate(SgGraph g, string template, bool isLit)
        {
            // ----- vertex stage --------------------------------------------
            var vsCtx = new LowerCtx(g) { IsVertexStage = true };
            var vsAssigns = new List<Assign>();
            string vsPos     = LowerVertexBlock(g, vsCtx, vsAssigns, "Position", "vertex_position", "v.pos");
            string vsNormal  = LowerVertexBlock(g, vsCtx, vsAssigns, "Normal",   "vertex_normal",   "v.normal");
            string vsTangent = LowerVertexBlock(g, vsCtx, vsAssigns, "Tangent",  "vertex_tangent",  "v.tangent");
            SgOptimize.Run(vsCtx.Statements, vsAssigns);

            // ----- fragment stage ------------------------------------------
            var ctx = new LowerCtx(g);
            var assigns = new List<Assign>();

            assigns.Add(LowerSurface(g, ctx, "BaseColor", "base_color", SgType.Vec3, Sg.LitConst(SgConst.Vec(SgType.Vec3, 0.5, 0.5, 0.5))));
            if (isLit)
            {
                assigns.Add(LowerSurface(g, ctx, "Metallic",   "metallic",   SgType.Float, Sg.Lit(0.0)));
                assigns.Add(LowerSurface(g, ctx, "Smoothness", "smoothness", SgType.Float, Sg.Lit(0.5)));
                assigns.Add(LowerSurface(g, ctx, "NormalTS",   "normal_ts",  SgType.Vec3,  Sg.LitConst(SgConst.Vec(SgType.Vec3, 0.0, 0.0, 1.0))));
                assigns.Add(LowerSurface(g, ctx, "Emission",   "emission",   SgType.Vec3,  Sg.LitConst(SgConst.Vec(SgType.Vec3, 0.0, 0.0, 0.0))));
                assigns.Add(LowerSurface(g, ctx, "Occlusion",  "occlusion",  SgType.Float, Sg.Lit(1.0)));
            }
            // Alpha clip: lower Alpha + AlphaClipThreshold into local
            // assigns; the discard line is appended verbatim after the
            // optimizer runs.
            if (g.AlphaClip)
            {
                assigns.Add(LowerSurface(g, ctx, "Alpha",               "alpha",                SgType.Float, Sg.Lit(1.0)));
                assigns.Add(LowerSurface(g, ctx, "AlphaClipThreshold",  "alpha_clip_threshold", SgType.Float, Sg.Lit(0.5)));
            }
            SgOptimize.Run(ctx.Statements, assigns);

            // ----- functions (subgraphs from either stage, deduped) --------
            var allFunctions = new List<SgFunctionDef>();
            var seenFnNames = new HashSet<string>();
            foreach (var fn in vsCtx.Functions) if (seenFnNames.Add(fn.Name)) allFunctions.Add(fn);
            foreach (var fn in ctx.Functions)   if (seenFnNames.Add(fn.Name)) allFunctions.Add(fn);
            foreach (var fn in allFunctions)
            {
                var roots = new List<SgOptimize.IAssignView>(fn.Outputs.Count);
                foreach (var o in fn.Outputs) roots.Add(new FunctionOutputView(o));
                SgOptimize.Run(fn.Body, roots);
            }

            // FormatFunction returns "...}\n"; the extra "\n" makes a blank
            // line between functions and between the last function and main().
            var functions = new StringBuilder();
            foreach (var fn in allFunctions) functions.Append(FormatFunction(fn)).Append('\n');

            // Helper functions (e.g. _hz_gradient_noise) — emit per-stage so
            // each compilation unit (`@vs` and `@fs` are independent SPIR-V
            // units) gets the definitions it actually needs. Vegetation's
            // wind sway calls _hz_gradient_noise from the vertex stage; a
            // fragment-only declaration there would error with "no matching
            // overloaded function" on every vertex call site.
            var fsHelpers = new StringBuilder();
            foreach (var h in ctx.HelperFunctions) fsHelpers.Append(h.body).Append("\n\n");
            var vsHelpers = new StringBuilder();
            foreach (var h in vsCtx.HelperFunctions) vsHelpers.Append(h.body).Append("\n\n");

            // ----- format bodies -------------------------------------------
            var vsBody = new StringBuilder();
            EmitStatementsWithGroupHeaders(vsBody, vsCtx.Statements, g);
            foreach (var a in vsAssigns)        vsBody.Append("    ").Append(a.Format()).Append('\n');

            var body = new StringBuilder();
            EmitStickyNotes(body, g);
            EmitStatementsWithGroupHeaders(body, ctx.Statements, g);
            foreach (var a in assigns)        body.Append("    ").Append(a.Format()).Append('\n');
            if (g.AlphaClip)
                body.Append("    if (alpha < alpha_clip_threshold) discard;\n");
            string bodyStr = body.ToString().TrimEnd('\n');

            // Only emit the vertex-stage material mirror when something in
            // the vertex stage actually reads material data. Detect via the
            // `mat.` substring in the rendered vsBody (the optimizer leaves
            // those references intact since `mat` is an opaque struct ref).
            string vsBodyStr = vsBody.ToString();
            bool vsNeedsMaterial = vsBodyStr.Contains("mat.");
            string vsMaterial = vsNeedsMaterial ? FormatVertexMaterialMirror(g) : "";
            string vsMatUnpack = vsNeedsMaterial
                ? "    MaterialFS mat = MaterialFS_unpack(v.material_offset);\n"
                : "";

            // Engine feature defines that gate `hz_scene_color` /
            // `hz_scene_depth_linear` (and the underlying *_sample macros)
            // inside renderer_shaders.glsl. Only emit them when the fragment
            // body OR any emitted subgraph/helper function calls them;
            // otherwise we'd force the renderer to bind a scene-color copy /
            // depth SRV unnecessarily. Subgraphs (e.g. DepthFade) inline the
            // helper inside the function body, so we must scan that string too.
            string fsScan = bodyStr + functions.ToString() + fsHelpers.ToString();
            var fsDefines = new StringBuilder();
            if (fsScan.Contains("hz_scene_color"))        fsDefines.AppendLine("#define HZ_RENDERER_SCENE_COLOR");
            if (fsScan.Contains("hz_scene_depth_linear")) fsDefines.AppendLine("#define HZ_RENDERER_SCENE_DEPTH");

            // Lit graphs that leave Base Color unwired are "implicit unlit" (their
            // output is driven through Emission). Skip the PBR lighting block and
            // start from black — the tail (occlusion/emission/fog) carries it.
            string lighting = isLit
                ? (IsBaseColorWired(g) ? LIT_LIGHTING : "    vec3 lit = vec3(0.0);\n")
                : "";

            return template
                .Replace("${LIGHTING}", lighting)
                .Replace("${MATERIAL_STRUCT}", FormatMaterialStruct(g))
                .Replace("${VS_MATERIAL}",    vsMaterial)
                .Replace("${VS_MAT_UNPACK}",  vsMatUnpack)
                .Replace("${VS_HELPERS}",   vsHelpers.ToString())
                .Replace("${HELPERS}",      fsHelpers.ToString())
                .Replace("${FS_FEATURE_DEFINES}", fsDefines.ToString())
                .Replace("${FUNCTIONS}", functions.ToString())
                .Replace("${VERTEX_BODY}", vsBodyStr)
                .Replace("${VS_POS}",      vsPos)
                .Replace("${VS_NORMAL}",   vsNormal)
                .Replace("${VS_TANGENT}",  vsTangent)
                .Replace("${MAIN_BODY}",   bodyStr);
        }

        // Emit the sibling shadow caster source. Same vertex displacement
        // body as the forward shader (substituted from the same lowered
        // ${VERTEX_BODY} so wind/bend stays in sync), and a fragment stage
        // that runs only the alpha-clip slice + discard. Only called when
        // g.AlphaClip is true.
        static string EmitShadowFromTemplate(SgGraph g)
        {
            // Reuse the same vertex lowering as the forward shader so the
            // shadow geometry follows the same wind/bend computation.
            var vsCtx = new LowerCtx(g) { IsVertexStage = true };
            var vsAssigns = new List<Assign>();
            string vsPos = LowerVertexBlock(g, vsCtx, vsAssigns, "Position", "vertex_position", "v.pos");
            // Normal/Tangent aren't sampled by the shadow @fs, but the
            // vertex lowering shares one ctx — we just don't substitute
            // them. Running the lowering keeps the temp numbering and
            // optimizer pass identical to the forward path.
            LowerVertexBlock(g, vsCtx, vsAssigns, "Normal",  "vertex_normal",  "v.normal");
            LowerVertexBlock(g, vsCtx, vsAssigns, "Tangent", "vertex_tangent", "v.tangent");
            SgOptimize.Run(vsCtx.Statements, vsAssigns);

            // Shadow fragment lowering: only Alpha + AlphaClipThreshold as
            // DCE roots — everything else (base_color, emission, lighting)
            // is pruned out.
            var ctx = new LowerCtx(g);
            var assigns = new List<Assign>();
            assigns.Add(LowerSurface(g, ctx, "Alpha",              "alpha",                SgType.Float, Sg.Lit(1.0)));
            assigns.Add(LowerSurface(g, ctx, "AlphaClipThreshold", "alpha_clip_threshold", SgType.Float, Sg.Lit(0.5)));
            SgOptimize.Run(ctx.Statements, assigns);

            // Functions + helpers — alpha lowering rarely uses subgraphs, but
            // if it does, emit only what its DCE kept.
            var allFunctions = new List<SgFunctionDef>();
            var seenFnNames = new HashSet<string>();
            foreach (var fn in vsCtx.Functions) if (seenFnNames.Add(fn.Name)) allFunctions.Add(fn);
            foreach (var fn in ctx.Functions)   if (seenFnNames.Add(fn.Name)) allFunctions.Add(fn);
            foreach (var fn in allFunctions)
            {
                var roots = new List<SgOptimize.IAssignView>(fn.Outputs.Count);
                foreach (var o in fn.Outputs) roots.Add(new FunctionOutputView(o));
                SgOptimize.Run(fn.Body, roots);
            }
            var functions = new StringBuilder();
            foreach (var fn in allFunctions) functions.Append(FormatFunction(fn)).Append('\n');

            var fsHelpers = new StringBuilder();
            foreach (var h in ctx.HelperFunctions) fsHelpers.Append(h.body).Append("\n\n");
            var vsHelpers = new StringBuilder();
            foreach (var h in vsCtx.HelperFunctions) vsHelpers.Append(h.body).Append("\n\n");

            var vsBody = new StringBuilder();
            EmitStatementsWithGroupHeaders(vsBody, vsCtx.Statements, g);
            foreach (var a in vsAssigns) vsBody.Append("    ").Append(a.Format()).Append('\n');

            var body = new StringBuilder();
            EmitStatementsWithGroupHeaders(body, ctx.Statements, g);
            foreach (var a in assigns) body.Append("    ").Append(a.Format()).Append('\n');
            body.Append("    if (alpha < alpha_clip_threshold) discard;\n");

            string vsBodyStr = vsBody.ToString();
            bool vsNeedsMaterial = vsBodyStr.Contains("mat.");
            string vsMaterial = vsNeedsMaterial ? FormatVertexMaterialMirror(g) : "";
            string vsMatUnpack = vsNeedsMaterial
                ? "    MaterialFS mat = MaterialFS_unpack(v.material_offset);\n"
                : "";

            return SHADOW_TEMPLATE
                .Replace("${MATERIAL_STRUCT}", FormatMaterialStruct(g))
                .Replace("${VS_MATERIAL}",     vsMaterial)
                .Replace("${VS_MAT_UNPACK}",   vsMatUnpack)
                .Replace("${VS_HELPERS}",      vsHelpers.ToString())
                .Replace("${SHADOW_HELPERS}",  fsHelpers.ToString())
                .Replace("${SHADOW_FUNCTIONS}", functions.ToString())
                .Replace("${VERTEX_BODY}",     vsBodyStr)
                .Replace("${VS_POS}",          vsPos)
                .Replace("${SHADOW_BODY}",     body.ToString().TrimEnd('\n'));
        }

        // Emit statements with `// === <group title> ===` headers injected
        // whenever the originating ShaderGraph group changes. Statements
        // without a group ("ungrouped") pass through without a header. The
        // first group transition emits a header; subsequent statements from
        // the same group don't repeat it. Group titles may reappear if the
        // optimizer interleaved their statements with statements from other
        // groups — that's expected and visually accurate.
        static void EmitStatementsWithGroupHeaders(StringBuilder sb, List<SgStatement> stmts, SgGraph g)
        {
            string lastGroup = null;
            foreach (var s in stmts)
            {
                string gid = s.GroupId;
                string title = (gid != null && g.GroupTitles.TryGetValue(gid, out var t)) ? t : null;
                if (title != null && gid != lastGroup)
                    sb.Append("    // === ").Append(title).Append(" ===\n");
                lastGroup = gid;   // also clear when we re-enter "no group"
                sb.Append("    ").Append(s.Format()).Append('\n');
            }
        }

        // Sticky notes as a top-of-main /* ... */ block. They have no
        // graph dependency so there's no good in-line position for them;
        // collecting them above main() keeps the noise predictable.
        static void EmitStickyNotes(StringBuilder sb, SgGraph g)
        {
            if (g.StickyNotes == null || g.StickyNotes.Count == 0) return;
            foreach (var note in g.StickyNotes)
            {
                bool hasTitle = !string.IsNullOrEmpty(note.Title);
                bool hasContent = !string.IsNullOrEmpty(note.Content);
                if (!hasTitle && !hasContent) continue;
                sb.Append("    /* ");
                if (hasTitle) sb.Append(note.Title);
                if (hasTitle && hasContent) sb.Append(":");
                sb.Append('\n');
                if (hasContent)
                    foreach (var line in note.Content.Replace("\r", "").Split('\n'))
                        sb.Append("     * ").Append(line).Append('\n');
                sb.Append("     */\n");
            }
        }

        // Lower a VertexDescription block. If the block has no incoming edge,
        // the block is a no-op — return the raw default (`v.pos` etc.) and
        // emit no statement. If wired, lower the chain into an Assign and
        // return the symbol name to substitute.
        static string LowerVertexBlock(SgGraph g, LowerCtx ctx, List<Assign> assigns,
                                       string blockName, string symbol, string fallback)
        {
            var block = g.FindVertexBlock("VertexDescription." + blockName);
            if (block == null || block.SlotByNum.Count == 0) return fallback;

            int numId = 0;
            foreach (var k in block.SlotByNum.Keys) { numId = k; break; }
            if (g.FindEdgeIntoSlot(block.Id, numId) == null) return fallback;

            var v = ctx.Input(block.Id, numId);
            var rhs = CoerceRhs(v.Rhs, v.Type, SgType.Vec3);
            assigns.Add(new Assign { Symbol = symbol, Type = SgType.Vec3, Rhs = rhs });
            return symbol;
        }

        // Wraps a function output so SgOptimize can treat its Rhs as a live
        // root (DCE keeps anything reachable from it).
        class FunctionOutputView : SgOptimize.IAssignView
        {
            readonly SgFunctionOutput _o;
            public FunctionOutputView(SgFunctionOutput o) { _o = o; }
            public SgRhs Rhs { get => _o.Rhs; set => _o.Rhs = value; }
        }

        // MaterialFS struct: one field per declared property, in declaration
        // order. Texture properties get the @texture2D annotation. Graphs
        // with no exposed properties still get a single placeholder field
        // (the engine compiler requires at least one).
        // The engine's preprocessor expands the @material block in @fs only —
        // it emits the MaterialFS struct, DECLARE_TEXTURE() calls, and the
        // MaterialFS_unpack() helper. When a graph reads material uniforms in
        // its vertex stage (wind/bend displacement, etc.) we have to mirror
        // the struct + unpack as plain GLSL since hz_materials.data[] is
        // bound in both stages but MaterialFS isn't. We use the same struct
        // name so `mat.X` references emit identically across stages.
        //
        // Texture fields stay as `uint` (just storage indices); the actual
        // sampler bindings are fragment-only and the vertex stage shouldn't
        // sample anyway. Bool fields are stored as `float` to match the
        // fragment-side rewrite in FormatMaterialStruct.
        static string FormatVertexMaterialMirror(SgGraph g)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("// vertex-stage material accessors (mirrors the @fs MaterialFS that the");
            sb.AppendLine("// engine preprocessor generates — needed when the graph's vertex stage");
            sb.AppendLine("// reads material uniforms, e.g. wind/bend displacement).");
            sb.AppendLine("struct MaterialFS {");
            if (g.Properties.Count == 0)
            {
                sb.AppendLine("    vec4 _placeholder;");
            }
            else
            {
                foreach (var p in g.Properties)
                {
                    if (p.Type == SgType.SamplerState) continue;
                    string ty = p.Type == SgType.Texture2D ? "uint"
                              : p.Type == SgType.Bool      ? "float"
                                                            : SgTypes.Glsl(p.Type);
                    sb.Append("    ").Append(ty).Append(' ').Append(p.FieldName).AppendLine(";");
                }
            }
            sb.AppendLine("};");
            sb.AppendLine("MaterialFS MaterialFS_unpack(uint material_offset) {");
            sb.AppendLine("    uint mbase = material_offset;");
            sb.AppendLine("    MaterialFS m;");
            foreach (var p in g.Properties)
            {
                if (p.Type == SgType.SamplerState) continue;
                string field = "m." + p.FieldName;
                if (p.Type == SgType.Texture2D)
                {
                    sb.AppendLine($"    {field} = hz_materials.data[mbase++];");
                }
                else if (p.Type == SgType.Float || p.Type == SgType.Bool)
                {
                    sb.AppendLine($"    {field} = uintBitsToFloat(hz_materials.data[mbase++]);");
                }
                else if (p.Type == SgType.Vec2)
                {
                    sb.AppendLine($"    {field} = vec2(uintBitsToFloat(hz_materials.data[mbase++]), uintBitsToFloat(hz_materials.data[mbase++]));");
                }
                else if (p.Type == SgType.Vec3)
                {
                    sb.AppendLine($"    {field} = vec3(uintBitsToFloat(hz_materials.data[mbase++]), uintBitsToFloat(hz_materials.data[mbase++]), uintBitsToFloat(hz_materials.data[mbase++]));");
                }
                else if (p.Type == SgType.Vec4)
                {
                    sb.AppendLine($"    {field} = vec4(uintBitsToFloat(hz_materials.data[mbase++]), uintBitsToFloat(hz_materials.data[mbase++]), uintBitsToFloat(hz_materials.data[mbase++]), uintBitsToFloat(hz_materials.data[mbase++]));");
                }
                else
                {
                    // Fall-through: emit a comment so the gap is visible.
                    sb.AppendLine($"    // TODO: vs material unpack for type {p.Type} on field '{p.FieldName}'");
                }
            }
            sb.AppendLine("    return m;");
            sb.AppendLine("}");
            return sb.ToString();
        }

        static string FormatMaterialStruct(SgGraph g)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@material");
            sb.AppendLine("struct MaterialFS {");
            if (g.Properties.Count == 0)
            {
                sb.AppendLine("    vec4 _placeholder;");
            }
            else
            {
                foreach (var p in g.Properties)
                {
                    if (p.Type == SgType.Texture2D)
                    {
                        sb.AppendLine("    @texture2D");
                        sb.Append("    uint ").Append(p.FieldName).AppendLine(";");
                    }
                    else if (p.Type == SgType.SamplerState)
                    {
                        // engine uses a default sampler — skip
                    }
                    else if (p.Type == SgType.Bool)
                    {
                        // Engine's MaterialFS preprocessor doesn't support
                        // bool fields — store as float and convert at access.
                        sb.Append("    float ").Append(p.FieldName).AppendLine(";");
                    }
                    else
                    {
                        sb.Append("    ").Append(SgTypes.Glsl(p.Type)).Append(' ')
                          .Append(p.FieldName).AppendLine(";");
                    }
                }
            }
            sb.AppendLine("};");
            return sb.ToString();
        }

        static string FormatFunction(SgFunctionDef fn)
        {
            // Parameter list — shared by both single- and struct-return forms.
            var paramStrs = new string[fn.Params.Count];
            for (int i = 0; i < fn.Params.Count; i++)
                paramStrs[i] = SgTypes.Glsl(fn.Params[i].Type) + " " + fn.Params[i].Name;
            string paramList = string.Join(", ", paramStrs);

            var sb = new StringBuilder();
            if (fn.StructName != null)
            {
                sb.Append("struct ").Append(fn.StructName).Append(" {\n");
                foreach (var o in fn.Outputs)
                    sb.Append("    ").Append(SgTypes.Glsl(o.Type)).Append(' ').Append(o.Name).Append(";\n");
                sb.Append("};\n\n");

                sb.Append(fn.StructName).Append(' ').Append(fn.Name)
                  .Append('(').Append(paramList).Append(") {\n");
                foreach (var s in fn.Body) sb.Append("    ").Append(s.Format()).Append('\n');
                var args = new string[fn.Outputs.Count];
                for (int i = 0; i < fn.Outputs.Count; i++) args[i] = fn.Outputs[i].Rhs.ToGlsl();
                sb.Append("    return ").Append(fn.StructName).Append('(').Append(string.Join(", ", args)).Append(");\n");
                sb.Append("}\n");
            }
            else
            {
                var o = fn.Outputs[0];
                sb.Append(SgTypes.Glsl(o.Type)).Append(' ').Append(fn.Name)
                  .Append('(').Append(paramList).Append(") {\n");
                foreach (var s in fn.Body) sb.Append("    ").Append(s.Format()).Append('\n');
                sb.Append("    return ").Append(o.Rhs.ToGlsl()).Append(";\n");
                sb.Append("}\n");
            }
            return sb.ToString();
        }

        // True when the fragment Base Color block has an incoming edge. When it's
        // unwired the graph is treated as "implicit unlit" — the PBR lighting
        // block is skipped and output flows through Emission. Mirrors the
        // edge-check in LowerSurface.
        static bool IsBaseColorWired(SgGraph g)
        {
            var block = g.FindFragmentBlock("SurfaceDescription.BaseColor");
            if (block == null || block.SlotByNum.Count == 0) return false;
            int numId = 0;
            foreach (var k in block.SlotByNum.Keys) { numId = k; break; }
            return g.FindEdgeIntoSlot(block.Id, numId) != null;
        }

        // Lower a SurfaceDescription block into an Assign. Falls back to the
        // provided literal RHS when the block has no input.
        static Assign LowerSurface(SgGraph g, LowerCtx ctx, string blockName,
                                   string symbol, SgType targetT, SgRhs fallbackRhs)
        {
            var block = g.FindFragmentBlock("SurfaceDescription." + blockName);
            if (block == null || block.SlotByNum.Count == 0)
                return new Assign { Symbol = symbol, Type = targetT, Rhs = fallbackRhs };

            int numId = 0;
            foreach (var k in block.SlotByNum.Keys) { numId = k; break; }
            var v = ctx.Input(block.Id, numId);

            // Coerce at the SgRhs level when we can; fall back to a Raw wrap.
            var rhs = CoerceRhs(v.Rhs, v.Type, targetT);
            return new Assign { Symbol = symbol, Type = targetT, Rhs = rhs };
        }

        // Public entry point used by SubGraphNodeHandler when coercing a
        // subgraph output to its declared SubGraphOutputNode slot type.
        public static SgRhs CoerceRhsForReturn(SgRhs r, SgType from, SgType to) => CoerceRhs(r, from, to);

        // Type-coercion at the structured level. Mirrors SgTypes.Coerce but
        // produces an SgRhs — so the optimizer can keep folding through.
        static SgRhs CoerceRhs(SgRhs r, SgType from, SgType to)
        {
            if (from == to) return r;
            // float → vec_n: broadcast via constructor.
            if (from == SgType.Float)
            {
                if (to == SgType.Vec2 || to == SgType.Vec3 || to == SgType.Vec4)
                    return Sg.Call(SgTypes.Glsl(to), r);
            }
            // vec_n → smaller via swizzle, → larger via constructor with 0 padding.
            int rf = SgTypes.Rank(from), rt = SgTypes.Rank(to);
            if (rf > 0 && rt > 0)
            {
                if (rt < rf) return Sg.Swizzle(r, "xyzw".Substring(0, rt));
                if (rt > rf)
                {
                    var args = new SgRhs[1 + (rt - rf)];
                    args[0] = r;
                    for (int i = 1; i < args.Length; i++) args[i] = Sg.Lit(0.0);
                    return Sg.Call(SgTypes.Glsl(to), args);
                }
            }
            // Fallback — let the GLSL coercer produce a string and wrap.
            return Sg.Raw(SgTypes.Coerce(r.ToGlsl(), from, to));
        }

        // ===== templates =======================================================

        // The body block is what changes per shader; everything around it (vs
        // outputs, fs inputs, lighting calls, MaterialFS) is fixed for a given
        // variant. As we add features (alpha clip, transparency, skinned, …)
        // we pick from a small set of templates rather than branching the
        // single one.
        const string UNLIT_TEMPLATE =
@"@vs
out vec2 v_uv;
out vec2 v_uv1;
out vec4 v_color;
flat out uint v_material_offset;
out vec3 v_normal;
${VS_MATERIAL}
${VS_HELPERS}
void main() {
    VertexInstanceData v = unpack_instance_data();
${VS_MAT_UNPACK}${VERTEX_BODY}    vec4 world_pos = v.model * vec4(${VS_POS}, 1.0);
    gl_Position = hz_scene.view_proj * world_pos;
    v_uv = v.uv;
    v_uv1 = vec2(v.uv1.x, 1.0 - v.uv1.y);
    v_color = v.color;
    v_material_offset = v.material_offset;
    v_normal = normalize((v.model * vec4(${VS_NORMAL}, 0.0)).xyz);
}
@end

@fs
#define HZ_RENDERER_LIGHTING_BASIC
${FS_FEATURE_DEFINES}in vec2 v_uv;
in vec2 v_uv1;
in vec4 v_color;
flat in uint v_material_offset;
in vec3 v_normal;
out vec4 frag_color;

${MATERIAL_STRUCT}
${HELPERS}${FUNCTIONS}void main() {
    MaterialFS mat = MaterialFS_unpack(v_material_offset);
${MAIN_BODY}
    frag_color = vec4(base_color, 1.0);
}
@end
";

        const string LIT_TEMPLATE =
@"@vs
#define HZ_RENDERER_LIGHTING_CLUSTERS
out vec3 v_world_pos;
out vec3 v_object_pos;
out vec3 v_normal;
out vec2 v_uv;
out vec2 v_uv1;
out vec4 v_color;
flat out uint v_material_offset;
out vec3 v_tangent;
out float v_bitangent_sign;
out float v_view_depth;
${VS_MATERIAL}
${VS_HELPERS}
void main() {
    VertexInstanceData v = unpack_instance_data();
${VS_MAT_UNPACK}${VERTEX_BODY}    vec4 world_pos = v.model * vec4(${VS_POS}, 1.0);
    gl_Position = hz_scene.view_proj * world_pos;
    v_world_pos = world_pos.xyz;
    v_object_pos = ${VS_POS};
    v_normal = normalize((v.model * vec4(${VS_NORMAL}, 0.0)).xyz);
    v_tangent = normalize((v.model * vec4(${VS_TANGENT}, 0.0)).xyz);
    v_bitangent_sign = v.bitangent_sign;
    v_uv = v.uv;
    v_uv1 = vec2(v.uv1.x, 1.0 - v.uv1.y);
    v_color = v.color;
    v_material_offset = v.material_offset;
    v_view_depth = -(hz_scene.view * world_pos).z;
}
@end

@fs
#define HZ_RENDERER_SHADOWS
#define HZ_RENDERER_IBL
#define HZ_RENDERER_LIGHTING_CLUSTERS
#define HZ_RENDERER_FOG
${FS_FEATURE_DEFINES}in vec3 v_world_pos;
in vec3 v_object_pos;
in vec3 v_normal;
in vec2 v_uv;
in vec2 v_uv1;
in vec4 v_color;
flat in uint v_material_offset;
in vec3 v_tangent;
in float v_bitangent_sign;
in float v_view_depth;
out vec4 frag_color;

${MATERIAL_STRUCT}
${HELPERS}${FUNCTIONS}void main() {
    MaterialFS mat = MaterialFS_unpack(v_material_offset);
${MAIN_BODY}
${LIGHTING}
    lit *= occlusion;
    lit += emission;
    lit = hz_apply_fog(lit, v_view_depth);
    frag_color = vec4(lit, 1.0);
}
@end
";

        // Grass variant of LIT_TEMPLATE: the @fs/@material blocks are byte-identical
        // to LIT_TEMPLATE, but the @vs is fully literal and pulls geometry from the
        // grass SSBOs (grass_terrain_pull.glsl) instead of unpack_instance_data().
        // It intentionally omits the graph's vertex substitutions (${VERTEX_BODY},
        // ${VS_POS}, ${VS_NORMAL}, ${VS_TANGENT}, ${VS_MATERIAL}, ${VS_MAT_UNPACK},
        // ${VS_HELPERS}) — EmitFromTemplate's .Replace of those is a harmless no-op
        // when absent. Materials used on grass point shader_source at this file.
        const string LIT_GRASS_TEMPLATE =
@"@vs
#define HZ_RENDERER_TERRAIN
#define HZ_RENDERER_GRASS
#include ""grass_terrain_pull.glsl""
out vec3 v_world_pos;
out vec3 v_object_pos;
out vec3 v_normal;
out vec2 v_uv;
out vec2 v_uv1;
out vec4 v_color;
flat out uint v_material_offset;
out vec3 v_tangent;
out float v_bitangent_sign;
out float v_view_depth;
void main() {
    uint mo = hz_grass_draw.data[0u];
    GrassVsParams p = grass_vs_params();
    uint vb = uint(gl_VertexIndex) * 2u;
    vec3 vpos = grass_unpack_pos_rgb10a2(hz_grass_vertices.data[vb + 0u], p.pos_min, p.pos_extent);
    vec2 vuv = unpackHalf2x16(hz_grass_vertices.data[vb + 1u]);
    uint ib = HZ_INSTANCE_INDEX * 2u;
    vec2 world_xz = unpackHalf2x16(hz_grass_instances.data[ib + 0u]);
    uint i1 = hz_grass_instances.data[ib + 1u];
    float yaw = float(i1 & 0xFFFFu) / 65535.0 * 6.28318530718;
    float scale_uni = unpackHalf2x16(i1).y;
    vec3 sp = vpos * scale_uni;
    float c = cos(yaw), s = sin(yaw);
    vec3 rotated = vec3(c * sp.x + s * sp.z, sp.y, -s * sp.x + c * sp.z);
    float y = 0.0;
    if (p.hws.x > 0.0) {
        vec2 huv = (world_xz - p.hwo) / p.hws;
        y = texture_sample(grass_heightmap, p.heightmap, huv).r * p.height_scale;
    }
    vec3 world = rotated + vec3(world_xz.x, y, world_xz.y);
    float local_h_norm = (vpos.y - p.pos_min.y) / max(p.pos_extent.y, 1e-3);
    float sway = sin(world.x * 0.07 + hz_scene.time * 1.3) * cos(world.z * 0.09 + hz_scene.time * 1.1);
    world.xz += vec2(0.944, 0.330) * (0.15 * local_h_norm * p.wind_response * sway);
    vec4 world_pos = vec4(world, 1.0);
    gl_Position = hz_scene.view_proj * world_pos;
    v_world_pos = world;
    v_object_pos = vpos;
    v_normal = vec3(0.0, 1.0, 0.0);
    v_tangent = vec3(1.0, 0.0, 0.0);
    v_bitangent_sign = 1.0;
    v_uv = vuv;
    v_uv1 = vuv;
    v_color = vec4(1.0);
    v_material_offset = mo;
    v_view_depth = -(hz_scene.view * world_pos).z;
}
@end

@fs
#define HZ_RENDERER_SHADOWS
#define HZ_RENDERER_IBL
#define HZ_RENDERER_LIGHTING_CLUSTERS
#define HZ_RENDERER_FOG
${FS_FEATURE_DEFINES}in vec3 v_world_pos;
in vec3 v_object_pos;
in vec3 v_normal;
in vec2 v_uv;
in vec2 v_uv1;
in vec4 v_color;
flat in uint v_material_offset;
in vec3 v_tangent;
in float v_bitangent_sign;
in float v_view_depth;
out vec4 frag_color;

${MATERIAL_STRUCT}
${HELPERS}${FUNCTIONS}void main() {
    MaterialFS mat = MaterialFS_unpack(v_material_offset);
${MAIN_BODY}
${LIGHTING}
    lit *= occlusion;
    lit += emission;
    lit = hz_apply_fog(lit, v_view_depth);
    frag_color = vec4(lit, 1.0);
}
@end
";

        // The PBR lighting block for the LIT template. Substituted into
        // ${LIGHTING} when the graph wires its Base Color. When Base Color is
        // unwired the graph is "implicit unlit" (output via Emission), so we
        // substitute `vec3 lit = vec3(0.0);` instead and let the tail
        // (occlusion/emission/fog) carry the result.
        const string LIT_LIGHTING =
@"    vec3 N = apply_normal_map(v_normal, v_tangent, v_bitangent_sign, normal_ts);

    SurfaceInput surface = SurfaceInput(base_color, metallic, smoothness);
    LightingInput li = LightingInput(v_world_pos, N, hz_scene.camera_pos);
    float shadow_factor = calculate_shadow(v_world_pos);
    vec3 lit = pbr_directional_lighting(surface, li, hz_scene.lights,
                                        shadow_factor, hz_shadow.shadow_color);

    HZ_CLUSTER_LOOP_BEGIN(v_view_depth)
        lit += pbr_point_light(surface, li, light);
    HZ_CLUSTER_LOOP_END

    if (hz_scene.skybox_material_offset != 0xFFFFFFFFu) {
        uint sky_off = hz_scene.skybox_material_offset;
        float intensity = uintBitsToFloat(hz_materials.data[sky_off + 4u]);
        uint cubemap_idx = hz_materials.data[sky_off + 5u];
        lit += ibl_ambient(surface, li, hz_scene.lights, cubemap_idx, intensity);
    }
";

        // Sibling shadow caster shader, emitted next to the forward LIT/UNLIT
        // shader when the graph has alpha clip. Same vertex displacement body
        // (substituted via ${VERTEX_BODY}) so the shadow geometry stays in
        // lockstep with the visible mesh under wind / bend. Fragment is just
        // the alpha-clip slice + discard. Modeled on standard_shadow.hz.glsl.
        const string SHADOW_TEMPLATE =
@"@vs
// HZ_RENDERER_SHADOW_PASS is defined at the file top (before the
// renderer.glsl include) so the engine exposes hz_shadow_indices and
// unpack_shadow_pass_instance_data() under that gate. The cooker
// (renderer_shader_preprocess) also reads this define to emit
// MAT_UB_SHADOW_PASS at UBO slot 1 + MAT_SRV_SHADOW_INDICES at the next
// SRV slot. Re-defining inside @vs is redundant but keeps the intent
// readable at the stage boundary.
#define HZ_RENDERER_SHADOW_PASS
out vec2 v_uv;
flat out uint v_material_offset;
${VS_MATERIAL}
${VS_HELPERS}
void main() {
    ShadowPassInstance _s = unpack_shadow_pass_instance_data();
    VertexInstanceData v = _s.v;
    uint cascade = _s.cascade;
${VS_MAT_UNPACK}${VERTEX_BODY}    vec3 world_pos    = (v.model * vec4(${VS_POS}, 1.0)).xyz;
    vec3 world_normal = normalize(mat3(v.model) * v.normal);
    world_pos = apply_shadow_bias(world_pos, world_normal, cascade);
    gl_Position = apply_shadow_clamping(hz_shadow_pass.light_view_proj[cascade] * vec4(world_pos, 1.0));
#ifndef SHADER_BACKEND_WGSL
    gl_ViewportIndex = int(cascade);
#endif
    v_uv = v.uv;
    v_material_offset = v.material_offset;
}
@end

@fs
#define HZ_RENDERER_SHADOW_PASS
in vec2 v_uv;
flat in uint v_material_offset;

${MATERIAL_STRUCT}
${SHADOW_HELPERS}${SHADOW_FUNCTIONS}void main() {
    MaterialFS mat = MaterialFS_unpack(v_material_offset);
${SHADOW_BODY}
}
@end
";
    }
}
