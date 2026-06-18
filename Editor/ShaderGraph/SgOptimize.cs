using System.Collections.Generic;

namespace HZExporter.ShaderGraph
{
    // Optimization passes on the lowered IR (`_nN = expr;` SSA statements +
    // a list of surface assigns) before codegen.
    //
    //   1. Constant fold     — eager evaluation when all RHS operands are
    //                          known constants. Mirrors naga's
    //                          ConstantEvaluator.binary_op / math.
    //   2. Algebraic peephole — small rule table (x*0→0, x*1→x, x±0→x,
    //                          0/x→0, …). Closure-per-rule style cribbed
    //                          from SPIRV-Tools `folding_rules.cpp`.
    //   3. DCE                — drop `_nN` whose name is not referenced by
    //                          any surviving statement or assign.
    //   4. Inline-once-used   — substitute single-use temps into their
    //                          consumer to compact the output.
    //   5. Renumber           — repack temps as `_n0`, `_n1`, … in order.
    static class SgOptimize
    {
        // Interface erasure for the codegen's Assign type so this file doesn't
        // depend on its concrete shape.
        public interface IAssignView
        {
            SgRhs Rhs { get; set; }
        }

        public static void Run(List<SgStatement> statements, List<IAssignView> assigns)
        {
            for (int pass = 0; pass < 8; pass++)
            {
                bool fold = ConstantFold(statements, assigns);
                bool peep = PeepholeRewrite(statements, assigns);
                if (!fold && !peep) break;
            }
            CommonSubexpr(statements, assigns);   // merge identical computations (CSE)
            DCE(statements, assigns);
            InlineOnceUsed(statements, assigns);  // collapse trivial temps (keeps calls)
            CommonSubexpr(statements, assigns);   // catch duplicates exposed by inlining
            DCE(statements, assigns);
            Renumber(statements, assigns);
        }

        // Backwards-compat overload accepting an arbitrary list — the codegen
        // wraps its `Assign` with a small adapter (see SgCodegen.cs).
        public static void Run<TAssign>(List<SgStatement> statements, List<TAssign> assigns)
            where TAssign : class, IAssignView
        {
            var erased = new List<IAssignView>(assigns.Count);
            foreach (var a in assigns) erased.Add(a);
            Run(statements, erased);
            // mutations to .Rhs already propagate via the interface — nothing
            // to copy back.
        }

        // ===== 1. constant folding =============================================

        // Walk top-down. Track which `_nN` are known constants. For each
        // statement we (a) propagate known constants into its RHS — replacing
        // `Ref("_n3")` with the literal if `_n3` is in the map — and then
        // (b) try to evaluate the result. The propagation step is what makes
        // peephole rewrites like `x*0→0` see the actual zero literal even
        // when it's been routed through a temp.
        static bool ConstantFold(List<SgStatement> statements, List<IAssignView> assigns)
        {
            bool changed = false;
            var known = new Dictionary<string, SgConst>();
            foreach (var s in statements)
            {
                // Conditional statements (keyword-driven branches) are opaque
                // to constant folding from the outside — each branch was
                // already optimized in its own ctx by KeywordNodeHandler.
                if (s.Branches != null) continue;

                var (prop, pc) = PropagateConsts(s.Rhs, known);
                if (pc) { s.Rhs = prop; changed = true; }

                if (s.Rhs is SgRhsLit lit)
                {
                    known[s.Name] = lit.Const;
                    continue;
                }
                if (s.Rhs.TryAsConst(known, out var c))
                {
                    s.Rhs = Sg.LitConst(c);
                    known[s.Name] = c;
                    changed = true;
                }
            }
            // Surface assigns are also consumers — propagate into them too.
            foreach (var a in assigns)
            {
                var (prop, pc) = PropagateConsts(a.Rhs, known);
                if (pc) { a.Rhs = prop; changed = true; }
            }
            return changed;
        }

        static (SgRhs rhs, bool changed) PropagateConsts(SgRhs r, Dictionary<string, SgConst> known)
        {
            switch (r)
            {
                case SgRhsRef rf:
                    if (known.TryGetValue(rf.Name, out var c)) return (Sg.LitConst(c), true);
                    return (r, false);
                case SgRhsBinop b:
                {
                    var (a1, ca) = PropagateConsts(b.A, known);
                    var (b1, cb) = PropagateConsts(b.B, known);
                    if (ca || cb) return (new SgRhsBinop { Op = b.Op, A = a1, B = b1 }, true);
                    return (r, false);
                }
                case SgRhsNeg n:
                {
                    var (i1, ci) = PropagateConsts(n.Inner, known);
                    return ci ? ((SgRhs)new SgRhsNeg { Inner = i1 }, true) : (r, false);
                }
                case SgRhsCall cc:
                {
                    var newArgs = new List<SgRhs>(cc.Args.Count);
                    bool ach = false;
                    foreach (var arg in cc.Args)
                    {
                        var (na, nc) = PropagateConsts(arg, known);
                        if (nc) ach = true;
                        newArgs.Add(na);
                    }
                    return ach ? ((SgRhs)new SgRhsCall { Func = cc.Func, Args = newArgs }, true) : (r, false);
                }
                case SgRhsSwizzle sw:
                {
                    var (sb, sc) = PropagateConsts(sw.Base, known);
                    return sc ? ((SgRhs)new SgRhsSwizzle { Base = sb, Swizzle = sw.Swizzle }, true) : (r, false);
                }
                case SgRhsRawTemplate rt:
                {
                    var newArgs = new List<SgRhs>(rt.Args.Count);
                    bool ach = false;
                    foreach (var arg in rt.Args)
                    {
                        var (na, nc) = PropagateConsts(arg, known);
                        if (nc) ach = true;
                        newArgs.Add(na);
                    }
                    return ach ? ((SgRhs)new SgRhsRawTemplate { Template = rt.Template, Args = newArgs }, true) : (r, false);
                }
            }
            return (r, false);
        }

        // ===== 2. peephole rewrites ============================================

        delegate SgRhs RewriteRule(SgRhs rhs);

        static readonly RewriteRule[] _rules = new RewriteRule[]
        {
            RuleMulZero, RuleMulOne, RuleAddZero, RuleSubZero, RuleSubFromZero,
            RuleDivOne, RuleZeroDiv, RuleNegNeg, RuleAbsNeg,
        };

        static SgRhs RuleMulZero(SgRhs r)
        {
            if (r is SgRhsBinop b && b.Op == "*" && (IsZero(b.A) || IsZero(b.B))) return Sg.Lit(0.0);
            return r;
        }
        static SgRhs RuleMulOne(SgRhs r)
        {
            if (r is SgRhsBinop b && b.Op == "*")
            {
                if (IsOne(b.A)) return b.B;
                if (IsOne(b.B)) return b.A;
            }
            return r;
        }
        static SgRhs RuleAddZero(SgRhs r)
        {
            if (r is SgRhsBinop b && b.Op == "+")
            {
                if (IsZero(b.A)) return b.B;
                if (IsZero(b.B)) return b.A;
            }
            return r;
        }
        static SgRhs RuleSubZero(SgRhs r)
        {
            // x - 0 → x
            if (r is SgRhsBinop b && b.Op == "-" && IsZero(b.B)) return b.A;
            return r;
        }
        static SgRhs RuleSubFromZero(SgRhs r)
        {
            // 0 - x → -x
            if (r is SgRhsBinop b && b.Op == "-" && IsZero(b.A)) return Sg.Neg(b.B);
            return r;
        }
        static SgRhs RuleDivOne(SgRhs r)
        {
            if (r is SgRhsBinop b && b.Op == "/" && IsOne(b.B)) return b.A;
            return r;
        }
        // 0 / x → 0 only when x is a known non-zero literal — we can't always prove non-zero.
        static SgRhs RuleZeroDiv(SgRhs r)
        {
            if (r is SgRhsBinop b && b.Op == "/" && IsZero(b.A) && IsNonZeroConst(b.B)) return Sg.Lit(0.0);
            return r;
        }
        // -(-x) → x
        static SgRhs RuleNegNeg(SgRhs r)
        {
            if (r is SgRhsNeg n && n.Inner is SgRhsNeg inner) return inner.Inner;
            return r;
        }
        // abs(-x) → abs(x)
        static SgRhs RuleAbsNeg(SgRhs r)
        {
            if (r is SgRhsCall c && c.Func == "abs" && c.Args.Count == 1 && c.Args[0] is SgRhsNeg n)
                return Sg.Call("abs", n.Inner);
            return r;
        }

        // Apply each rule to every node in the RHS tree until no rule fires.
        static bool PeepholeRewrite(List<SgStatement> statements, List<IAssignView> assigns)
        {
            bool any = false;
            foreach (var s in statements)
            {
                if (s.Branches != null) continue;   // conditionals are opaque
                var (newRhs, ch) = ApplyRulesRec(s.Rhs);
                if (ch) { s.Rhs = newRhs; any = true; }
            }
            foreach (var a in assigns)
            {
                var (newRhs, ch) = ApplyRulesRec(a.Rhs);
                if (ch) { a.Rhs = newRhs; any = true; }
            }
            return any;
        }

        static (SgRhs rhs, bool changed) ApplyRulesRec(SgRhs r)
        {
            // Rewrite children first (post-order) so the parent sees folded sub-trees.
            bool changed = false;
            switch (r)
            {
                case SgRhsBinop b:
                    var (a1, ca) = ApplyRulesRec(b.A);
                    var (b1, cb) = ApplyRulesRec(b.B);
                    if (ca || cb) { r = new SgRhsBinop { Op = b.Op, A = a1, B = b1 }; changed = true; }
                    break;
                case SgRhsNeg n:
                    var (i1, ci) = ApplyRulesRec(n.Inner);
                    if (ci) { r = new SgRhsNeg { Inner = i1 }; changed = true; }
                    break;
                case SgRhsCall c:
                    var newArgs = new List<SgRhs>(c.Args.Count);
                    bool argsChanged = false;
                    foreach (var arg in c.Args)
                    {
                        var (na, nc) = ApplyRulesRec(arg);
                        if (nc) argsChanged = true;
                        newArgs.Add(na);
                    }
                    if (argsChanged) { r = new SgRhsCall { Func = c.Func, Args = newArgs }; changed = true; }
                    break;
                case SgRhsSwizzle sw:
                    var (sb, sc) = ApplyRulesRec(sw.Base);
                    if (sc) { r = new SgRhsSwizzle { Base = sb, Swizzle = sw.Swizzle }; changed = true; }
                    break;
                case SgRhsRawTemplate rt:
                    var rtArgs = new List<SgRhs>(rt.Args.Count);
                    bool rtChanged = false;
                    foreach (var arg in rt.Args)
                    {
                        var (rta, rtc) = ApplyRulesRec(arg);
                        if (rtc) rtChanged = true;
                        rtArgs.Add(rta);
                    }
                    if (rtChanged) { r = new SgRhsRawTemplate { Template = rt.Template, Args = rtArgs }; changed = true; }
                    break;
            }

            // Then apply rules at this level.
            foreach (var rule in _rules)
            {
                var rewritten = rule(r);
                if (!ReferenceEquals(rewritten, r)) { r = rewritten; changed = true; }
            }
            return (r, changed);
        }

        // ===== 3. dead code elimination ========================================

        static void DCE(List<SgStatement> statements, List<IAssignView> assigns)
        {
            // Mark every name reachable from the assigns (the live roots) plus
            // any statement transitively referenced by a live one.
            var live = new HashSet<string>();
            // Seed: refs from the surface assigns.
            foreach (var a in assigns)
                foreach (var r in a.Rhs.Refs()) live.Add(r);

            // Iterate from the end backwards: if a statement's name is live,
            // its RHS refs become live too. Single backward pass suffices for
            // SSA in topological order. Conditionals walk all branches.
            for (int i = statements.Count - 1; i >= 0; i--)
            {
                if (!live.Contains(statements[i].Name)) continue;
                foreach (var r in statements[i].Refs()) live.Add(r);
            }

            statements.RemoveAll(s => !live.Contains(s.Name));
        }

        // ===== 4. inline-once-used ============================================

        // If a temp is referenced exactly once (across all surviving statements
        // + assigns) and isn't a side-effecting opaque (Raw with non-empty
        // refs is treated as side-effecting to be conservative), substitute
        // its RHS into the consumer and drop the producer. Iterates so chains
        // collapse.
        static void InlineOnceUsed(List<SgStatement> statements, List<IAssignView> assigns)
        {
            for (;;)
            {
                var useCount = new Dictionary<string, int>();
                CountRefs(statements, assigns, useCount);

                int dropped = 0;
                for (int i = 0; i < statements.Count; i++)
                {
                    var s = statements[i];
                    if (s.Branches != null) continue;   // can't inline a conditional
                    if (!useCount.TryGetValue(s.Name, out var n) || n != 1) continue;
                    if (!IsInlineable(s.Rhs)) continue;

                    // Find the unique consumer and substitute.
                    bool replaced = false;
                    for (int j = i + 1; j < statements.Count && !replaced; j++)
                    {
                        var (newRhs, did) = SubstituteOnce(statements[j].Rhs, s.Name, s.Rhs);
                        if (did) { statements[j].Rhs = newRhs; replaced = true; }
                    }
                    if (!replaced)
                    {
                        for (int k = 0; k < assigns.Count && !replaced; k++)
                        {
                            var (newRhs, did) = SubstituteOnce(assigns[k].Rhs, s.Name, s.Rhs);
                            if (did) { assigns[k].Rhs = newRhs; replaced = true; }
                        }
                    }
                    if (replaced)
                    {
                        statements.RemoveAt(i);
                        i--;
                        dropped++;
                    }
                }
                if (dropped == 0) break;
            }
        }

        static bool IsInlineable(SgRhs r)
        {
            // Don't inline Raw with refs — those are opaque to us, so we don't
            // know whether they're side-effecting. (UV varying reads are Raw
            // with no refs, and those *are* safe to inline; allow them.)
            if (r is SgRhsRaw raw && raw.RefList.Count > 0) return false;
            // Keep genuine function calls as named variables rather than burying
            // them inside larger expressions: texture_sample / subgraph_* / hz_*
            // helpers (RawTemplate) and any non-constructor call. This (with the
            // CSE pass) materializes each distinct call once and reuses it instead
            // of recomputing a costly call multiple times. Trivial temps —
            // arithmetic, swizzles, vec/mat constructors, literals, refs — still
            // inline so the output isn't drowned in noise.
            if (r is SgRhsRawTemplate) return false;
            if (r is SgRhsCall c && !IsConstructorFunc(c.Func)) return false;
            return true;
        }

        static bool IsConstructorFunc(string f) =>
            f == "vec2" || f == "vec3" || f == "vec4" || f == "mat3" || f == "mat4";

        // ===== CSE: merge identical computations ==============================

        // ShaderGraph compiles to straight-line dataflow (no scope/branches
        // beyond keyword #ifdefs), so a value computed identically more than
        // once can be shared. Merge non-conditional statements whose
        // (type, canonical-GLSL) form matches: drop the later duplicate and
        // redirect every reference to the first. Combined with IsInlineable
        // refusing to inline function-call temps, this materializes each
        // distinct call once and reuses it (e.g. two `hz_baked_gi(pos,nrm)`
        // nodes collapse to one variable).
        static bool CommonSubexpr(List<SgStatement> statements, List<IAssignView> assigns)
        {
            var seen = new Dictionary<string, string>();    // "type|glsl" -> kept temp name
            var rename = new Dictionary<string, string>();  // dropped name -> kept name
            var keep = new List<SgStatement>();

            foreach (var s in statements)
            {
                // Conditional (keyword) statements are opaque: never merge them
                // or across them, but do rewrite their inner refs to temps
                // merged earlier in this pass.
                if (s.Branches != null)
                {
                    if (rename.Count > 0) RenameStatement(s, rename);
                    keep.Add(s);
                    continue;
                }
                // A statement only references temps defined before it (SSA), so
                // applying the renames accumulated so far is sufficient.
                if (rename.Count > 0) s.Rhs = RenameRefs(s.Rhs, rename);

                string typeKey;
                if (s.TypeName != null) typeKey = s.TypeName;
                else if (s.Type == SgType.Unknown || s.Type == SgType.SamplerState) { keep.Add(s); continue; }
                else typeKey = SgTypes.Glsl(s.Type);

                string key = typeKey + "|" + s.Rhs.ToGlsl();
                if (seen.TryGetValue(key, out var first))
                    rename[s.Name] = first;   // duplicate: drop it, redirect uses to `first`
                else
                {
                    seen[key] = s.Name;
                    keep.Add(s);
                }
            }

            if (rename.Count == 0) return false;

            // Surface assigns reference any temp → apply the full map at the end.
            foreach (var a in assigns) a.Rhs = RenameRefs(a.Rhs, rename);

            statements.Clear();
            statements.AddRange(keep);
            return true;
        }

        // Returns a new RHS with the first occurrence of Ref(name) replaced by
        // `replacement`. Walks the tree; "first occurrence" means first in
        // depth-first order, which suffices since we only call this when the
        // use count is 1.
        static (SgRhs rhs, bool replaced) SubstituteOnce(SgRhs r, string name, SgRhs replacement)
        {
            switch (r)
            {
                case SgRhsRef rf when rf.Name == name:
                    return (replacement, true);
                case SgRhsBinop b:
                {
                    var (a1, ca) = SubstituteOnce(b.A, name, replacement);
                    if (ca) return (new SgRhsBinop { Op = b.Op, A = a1, B = b.B }, true);
                    var (b1, cb) = SubstituteOnce(b.B, name, replacement);
                    if (cb) return (new SgRhsBinop { Op = b.Op, A = b.A, B = b1 }, true);
                    return (r, false);
                }
                case SgRhsNeg n:
                {
                    var (i1, ci) = SubstituteOnce(n.Inner, name, replacement);
                    return ci ? ((SgRhs)new SgRhsNeg { Inner = i1 }, true) : (r, false);
                }
                case SgRhsCall c:
                    for (int i = 0; i < c.Args.Count; i++)
                    {
                        var (na, did) = SubstituteOnce(c.Args[i], name, replacement);
                        if (did)
                        {
                            var newArgs = new List<SgRhs>(c.Args);
                            newArgs[i] = na;
                            return (new SgRhsCall { Func = c.Func, Args = newArgs }, true);
                        }
                    }
                    return (r, false);
                case SgRhsSwizzle sw:
                {
                    var (sb, sc) = SubstituteOnce(sw.Base, name, replacement);
                    return sc ? ((SgRhs)new SgRhsSwizzle { Base = sb, Swizzle = sw.Swizzle }, true) : (r, false);
                }
                case SgRhsRawTemplate rt:
                    for (int i = 0; i < rt.Args.Count; i++)
                    {
                        var (na, did) = SubstituteOnce(rt.Args[i], name, replacement);
                        if (did)
                        {
                            var newArgs = new List<SgRhs>(rt.Args);
                            newArgs[i] = na;
                            return (new SgRhsRawTemplate { Template = rt.Template, Args = newArgs }, true);
                        }
                    }
                    return (r, false);
            }
            return (r, false);
        }

        static void CountRefs(List<SgStatement> statements, List<IAssignView> assigns,
                              Dictionary<string, int> outCount)
        {
            foreach (var s in statements)
                foreach (var r in s.Refs()) Inc(outCount, r);
            foreach (var a in assigns)
                foreach (var r in a.Rhs.Refs()) Inc(outCount, r);
        }

        static void Inc(Dictionary<string, int> d, string k) =>
            d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1;

        // ===== 5. dense renumber ==============================================

        // After DCE + inlining, temp names like `_n3, _n7, _n22` appear; pack
        // them back to `_n0, _n1, _n2, …` for readability.
        static void Renumber(List<SgStatement> statements, List<IAssignView> assigns)
        {
            var rename = new Dictionary<string, string>();
            for (int i = 0; i < statements.Count; i++)
            {
                string newName = "_n" + i;
                rename[statements[i].Name] = newName;
            }
            for (int i = 0; i < statements.Count; i++) RenameStatement(statements[i], rename);
            foreach (var a in assigns) a.Rhs = RenameRefs(a.Rhs, rename);
        }

        // Walk a statement (assign or conditional) and rename refs to outer
        // names. Recurses through nested conditional branches so deeply-nested
        // `#if` blocks see fresh outer names. Branch-local temp names (with
        // their `_b<idx>_n` prefix) aren't in `rename` so they're untouched.
        static void RenameStatement(SgStatement s, Dictionary<string, string> rename)
        {
            if (s.Branches != null)
            {
                foreach (var b in s.Branches)
                {
                    b.Rhs = RenameRefs(b.Rhs, rename);
                    foreach (var bs in b.Body) RenameStatement(bs, rename);
                }
            }
            else if (s.Rhs != null)
            {
                s.Rhs = RenameRefs(s.Rhs, rename);
            }
            // Outer-named temps get renumbered; branch-locals use a different
            // prefix and aren't keys in the rename map, so this is a no-op
            // for them.
            if (rename.TryGetValue(s.Name, out var nn)) s.Name = nn;
        }

        static SgRhs RenameRefs(SgRhs r, Dictionary<string, string> rename)
        {
            switch (r)
            {
                case SgRhsRef rf:
                    return rename.TryGetValue(rf.Name, out var nn)
                        ? new SgRhsRef { Name = nn, Type = rf.Type }
                        : r;
                case SgRhsBinop b:
                    return new SgRhsBinop { Op = b.Op, A = RenameRefs(b.A, rename), B = RenameRefs(b.B, rename) };
                case SgRhsNeg n:
                    return new SgRhsNeg { Inner = RenameRefs(n.Inner, rename) };
                case SgRhsCall c:
                {
                    var args = new List<SgRhs>(c.Args.Count);
                    foreach (var arg in c.Args) args.Add(RenameRefs(arg, rename));
                    return new SgRhsCall { Func = c.Func, Args = args };
                }
                case SgRhsSwizzle sw:
                    return new SgRhsSwizzle { Base = RenameRefs(sw.Base, rename), Swizzle = sw.Swizzle };
                case SgRhsRawTemplate rt:
                {
                    var args = new List<SgRhs>(rt.Args.Count);
                    foreach (var arg in rt.Args) args.Add(RenameRefs(arg, rename));
                    return new SgRhsRawTemplate { Template = rt.Template, Args = args };
                }
            }
            return r;
        }

        // ===== predicates =====================================================

        static bool IsZero(SgRhs r) =>
            r is SgRhsLit l && l.Const.Values.Length >= 1 && AllEqual(l.Const.Values, 0.0);

        static bool IsOne(SgRhs r) =>
            r is SgRhsLit l && l.Const.Values.Length >= 1 && AllEqual(l.Const.Values, 1.0);

        static bool IsNonZeroConst(SgRhs r)
        {
            if (!(r is SgRhsLit l)) return false;
            foreach (var v in l.Const.Values) if (v == 0.0) return false;
            return true;
        }

        static bool AllEqual(double[] values, double v)
        {
            foreach (var x in values) if (x != v) return false;
            return true;
        }
    }
}
