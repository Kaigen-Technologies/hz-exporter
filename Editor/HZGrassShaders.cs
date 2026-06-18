using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Declarative list of shaders that should get a grass variant, loaded from
    /// Assets/Editor/HZExporter/grass_shaders.json. Consumed by HZShaderGraphExporter
    /// to decide whether to emit a sibling `<name>_grass.hz.glsl` (grass-SSBO @vs,
    /// identical @fs/@material). Mirrors HZShaderMap's mtime-reload loader.
    /// </summary>
    public static class HZGrassShaders
    {
        [Serializable]
        class RawConfig
        {
            public List<string> shaders;
        }

        static HashSet<string> s_names;   // lowercased source-name + file-stem entries
        static DateTime s_mtime;

        static string GetConfigPath()
        {
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(HZGrassShaders).Assembly);
            return pkg != null
                ? Path.Combine(pkg.resolvedPath, "Editor", "grass_shaders.json")
                : Path.GetFullPath("Assets/Editor/HZExporter/grass_shaders.json");
        }

        static void EnsureLoaded()
        {
            string path = GetConfigPath();
            bool exists = File.Exists(path);
            DateTime mtime = exists ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (s_names != null && mtime == s_mtime) return;

            s_names = new HashSet<string>();
            s_mtime = mtime;
            if (!exists)
            {
                Debug.LogWarning($"[HZGrassShaders] config not found at {path}; no grass variants will be emitted.");
                return;
            }
            var raw = JsonUtility.FromJson<RawConfig>(File.ReadAllText(path));
            if (raw?.shaders != null)
                foreach (var s in raw.shaders)
                    if (!string.IsNullOrEmpty(s))
                        s_names.Add(s.ToLowerInvariant());
        }

        /// <summary>True when either the graph's source name (e.g. "Vegetation") or its
        /// snake-cased file stem (e.g. "vegetation") is listed in grass_shaders.json.</summary>
        public static bool IsCapable(string sourceName, string fileStem)
        {
            EnsureLoaded();
            if (s_names.Count == 0) return false;
            if (!string.IsNullOrEmpty(sourceName) && s_names.Contains(sourceName.ToLowerInvariant())) return true;
            if (!string.IsNullOrEmpty(fileStem) && s_names.Contains(fileStem.ToLowerInvariant())) return true;
            return false;
        }
    }
}
