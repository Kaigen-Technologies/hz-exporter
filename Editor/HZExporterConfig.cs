using System.IO;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Per-machine exporter configuration, persisted as JSON under the project's
    /// UserSettings/ folder (gitignored by Unity convention) so absolute, machine-
    /// specific paths are never committed. Edited via HZExporterConfigWindow.
    /// </summary>
    [System.Serializable]
    public class HZExporterConfig
    {
        // Local staging directory for exports (relative to project root, or absolute).
        public string outputDir = "ExportedAssets";

        // Root of the hz-engine project to copy exports into. Empty = no copy.
        public string engineProjectPath = "";

        // Subdir inside the engine project where exports land.
        public string engineAssetsSubdir = "assets/unity";

        // Engine project shader root (the dir that IS `shaders/` in an engine path).
        // Translated ShaderGraphs land in the subdir their shader_mapping.json
        // engineShader path names, e.g. /shaders/unity/water.hz.glsl ->
        // <shaderRootDir>/unity/water.hz.glsl. Empty = stage under outputDir/shaders.
        public string shaderRootDir = "";

        // When true, ExportScene copies the staging dir into the engine project after export.
        public bool autoCopyOnExport = false;

        // ---- Persistence ----
        const string ConfigPath = "UserSettings/HzExporter.json";

        static HZExporterConfig s_instance;

        public static HZExporterConfig Instance
        {
            get
            {
                if (s_instance == null) s_instance = Load();
                return s_instance;
            }
        }

        static HZExporterConfig Load()
        {
            try
            {
                string path = Path.GetFullPath(ConfigPath);
                if (File.Exists(path))
                    return JsonUtility.FromJson<HZExporterConfig>(File.ReadAllText(path)) ?? new HZExporterConfig();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HZExporterConfig] failed to load {ConfigPath}: {e.Message}");
            }
            return new HZExporterConfig();
        }

        public void Save()
        {
            try
            {
                string path = Path.GetFullPath(ConfigPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(this, true));
                s_instance = this;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HZExporterConfig] failed to save {ConfigPath}: {e.Message}");
            }
        }

        /// <summary>Absolute path to the local staging output dir.</summary>
        public string ResolvedOutputDir()
        {
            string dir = string.IsNullOrEmpty(outputDir) ? "ExportedAssets" : outputDir;
            return Path.GetFullPath(dir);
        }

        /// <summary>Absolute engine shader root, or null when not configured.</summary>
        public string ResolvedShaderRootDir()
        {
            if (string.IsNullOrEmpty(shaderRootDir)) return null;
            return Path.GetFullPath(shaderRootDir);
        }

        /// <summary>Absolute engine destination dir, or null when not configured.</summary>
        public string ResolvedEngineDir()
        {
            if (string.IsNullOrEmpty(engineProjectPath)) return null;
            string sub = string.IsNullOrEmpty(engineAssetsSubdir) ? "assets/unity" : engineAssetsSubdir;
            return Path.Combine(engineProjectPath, sub);
        }
    }
}
