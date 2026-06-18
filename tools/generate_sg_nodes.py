#!/usr/bin/env python3
"""Bulk-generate SgGeneratedNodes.cs by parsing Unity ShaderGraph
CodeFunctionNode classes from the package cache.

Outputs:
  Assets/Editor/HZExporter/ShaderGraph/SgGeneratedNodes.cs  — generated registrations
  tools/sg_nodes_skipped.txt                                — nodes that need manual handlers

Re-run this whenever Unity ships new ShaderGraph nodes.
"""

from __future__ import annotations
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SG_PKG_GLOB = ROOT / "Library/PackageCache"
OUT_FILE   = ROOT / "Assets/Editor/HZExporter/ShaderGraph/SgGeneratedNodes.cs"
SKIP_FILE  = ROOT / "tools/sg_nodes_skipped.txt"

# --- HLSL → GLSL function-name map -----------------------------------------
# value is the GLSL name; anything not listed passes through unchanged
# (most names — sin, cos, tan, abs, floor, ceil, round, min, max, step,
#  smoothstep, clamp, dot, cross, length, distance, normalize, reflect,
#  refract, sign, sqrt, exp, log, log2, exp2, pow, mod, sinh, cosh, tanh,
#  asin, acos, atan, fwidth, trunc, all, any — work in GLSL too)
HLSL_TO_GLSL = {
    "lerp":     "mix",
    "frac":     "fract",
    "rsqrt":    "inversesqrt",
    "atan2":    "atan",
    "ddx":      "dFdx",
    "ddy":      "dFdy",
    "ddx_fine": "dFdx",
    "ddy_fine": "dFdy",
    "IsNaN":    "isnan",
    "fmod":     "mod",
}

# HLSL intrinsics we can't translate cleanly — node gets skipped if any appear.
HLSL_UNSUPPORTED = {
    "mul",        # mul(M, v) is matrix*vector with HLSL conventions; GLSL `*` differs
    "mad",        # fused multiply-add — no direct GLSL equivalent
    "tex2D", "tex2Dlod", "tex2Dproj", "tex3D", "texCUBE",
    "SAMPLE_TEXTURE2D", "SAMPLE_TEXTURE2D_LOD",
    # Unity-specific HLSL helpers:
    "SafeNormalize", "UnpackNormal", "UnpackNormalRGorAG", "UnpackNormalmapRGorAG",
    "TransformObjectToWorld", "TransformWorldToObject",
    "TransformWorldToView",   "TransformObjectToWorldDir",
    "GetVFXSampler",          "PositionInputsFromPixelCoord",
}

# Slot type → output-type rule
TYPE_MAP = {
    "Vector1":   "_ => SgType.Float",
    "Vector2":   "_ => SgType.Vec2",
    "Vector3":   "_ => SgType.Vec3",
    "Vector4":   "_ => SgType.Vec4",
    "ColorRGB":  "_ => SgType.Vec3",
    "ColorRGBA": "_ => SgType.Vec4",
    "Boolean":   "_ => SgType.Bool",
}

DYNAMIC_TYPES = {"DynamicDimensionVector", "DynamicVector", "DynamicMatrix"}


def find_package() -> Path:
    cands = list(SG_PKG_GLOB.glob("com.unity.shadergraph@*"))
    if not cands:
        raise SystemExit("ShaderGraph package not found under Library/PackageCache")
    return cands[0]


SLOT_RE = re.compile(
    r"\[Slot\((\d+)\s*,[^)]*\)\]\s*"   # [Slot(N, …)]
    r"(out\s+)?"                       # optional 'out '
    r"(\w+)\s+(\w+)"                   # Type Name
)


def parse_node(path: Path):
    """Parses one .cs file. Returns dict on success, None to skip."""
    try:
        text = path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        return None, "could not decode file"

    m = re.search(r"class\s+(\w+Node)\s*:\s*CodeFunctionNode", text)
    if not m:
        return None, None  # not a CodeFunctionNode — silently skip
    cls = m.group(1)

    # The static helper method `Unity_Xxx(...)` returns the HLSL body via a
    # `@" ... "` raw string. Hand-parse to handle nested parens in the
    # parameter list (e.g. `[Slot(0, Binding.None)]`).
    sig_m = re.search(r"static\s+string\s+Unity_\w+\s*\(", text)
    if not sig_m:
        return cls, "no Unity_* method"
    i = sig_m.end()
    depth = 1
    while i < len(text) and depth > 0:
        if text[i] == "(": depth += 1
        elif text[i] == ")": depth -= 1
        i += 1
    if depth != 0:
        return cls, "unmatched parens in Unity_* signature"
    params_str = text[sig_m.end():i-1]

    # Find the @" raw-string return after the signature.
    body_m = re.search(r"return\s*@\"([^\"]*)\"", text[i:], re.S)
    if not body_m:
        return cls, "no @ raw-string return"
    body = body_m.group(1)

    in_params, out_params = [], []
    for pm in SLOT_RE.finditer(params_str):
        slot_id = int(pm.group(1))
        is_out  = bool(pm.group(2))
        ptype   = pm.group(3)
        pname   = pm.group(4)
        (out_params if is_out else in_params).append((slot_id, ptype, pname))

    if not out_params:
        return cls, "no [out] parameter"
    if len(out_params) != 1:
        return cls, f"multi-output ({len(out_params)} outs) — needs custom handler"

    out_slot, out_type, out_name = out_params[0]

    # Body shape: `{ Out = <expr>; }` with no embedded semicolons (so we
    # reject multi-statement bodies). Some nodes have a stray `;` after `}` —
    # tolerated. Whitespace flexible.
    body_clean = re.sub(r"\s+", " ", body).strip()
    body_m = re.fullmatch(
        r"\{\s*" + re.escape(out_name) + r"\s*=\s*([^;]+?)\s*;\s*\}\s*;?",
        body_clean,
    )
    if not body_m:
        return cls, "multi-statement body or non-assignment shape"

    expr = body_m.group(1)

    # Check for unsupported HLSL intrinsics.
    for fn in HLSL_UNSUPPORTED:
        if re.search(r"\b" + re.escape(fn) + r"\s*\(", expr):
            return cls, f"unsupported HLSL intrinsic '{fn}'"

    # Reject preprocessor directives, Unity precision-token placeholders
    # (`$precision3` etc. expand at C# time in Unity but won't reach us),
    # and UNITY_* macros.
    if "#" in body or "UNITY_" in expr or "$" in expr:
        return cls, "preprocessor / Unity macro / $ token in body"

    # Reject pre-Out assignments (some nodes have multi-line bodies that
    # happen to fit our shape but rely on temporaries — those have a `=`
    # before the final `Out =` we matched. Defensive — already handled by
    # multi-statement check, but keep as belt-and-braces.)

    # Sort inputs by slot id so a[i] indexing aligns with PatternHandler
    # (which builds args in declaration order).
    in_params.sort(key=lambda p: p[0])

    return {
        "class":      cls,
        "in_params":  in_params,
        "out_type":   out_type,
        "expr":       expr,
    }, None


def translate_expr(expr: str) -> str:
    """Apply HLSL → GLSL function-name substitutions. Whole-word matches only."""
    # saturate(x) → clamp(x, 0.0, 1.0)
    while True:
        m = re.search(r"\bsaturate\s*\(", expr)
        if not m:
            break
        # find matching close paren
        i = m.end()
        depth = 1
        while i < len(expr) and depth > 0:
            if expr[i] == "(": depth += 1
            elif expr[i] == ")": depth -= 1
            i += 1
        inner = expr[m.end():i-1]
        expr = expr[:m.start()] + f"clamp({inner}, 0.0, 1.0)" + expr[i:]

    for hlsl, glsl in HLSL_TO_GLSL.items():
        expr = re.sub(r"\b" + re.escape(hlsl) + r"\b", glsl, expr)
    return expr


def out_type_rule(out_type: str, in_params) -> str:
    if out_type in DYNAMIC_TYPES:
        # Dynamic output: matches whatever flows in. Two+ dynamic inputs → promote.
        dyn_count = sum(1 for p in in_params if p[1] in DYNAMIC_TYPES)
        return "PromoteAB" if dyn_count >= 2 else "SameAsIn"
    if out_type in TYPE_MAP:
        return TYPE_MAP[out_type]
    return "SameAsIn"


def make_pattern(node) -> tuple[str | None, str | None]:
    """Given a parsed node, produce a Pattern(...) line or skip with a reason."""
    cls         = node["class"]
    in_params   = node["in_params"]
    out_type    = node["out_type"]
    expr        = translate_expr(node["expr"])
    in_names    = [p[2] for p in in_params]
    arg_idx     = {n: i for i, n in enumerate(in_names)}
    rule        = out_type_rule(out_type, in_params)
    slot_names  = ", ".join(f'"{n}"' for n in in_names)

    # The Build callback now receives `SgValue[]` (not `SgRhs[]`). For SgRhs
    # contexts (Sg.Call args, Sg.Neg, Sg.Bin's literal-augmented form) we
    # use `v[i].Rhs`; for type-aware ops (Sg.BinPromote) we pass `v[i]`.
    def operand_rhs(token: str) -> str | None:
        token = token.strip()
        if token in arg_idx:
            return f"v[{arg_idx[token]}].Rhs"
        if re.fullmatch(r"-?\d+(?:\.\d*)?(?:[eE][-+]?\d+)?", token):
            return f"Sg.Lit({token})"
        return None

    NUM = r"-?\d+(?:\.\d*)?(?:[eE][-+]?\d+)?"
    OPERAND = rf"(?:[A-Za-z_]\w*|{NUM})"

    # GLSL builtins where every vector operand must share the same rank — we
    # rank-align all SgValue operands to the minimum so HLSL-style truncation
    # works (e.g. lerp(float4, float4, float3) → mix(.xyz, .xyz, .xyz)).
    PROMOTE_FNS = {"mix", "smoothstep", "clamp", "step", "min", "max", "pow", "mod"}

    # Shape 1: simple function call `f(arg, arg, ...)`.
    m = re.fullmatch(r"(\w+)\s*\(\s*([^()]*)\s*\)", expr)
    if m:
        fn = m.group(1)
        raw_args = [s.strip() for s in m.group(2).split(",")] if m.group(2).strip() else []
        # Use CallPromote when (a) the function needs rank-aligned args, and
        # (b) every arg is a declared parameter (not a literal — literals
        # already broadcast naturally).
        if fn in PROMOTE_FNS and raw_args and all(a in arg_idx for a in raw_args):
            args = ", ".join(f"v[{arg_idx[a]}]" for a in raw_args)
            builder = f'v => Sg.CallPromote("{fn}", {args})'
            # CallPromote truncates to the min vec rank across operands; the
            # output rule has to match (otherwise the declared temp type and
            # the actual RHS rank disagree → GLSL type error).
            promote_rule = "PromoteAll" if rule == "PromoteAB" else rule
            return f'Pattern("{cls}", {builder}, {promote_rule}, {slot_names});', None
        coded = [operand_rhs(a) for a in raw_args]
        if all(c is not None for c in coded):
            args = ", ".join(coded)
            builder = f'v => Sg.Call("{fn}", {args})' if args else f'v => Sg.Call("{fn}")'
            return f'Pattern("{cls}", {builder}, {rule}, {slot_names});', None

    # Shape 2: simple binary op `A op B`. Bare-parameter binops (no literals)
    # use Sg.BinPromote so vec_n × vec_m mismatches truncate HLSL-style;
    # binops with a numeric literal stay as plain Sg.Bin (the literal is a
    # scalar; GLSL broadcasts naturally).
    m = re.fullmatch(rf"({OPERAND})\s*([+\-*/])\s*({OPERAND})", expr)
    if m:
        a_text, op, b_text = m.group(1), m.group(2), m.group(3)
        if a_text in arg_idx and b_text in arg_idx:
            builder = f'v => Sg.BinPromote("{op}", v[{arg_idx[a_text]}], v[{arg_idx[b_text]}])'
            # Override the rule with ArithPromote so OutType matches the
            # truncating semantics (min-rank when both vec_n).
            arith_rule = "ArithPromote" if rule == "PromoteAB" else rule
            return f'Pattern("{cls}", {builder}, {arith_rule}, {slot_names});', None
        a_code, b_code = operand_rhs(a_text), operand_rhs(b_text)
        if a_code and b_code:
            builder = f'v => Sg.Bin("{op}", {a_code}, {b_code})'
            return f'Pattern("{cls}", {builder}, {rule}, {slot_names});', None

    # Shape 3: unary minus `-In`.
    m = re.fullmatch(r"-\s*([A-Za-z_]\w*)", expr)
    if m:
        name = m.group(1)
        if name in arg_idx:
            builder = f"v => Sg.Neg(v[{arg_idx[name]}].Rhs)"
            return f'Pattern("{cls}", {builder}, {rule}, {slot_names});', None

    # Shape 4: complex expression — emit as Sg.RawTemplate with structural
    # operands. Each parameter name becomes a positional placeholder `{i}`
    # which gets substituted by Args[i].ToGlsl() at render time, AFTER the
    # optimizer's renumber pass — so embedded temp names stay correct.
    # Process longest names first so e.g. "InMinMax" matches before "In".
    if not in_names or all(re.search(r"\b" + re.escape(n) + r"\b", expr) is not None for n in in_names):
        substituted = expr
        for name in sorted(in_names, key=len, reverse=True):
            substituted = re.sub(
                r"\b" + re.escape(name) + r"\b",
                "\x00" + str(arg_idx[name]) + "\x00",   # temp marker — avoid double-replacement
                substituted,
            )
        # Now convert the markers to {N} placeholders.
        substituted = re.sub(r"\x00(\d+)\x00", r"{\1}", substituted)
        # Wrap in parens so the result is safe to splice into any consumer
        # context (otherwise something like `a ? b : c` consumed by `x * _`
        # parses as `(x * a) ? b : c` due to operator precedence).
        substituted = "(" + substituted + ")"
        # Escape for C# string literal (no interpolation needed — plain string).
        cs_string = substituted.replace("\\", "\\\\").replace("\"", "\\\"")
        if not in_names:
            builder = f'v => Sg.Raw("{cs_string}")'
        else:
            refs = ", ".join(f"v[{i}].Rhs" for i in range(len(in_names)))
            builder = f'v => Sg.RawTemplate("{cs_string}", {refs})'
        return f'Pattern("{cls}", {builder}, {rule}, {slot_names});', None

    return None, "expression references something other than declared parameters"


def main():
    pkg = find_package()
    nodes_dir = pkg / "Editor/Data/Nodes"
    print(f"scanning {nodes_dir}", file=sys.stderr)

    generated: list[tuple[str, str]] = []
    skipped:   list[tuple[str, str]] = []

    for cs in sorted(nodes_dir.rglob("*.cs")):
        result, err = parse_node(cs)
        if result is None and err is None:
            continue  # not a CodeFunctionNode
        if isinstance(result, str):
            skipped.append((result, err or "unknown"))
            continue
        if result is None:
            skipped.append((cs.stem, err or "parse failed"))
            continue
        line, perr = make_pattern(result)
        if line:
            generated.append((result["class"], line))
        else:
            skipped.append((result["class"], perr or "could not classify"))

    # Dedup by class name (some files re-declare in different paths)
    seen = set()
    deduped = []
    for name, line in generated:
        if name in seen: continue
        seen.add(name)
        deduped.append((name, line))
    deduped.sort(key=lambda x: x[0])

    OUT_FILE.parent.mkdir(parents=True, exist_ok=True)
    with OUT_FILE.open("w", encoding="utf-8") as f:
        f.write("// AUTO-GENERATED by tools/generate_sg_nodes.py — do not edit by hand.\n")
        f.write("// Re-run the script to refresh after Unity ships new ShaderGraph nodes.\n\n")
        f.write("using System;\nusing System.Collections.Generic;\n\n")
        f.write("namespace HZExporter.ShaderGraph\n{\n")
        f.write("    static class SgGeneratedNodes\n    {\n")
        f.write("        public delegate void Registrar(string nodeType, Func<SgValue[], SgRhs> build,\n")
        f.write("                                       Func<SgType[], SgType> outType, params string[] inputs);\n\n")
        f.write("        public static void RegisterAll(Registrar Pattern)\n        {\n")
        for _, line in deduped:
            f.write(f"            {line}\n")
        f.write("        }\n\n")
        f.write("        // Type-resolution rules used by the Pattern lines above.\n")
        f.write("        static SgType PromoteAB(SgType[] inT) => SgTypes.Promote(inT[0], inT[1]);\n")
        f.write("        static SgType SameAsIn(SgType[] inT) => inT[0];\n")
        f.write("        static SgType ArithPromote(SgType[] inT)\n        {\n")
        f.write("            int ra = SgTypes.Rank(inT[0]), rb = SgTypes.Rank(inT[1]);\n")
        f.write("            if (ra > 1 && rb > 1 && ra != rb) return SgTypes.FromRank(System.Math.Min(ra, rb));\n")
        f.write("            return SgTypes.Promote(inT[0], inT[1]);\n")
        f.write("        }\n")
        f.write("        // Mirrors Sg.CallPromote: result rank = min over all vector operands\n")
        f.write("        // (scalars broadcast and don't pull rank down).\n")
        f.write("        static SgType PromoteAll(SgType[] inT)\n        {\n")
        f.write("            int minRank = int.MaxValue, maxRank = 0;\n")
        f.write("            foreach (var t in inT)\n            {\n")
        f.write("                int r = SgTypes.Rank(t);\n")
        f.write("                if (r <= 0) continue;\n")
        f.write("                if (r > 1 && r < minRank) minRank = r;\n")
        f.write("                if (r > maxRank) maxRank = r;\n")
        f.write("            }\n")
        f.write("            if (minRank == int.MaxValue) return SgTypes.FromRank(maxRank > 0 ? maxRank : 1);\n")
        f.write("            return SgTypes.FromRank(minRank);\n")
        f.write("        }\n")
        f.write("    }\n}\n")

    SKIP_FILE.parent.mkdir(parents=True, exist_ok=True)
    with SKIP_FILE.open("w", encoding="utf-8") as f:
        f.write(f"# {len(skipped)} nodes need manual handlers\n\n")
        for name, reason in sorted(skipped):
            f.write(f"{name}: {reason}\n")

    print(f"generated: {len(deduped)} nodes -> {OUT_FILE.relative_to(ROOT)}")
    print(f"skipped:   {len(skipped)} nodes -> {SKIP_FILE.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
