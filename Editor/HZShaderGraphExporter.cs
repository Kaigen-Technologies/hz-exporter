using System.IO;
using UnityEngine;
using HZExporter.ShaderGraph;

namespace HZExporter
{
    // Public entry point for translating Unity ShaderGraph (.shadergraph) files
    // into hz-engine-v5 .hz.glsl shader sources.
    //
    // Pipeline (each stage operates on a typed model — only SgParser touches
    // the raw JSON):
    //
    //     .shadergraph (JSON)
    //         │
    //         ▼  SgParser           → SgGraph (Nodes, Slots, Edges, Variant)
    //         ▼  SgLowering         → list of GLSL statements with SSA temps
    //         ▼  SgCodegen          → template fill (variant picks template)
    //         ▼
    //     .hz.glsl
    //
    // Adding support for a new node type usually means one entry in the
    // SgNodes registry (SgLowering.cs); only nodes with multi-output, custom
    // varying needs, or property access need a custom NodeHandler.
    public static class HZShaderGraphExporter
    {
        public static void ExportFile(string shadergraphPath, string outDir)
        {
            string text = File.ReadAllText(shadergraphPath);
            var graph = SgParser.Parse(text);
            // Source name preserved in the generated comment (traceability),
            // but the on-disk filename is snake-cased so all engine paths stay
            // consistent — `1_UnlitTest.shadergraph` → `1_unlit_test.hz.glsl`.
            string name = Path.GetFileNameWithoutExtension(shadergraphPath);
            string fileStem = SgNames.ToSnakeCase(name);
            // Grass-capability is declared externally (grass_shaders.json), not parsed
            // from the graph — set it before codegen so Emit can produce the sibling.
            graph.IsGrassCapable = HZGrassShaders.IsCapable(name, fileStem);
            var emitted = SgCodegen.Emit(graph, name);

            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, fileStem + ".hz.glsl");
            File.WriteAllText(outPath, emitted.Forward);
            Debug.Log($"[HZ-SG] {name} -> {outPath} (variant={graph.Variant})");

            // Alpha-clipped graphs also produce a sibling shadow caster
            // shader so the material's shadow_shader ref has somewhere to
            // point. Same vertex displacement body; alpha-clip-only fs.
            if (emitted.Shadow != null)
            {
                string shadowPath = Path.Combine(outDir, fileStem + "_shadow.hz.glsl");
                File.WriteAllText(shadowPath, emitted.Shadow);
                Debug.Log($"[HZ-SG] {name} -> {shadowPath} (shadow caster)");
            }

            // Grass-capable Lit graphs also produce a sibling `_grass` shader:
            // identical @fs/@material, grass-SSBO @vs. Materials used on grass
            // point shader_source at this file (see HZTerrainExporter / GLB grass path).
            if (emitted.ForwardGrass != null)
            {
                string grassPath = Path.Combine(outDir, fileStem + "_grass.hz.glsl");
                File.WriteAllText(grassPath, emitted.ForwardGrass);
                Debug.Log($"[HZ-SG] {name} -> {grassPath} (grass)");
            }
        }
    }
}
