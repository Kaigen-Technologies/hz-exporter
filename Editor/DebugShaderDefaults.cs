using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Probes the Unity Shader API to find every plausible source of "default texture"
    /// information for ShaderGraph texture properties. Goal: determine whether there's
    /// a clean public API to resolve a property's default Texture2D asset, vs. having
    /// to parse the .shadergraph file ourselves.
    /// </summary>
    public static class DebugShaderDefaults
    {
        public static void DumpCharacterShader()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/yyyrjis/ModularAnimeCharacter1/URP/Materials/ExampleCharacter1Materials/Character1.mat");
            if (mat == null) { Debug.LogError("Character1.mat not found"); return; }
            var shader = mat.shader;

            string outPath = Path.GetFullPath("ExportedAssets/shader_defaults_probe.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            var sb = new StringBuilder();
            void W(string s) { sb.AppendLine(s); }

            W($"Shader: {shader.name}");
            W($"Asset path: {AssetDatabase.GetAssetPath(shader)}");
            W($"Property count: {shader.GetPropertyCount()}");
            W("");

            // List every method on Shader that mentions "Default" or "Texture" — so we
            // see the full API surface for the version of Unity this project uses.
            W("=== Shader API: methods mentioning Default/Texture ===");
            var methods = typeof(Shader).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            foreach (var m in methods)
            {
                if (m.Name.Contains("Default") || m.Name.Contains("Texture"))
                    W($"  {m.ReturnType.Name} Shader.{m.Name}({string.Join(", ", System.Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name))})");
            }
            W("");

            W("=== ShaderUtil API: methods mentioning Default/Texture ===");
            foreach (var m in typeof(ShaderUtil).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name.Contains("Default") || m.Name.Contains("Texture") || m.Name.Contains("PropertyTex"))
                    W($"  {m.ReturnType.Name} ShaderUtil.{m.Name}({string.Join(", ", System.Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name))})");
            }
            W("");

            W("=== Per-property dump (texture properties only) ===");
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                var type = shader.GetPropertyType(i);
                if (type != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                string name = shader.GetPropertyName(i);

                W($"[{i}] {name}  (type={type})");

                // Public API: GetPropertyTextureDefaultName — returns the default texture's
                // *string name* (e.g., "white", "black") OR for ShaderGraph custom defaults
                // it might return the asset's filename. Probe.
                try
                {
                    string defName = shader.GetPropertyTextureDefaultName(i);
                    W($"    Shader.GetPropertyTextureDefaultName(i) = \"{defName}\"");
                }
                catch (System.Exception e)
                {
                    W($"    Shader.GetPropertyTextureDefaultName(i) THREW: {e.Message}");
                }

                // What does the material currently report?
                Texture cur = mat.HasProperty(name) ? mat.GetTexture(name) : null;
                W($"    mat.GetTexture(name) = {(cur != null ? cur.name + " (" + AssetDatabase.GetAssetPath(cur) + ")" : "<null>")}");

                // Check if there's an attribute / texture default reference accessible via reflection.
                // (Try the legacy ShaderUtil.GetTexEnv and similar.)
            }

            File.WriteAllText(outPath, sb.ToString());
            Debug.Log($"[DebugShaderDefaults] wrote {outPath}");
        }
    }
}
