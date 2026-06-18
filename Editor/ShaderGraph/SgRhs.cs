using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HZExporter.ShaderGraph
{
    // Typed right-hand-side expression tree. Lowering builds these per
    // statement; the optimizer pattern-matches on them; codegen formats them
    // back to GLSL via ToGlsl().
    //
    // The eager constant evaluator (TryAsConst) mirrors naga's
    // ConstantEvaluator.binary_op / unary_op / math: if all operands fold to
    // SgConst, the node folds too, recursively.

    // ===== concrete-value constant ============================================

    struct SgConst
    {
        public SgType Type;
        public double[] Values;   // 1, 2, 3, or 4 components — scalars and vec_n only

        public static SgConst Scalar(double v) =>
            new SgConst { Type = SgType.Float, Values = new[] { v } };

        public static SgConst Vec(SgType t, params double[] vs) =>
            new SgConst { Type = t, Values = vs };

        public bool IsFinite()
        {
            foreach (var v in Values)
                if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            return true;
        }

        public string ToGlsl()
        {
            if (Type == SgType.Float) return SgTypes.FormatFloat(Values[0]);
            var parts = new string[Values.Length];
            for (int i = 0; i < Values.Length; i++) parts[i] = SgTypes.FormatFloat(Values[i]);
            return $"{SgTypes.Glsl(Type)}({string.Join(", ", parts)})";
        }
    }

    // ===== rhs hierarchy ======================================================

    abstract class SgRhs
    {
        public abstract string ToGlsl();
        public abstract IEnumerable<string> Refs();   // names of `_nN` referenced
        public virtual bool TryAsConst(Dictionary<string, SgConst> known, out SgConst c)
        {
            c = default; return false;
        }
    }

    class SgRhsLit : SgRhs
    {
        public SgConst Const;
        public override string ToGlsl() => Const.ToGlsl();
        public override IEnumerable<string> Refs() { yield break; }
        public override bool TryAsConst(Dictionary<string, SgConst> known, out SgConst c)
        {
            c = Const; return true;
        }
    }

    class SgRhsRef : SgRhs
    {
        public string Name;
        public SgType Type;
        public override string ToGlsl() => Name;
        public override IEnumerable<string> Refs() { yield return Name; }
        public override bool TryAsConst(Dictionary<string, SgConst> known, out SgConst c)
            => known.TryGetValue(Name, out c);
    }

    class SgRhsBinop : SgRhs
    {
        public string Op;     // "+", "-", "*", "/"
        public SgRhs A, B;
        public override string ToGlsl() => $"({A.ToGlsl()} {Op} {B.ToGlsl()})";
        public override IEnumerable<string> Refs()
        {
            foreach (var r in A.Refs()) yield return r;
            foreach (var r in B.Refs()) yield return r;
        }
        public override bool TryAsConst(Dictionary<string, SgConst> known, out SgConst c)
        {
            c = default;
            if (!A.TryAsConst(known, out var a) || !B.TryAsConst(known, out var b)) return false;
            return SgEval.TryBinop(Op, a, b, out c);
        }
    }

    class SgRhsNeg : SgRhs
    {
        public SgRhs Inner;
        public override string ToGlsl() => $"(-{Inner.ToGlsl()})";
        public override IEnumerable<string> Refs() => Inner.Refs();
        public override bool TryAsConst(Dictionary<string, SgConst> known, out SgConst c)
        {
            c = default;
            if (!Inner.TryAsConst(known, out var v)) return false;
            var r = new double[v.Values.Length];
            for (int i = 0; i < r.Length; i++) r[i] = -v.Values[i];
            c = new SgConst { Type = v.Type, Values = r };
            return true;
        }
    }

    class SgRhsCall : SgRhs
    {
        public string Func;                                // GLSL function name: floor, abs, vec3, clamp, …
        public List<SgRhs> Args = new List<SgRhs>();
        public override string ToGlsl()
        {
            var parts = new string[Args.Count];
            for (int i = 0; i < Args.Count; i++) parts[i] = Args[i].ToGlsl();
            return $"{Func}({string.Join(", ", parts)})";
        }
        public override IEnumerable<string> Refs()
        {
            foreach (var a in Args) foreach (var r in a.Refs()) yield return r;
        }
        public override bool TryAsConst(Dictionary<string, SgConst> known, out SgConst c)
        {
            c = default;
            var args = new SgConst[Args.Count];
            for (int i = 0; i < Args.Count; i++)
            {
                if (!Args[i].TryAsConst(known, out var v)) return false;
                args[i] = v;
            }
            return SgEval.TryCall(Func, args, out c);
        }
    }

    class SgRhsSwizzle : SgRhs
    {
        public SgRhs Base;
        public string Swizzle;                              // "x", "y", "z", "w" or multi-char "xyz", …
        public override string ToGlsl() => $"{Base.ToGlsl()}.{Swizzle}";
        public override IEnumerable<string> Refs() => Base.Refs();
        public override bool TryAsConst(Dictionary<string, SgConst> known, out SgConst c)
        {
            c = default;
            if (!Base.TryAsConst(known, out var b)) return false;
            return SgEval.TrySwizzle(b, Swizzle, out c);
        }
    }

    // Escape hatch — opaque GLSL fragment we don't try to optimize. The refs
    // list is provided explicitly so DCE can still see through it.
    class SgRhsRaw : SgRhs
    {
        public string Glsl;
        public List<string> RefList = new List<string>();
        public override string ToGlsl() => Glsl;
        public override IEnumerable<string> Refs() => RefList;
    }

    // GLSL fragment with positional placeholders `{0}`, `{1}`, ... that get
    // substituted at ToGlsl time using each Args[i]. Unlike SgRhsRaw, this
    // walks operand SgRhs structurally so the optimizer's renumber pass can
    // refresh embedded temp names. Used by auto-generated patterns whose
    // body doesn't fit a clean shape (RemapNode, FresnelNode, etc).
    class SgRhsRawTemplate : SgRhs
    {
        public string Template;
        public List<SgRhs> Args = new List<SgRhs>();

        public override string ToGlsl()
        {
            var sb = new System.Text.StringBuilder(Template.Length + 32);
            for (int i = 0; i < Template.Length; i++)
            {
                char c = Template[i];
                if (c == '{' && i + 2 < Template.Length && Template[i + 2] == '}' && char.IsDigit(Template[i + 1]))
                {
                    int idx = Template[i + 1] - '0';
                    if (idx < Args.Count) sb.Append(Args[idx].ToGlsl());
                    i += 2;
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public override IEnumerable<string> Refs()
        {
            // Yield each operand's refs once per occurrence of `{i}` in the
            // template — so the optimizer's use-count is accurate. (If the
            // template uses {0} twice, that operand is referenced twice;
            // inline-once-used must not collapse it.)
            var counts = new int[Args.Count];
            for (int i = 0; i + 2 < Template.Length; i++)
            {
                if (Template[i] == '{' && Template[i + 2] == '}' && char.IsDigit(Template[i + 1]))
                {
                    int idx = Template[i + 1] - '0';
                    if (idx < counts.Length) counts[idx]++;
                }
            }
            for (int i = 0; i < Args.Count; i++)
                for (int n = 0; n < counts[i]; n++)
                    foreach (var r in Args[i].Refs()) yield return r;
        }
    }

    // A reference to a texture-typed material property. Behaves opaquely for
    // the optimizer (no folding) and is recognised structurally by node
    // handlers like SampleTexture2D (which need the FieldName to expand into
    // `texture_sample(<name>, mat.<name>, uv)`).
    class SgRhsTextureRef : SgRhs
    {
        public string FieldName;
        public bool IsNormalMap;       // hint for SampleTexture2D to emit the [0,1]→[-1,1] decode
        public override string ToGlsl() => "mat." + FieldName;
        public override IEnumerable<string> Refs() { yield break; }
    }

    // ===== a single SSA assignment ============================================

    class SgStatement
    {
        public string Name;
        public SgType Type;
        // Optional GLSL type name override — used for struct-typed temps (e.g.
        // `Subgraph_X_Outputs _sg0 = subgraph_x();`) where Type doesn't have
        // a built-in mapping. When non-null, this string is rendered verbatim
        // in place of `SgTypes.Glsl(Type)`.
        public string TypeName;
        public SgRhs Rhs;

        // Originating node's m_Group.m_Id — codegen uses this to inject
        // `// === <title> ===` section headers when consecutive statements
        // come from different graph groups. Null = ungrouped.
        public string GroupId;

        // Optional `#ifdef`-driven conditional form — when non-null, this
        // statement renders as a forward-declared temp followed by guarded
        // assignment branches (instead of `<type> name = rhs;`). Used by
        // KeywordNode to emit shader-keyword-driven compile-time branches.
        public List<SgConditionalBranch> Branches;

        public string Format()
        {
            if (Branches == null)
                return $"{TypeName ?? SgTypes.Glsl(Type)} {Name} = {Rhs.ToGlsl()};";
            return FormatConditional();
        }

        string FormatConditional()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(TypeName ?? SgTypes.Glsl(Type)).Append(' ').Append(Name).Append(";\n");
            for (int i = 0; i < Branches.Count; i++)
            {
                var b = Branches[i];
                if (b.Define != null)
                    sb.Append(i == 0 ? "    #if defined(" : "    #elif defined(").Append(b.Define).Append(")\n");
                else
                    sb.Append("    #else\n");
                sb.Append("    {\n");
                foreach (var s in b.Body)
                    foreach (var line in s.Format().Split('\n'))
                        sb.Append("        ").Append(line).Append('\n');
                sb.Append("        ").Append(Name).Append(" = ").Append(b.Rhs.ToGlsl()).Append(";\n");
                sb.Append("    }\n");
            }
            sb.Append("    #endif");
            return sb.ToString();
        }

        // All refs flowing into this statement, walking branches when set.
        // Used by the optimizer's CountRefs / DCE seeds.
        public IEnumerable<string> Refs()
        {
            if (Branches == null)
            {
                foreach (var r in Rhs.Refs()) yield return r;
                yield break;
            }
            foreach (var b in Branches)
            {
                foreach (var s in b.Body)
                    foreach (var r in s.Refs()) yield return r;
                foreach (var r in b.Rhs.Refs()) yield return r;
            }
        }
    }

    class SgConditionalBranch
    {
        public string Define;            // null = #else / default branch
        public SgType RhsType;           // resolved type of Rhs — drives the outer temp's declared type
        public List<SgStatement> Body = new List<SgStatement>();
        public SgRhs Rhs;
    }

    // A free-standing function emitted ahead of main(). Used for inlining
    // SubGraphs as named functions on the same file.
    //
    // Two output forms:
    //   • Single output  → simple `<type> name() { body; return rhs; }`.
    //   • Multiple outputs → struct return:
    //         struct Name_Outputs { ... };
    //         Name_Outputs name() { body; return Name_Outputs(rhs1, rhs2); }
    //
    // The optimizer treats every SgFunctionOutput.Rhs as a live root.
    class SgFunctionDef
    {
        public string Name;
        public string StructName;                                  // null ⇒ single-return form
        public List<SgFunctionParam> Params = new List<SgFunctionParam>();
        public List<SgFunctionOutput> Outputs = new List<SgFunctionOutput>();
        public List<SgStatement> Body = new List<SgStatement>();
    }

    class SgFunctionParam
    {
        public string Name;       // identifier as it appears in the function signature + body
        public SgType Type;
    }

    class SgFunctionOutput
    {
        public string Name;       // GLSL field name (e.g. "Out_Vector1")
        public SgType Type;
        public SgRhs Rhs;
    }

    // ===== ergonomic builders =================================================

    static class Sg
    {
        public static SgRhs Lit(double v) => new SgRhsLit { Const = SgConst.Scalar(v) };
        public static SgRhs LitConst(SgConst c) => new SgRhsLit { Const = c };
        public static SgRhs Ref(string name, SgType t) => new SgRhsRef { Name = name, Type = t };
        public static SgRhs Bin(string op, SgRhs a, SgRhs b) => new SgRhsBinop { Op = op, A = a, B = b };
        public static SgRhs Neg(SgRhs x) => new SgRhsNeg { Inner = x };
        public static SgRhs Call(string fn, params SgRhs[] args)
            => new SgRhsCall { Func = fn, Args = new List<SgRhs>(args) };
        public static SgRhs Swizzle(SgRhs b, string s) => new SgRhsSwizzle { Base = b, Swizzle = s };
        public static SgRhs Raw(string glsl, params string[] refs)
            => new SgRhsRaw { Glsl = glsl, RefList = new List<string>(refs) };

        // Builder for complex-expression bodies that reference other SgRhs
        // operands. The template uses positional placeholders `{0}`, `{1}`,
        // … which are substituted at ToGlsl time — so the optimizer's
        // renumbering pass can refresh embedded temp names through the
        // operands structurally.
        public static SgRhs RawTemplate(string template, params SgRhs[] inputs)
            => new SgRhsRawTemplate { Template = template, Args = new List<SgRhs>(inputs) };

        // HLSL-style truncating binary op. When operands are different-rank
        // vectors (e.g. vec4 × vec3), GLSL rejects the expression — so we
        // swizzle the larger one down to the smaller's rank before the op.
        // Float-with-vec broadcasts in GLSL natively, so we leave those.
        // Mat operands fall through to plain Bin (callers handle).
        public static SgRhs BinPromote(string op, SgValue a, SgValue b)
        {
            int ra = SgTypes.Rank(a.Type), rb = SgTypes.Rank(b.Type);
            if (ra > 0 && rb > 0 && ra != rb)
            {
                int target = System.Math.Min(ra, rb);
                if (target > 1)
                {
                    string swizzle = "xyzw".Substring(0, target);
                    var aRhs = ra > target ? Swizzle(a.Rhs, swizzle) : a.Rhs;
                    var bRhs = rb > target ? Swizzle(b.Rhs, swizzle) : b.Rhs;
                    return Bin(op, aRhs, bRhs);
                }
            }
            return Bin(op, a.Rhs, b.Rhs);
        }

        // HLSL-style truncating function call. When operands have mixed
        // vector ranks (e.g. mix(vec4, vec4, vec3) for `lerp(float4, float4,
        // float3)`), GLSL rejects the call — so we swizzle every operand
        // down to the minimum vector rank present. Float-rank operands stay
        // as-is (GLSL broadcasts scalars naturally). Used by multi-arg
        // intrinsics like mix / smoothstep / clamp where all vector args
        // must share rank.
        public static SgRhs CallPromote(string fn, params SgValue[] vs)
        {
            int minRank = int.MaxValue;
            foreach (var v in vs)
            {
                int r = SgTypes.Rank(v.Type);
                if (r > 1 && r < minRank) minRank = r;
            }
            var args = new SgRhs[vs.Length];
            if (minRank == int.MaxValue)
            {
                for (int i = 0; i < vs.Length; i++) args[i] = vs[i].Rhs;
            }
            else
            {
                string sw = "xyzw".Substring(0, minRank);
                for (int i = 0; i < vs.Length; i++)
                {
                    int r = SgTypes.Rank(vs[i].Type);
                    args[i] = r > minRank ? Swizzle(vs[i].Rhs, sw) : vs[i].Rhs;
                }
            }
            return new SgRhsCall { Func = fn, Args = new List<SgRhs>(args) };
        }
    }

    // ===== constant evaluator =================================================

    // Eager folding: if all operands of an expression are SgConst, compute the
    // result. Pattern-match style is borrowed from naga's binary_op and math.
    static class SgEval
    {
        public static bool TryBinop(string op, SgConst a, SgConst b, out SgConst c)
        {
            c = default;
            int na = a.Values.Length, nb = b.Values.Length;
            if (na != nb && na != 1 && nb != 1) return false;
            int rank = Math.Max(na, nb);

            var av = Broadcast(a.Values, rank);
            var bv = Broadcast(b.Values, rank);
            var r = new double[rank];
            for (int i = 0; i < rank; i++)
            {
                double x;
                switch (op)
                {
                    case "+": x = av[i] + bv[i]; break;
                    case "-": x = av[i] - bv[i]; break;
                    case "*": x = av[i] * bv[i]; break;
                    case "/":
                        if (bv[i] == 0.0) return false;   // refuse to fold div-by-zero
                        x = av[i] / bv[i]; break;
                    default: return false;
                }
                if (double.IsNaN(x) || double.IsInfinity(x)) return false;
                r[i] = x;
            }
            c = new SgConst { Type = TypeForRank(rank), Values = r };
            return true;
        }

        public static bool TryCall(string fn, SgConst[] args, out SgConst c)
        {
            c = default;

            // vec_n constructors.
            if (fn == "vec2" || fn == "vec3" || fn == "vec4")
            {
                int n = fn == "vec2" ? 2 : fn == "vec3" ? 3 : 4;
                var t = TypeForRank(n);

                // vec_n(scalar) — broadcast.
                if (args.Length == 1 && args[0].Values.Length == 1)
                {
                    var v = new double[n];
                    for (int i = 0; i < n; i++) v[i] = args[0].Values[0];
                    c = new SgConst { Type = t, Values = v };
                    return true;
                }
                // vec_n(a, b, …) — all-scalar component-wise.
                if (args.Length == n && args.All(a => a.Values.Length == 1))
                {
                    var v = new double[n];
                    for (int i = 0; i < n; i++) v[i] = args[i].Values[0];
                    c = new SgConst { Type = t, Values = v };
                    return true;
                }
                return false;
            }

            // scalar unary builtins.
            if (args.Length == 1 && args[0].Values.Length == 1)
            {
                double x = args[0].Values[0];
                double y;
                switch (fn)
                {
                    case "abs":   y = Math.Abs(x); break;
                    case "floor": y = Math.Floor(x); break;
                    case "ceil":  y = Math.Ceiling(x); break;
                    case "round": y = Math.Round(x); break;
                    case "fract": y = x - Math.Floor(x); break;
                    case "sign":  y = Math.Sign(x); break;
                    case "sqrt":  if (x < 0) return false; y = Math.Sqrt(x); break;
                    default: return false;
                }
                if (double.IsNaN(y) || double.IsInfinity(y)) return false;
                c = SgConst.Scalar(y);
                return true;
            }

            // 3-arg clamp(x, lo, hi) — scalar only for now.
            if (fn == "clamp" && args.Length == 3
                && args.All(a => a.Values.Length == 1))
            {
                double x  = args[0].Values[0];
                double lo = args[1].Values[0];
                double hi = args[2].Values[0];
                c = SgConst.Scalar(Math.Max(lo, Math.Min(x, hi)));
                return true;
            }

            return false;
        }

        public static bool TrySwizzle(SgConst b, string sw, out SgConst c)
        {
            c = default;
            if (sw.Length == 1)
            {
                int idx = SwizzleIndex(sw[0]);
                if (idx < 0 || idx >= b.Values.Length) return false;
                c = SgConst.Scalar(b.Values[idx]);
                return true;
            }
            // Multi-component swizzle (e.g. ".xyz") — produce a vec_n.
            var v = new double[sw.Length];
            for (int i = 0; i < sw.Length; i++)
            {
                int idx = SwizzleIndex(sw[i]);
                if (idx < 0 || idx >= b.Values.Length) return false;
                v[i] = b.Values[idx];
            }
            c = new SgConst { Type = TypeForRank(sw.Length), Values = v };
            return true;
        }

        // ----- helpers --------------------------------------------------------

        static int SwizzleIndex(char c)
        {
            switch (c)
            {
                case 'x': case 'r': return 0;
                case 'y': case 'g': return 1;
                case 'z': case 'b': return 2;
                case 'w': case 'a': return 3;
            }
            return -1;
        }

        static double[] Broadcast(double[] v, int rank)
        {
            if (v.Length == rank) return v;
            if (v.Length == 1)
            {
                var r = new double[rank];
                for (int i = 0; i < rank; i++) r[i] = v[0];
                return r;
            }
            return v;
        }

        static SgType TypeForRank(int r)
        {
            switch (r)
            {
                case 1: return SgType.Float;
                case 2: return SgType.Vec2;
                case 3: return SgType.Vec3;
                case 4: return SgType.Vec4;
            }
            return SgType.Unknown;
        }
    }
}
