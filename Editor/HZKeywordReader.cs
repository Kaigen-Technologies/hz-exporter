using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using HZExporter.ShaderGraph;

namespace HZExporter
{
    /// <summary>
    /// Reads a Unity Material's shader-keyword state by re-parsing the material's
    /// .shadergraph for its declared keyword catalog, then querying each keyword's
    /// Material-side float property (or falling back to the graph default for
    /// non-exposed keywords like URP-pipeline globals).
    ///
    /// Output is the engine's (name, value) pair convention:
    ///   Boolean → { name = ReferenceName, value = "" } when ON; omitted when OFF
    ///   Enum    → { name = ReferenceName, value = Entries[idx].ReferenceName }
    /// matching MaterialKeywordBlob in the engine.
    /// </summary>
    public static class HZKeywordReader
    {
        public struct KeywordKV
        {
            public string name;
            public string value;
        }

        // shadergraph path → parsed graph. Avoids reparsing per material in a
        // batch export.
        static readonly Dictionary<string, SgGraph> s_graphCache =
            new Dictionary<string, SgGraph>();

        /// <summary>Force-drop the parsed-graph cache. Call when shadergraph files
        /// change on disk during an editor session.</summary>
        public static void ClearCache() => s_graphCache.Clear();

        /// <summary>Returns the engine-side keyword list for a Unity Material.
        /// Empty list if the shader isn't a .shadergraph or has no keywords.</summary>
        public static List<KeywordKV> ReadEnabled(Material mat)
        {
            var result = new List<KeywordKV>();
            if (mat == null || mat.shader == null) return result;

            string shaderPath = AssetDatabase.GetAssetPath(mat.shader);
            if (string.IsNullOrEmpty(shaderPath) || !shaderPath.EndsWith(".shadergraph"))
                return result;

            if (!s_graphCache.TryGetValue(shaderPath, out var graph))
            {
                string text;
                try { text = File.ReadAllText(shaderPath); }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[HZKeywordReader] failed to read '{shaderPath}': {e.Message}");
                    return result;
                }
                graph = SgParser.Parse(text);
                s_graphCache[shaderPath] = graph;
            }

            foreach (var kw in graph.Keywords)
            {
                if (string.IsNullOrEmpty(kw.ReferenceName)) continue;

                // mat.HasProperty(refName) tells us whether ShaderGraph created
                // a per-material override slot for this keyword. Predefined /
                // global keywords (URP pipeline keywords like
                // _MAIN_LIGHT_SHADOWS_CASCADE) have no Material property and
                // fall through to the graph default.
                float v;
                if (mat.HasProperty(kw.ReferenceName))
                    v = mat.GetFloat(kw.ReferenceName);
                else
                    v = kw.DefaultValue;

                if (kw.IsBoolean)
                {
                    // Engine convention: list a boolean keyword only when ON.
                    // "Not listed" = OFF variant.
                    if (v >= 0.5f)
                        result.Add(new KeywordKV { name = kw.ReferenceName, value = "" });
                }
                else
                {
                    int idx = Mathf.Clamp(Mathf.RoundToInt(v), 0, kw.Entries.Count - 1);
                    if (kw.Entries.Count == 0 || idx < 0) continue;
                    var entry = kw.Entries[idx];
                    if (string.IsNullOrEmpty(entry.ReferenceName)) continue;
                    result.Add(new KeywordKV {
                        name = kw.ReferenceName,
                        value = entry.ReferenceName,
                    });
                }
            }
            return result;
        }
    }
}
