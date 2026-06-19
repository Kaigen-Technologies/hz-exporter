using UnityEditor;
using UnityEngine;

namespace HZExporter
{
    /// <summary>Editor window for configuring the HZ exporter (opened via Hz Exporter/Configure).</summary>
    public class HZExporterConfigWindow : EditorWindow
    {
        HZExporterConfig _config;

        [MenuItem("Hz Exporter/Configure")]
        public static void Open()
        {
            var window = GetWindow<HZExporterConfigWindow>(false, "Hz Exporter Config");
            window.minSize = new Vector2(460, 240);
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Export", EditorStyles.boldLabel);
            _config.outputDir = EditorGUILayout.TextField(
                new GUIContent("Output Dir", "Local staging dir for exports (relative to project root or absolute)."),
                _config.outputDir);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Engine Project", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _config.engineProjectPath = EditorGUILayout.TextField(
                    new GUIContent("Project Path", "Root of the hz-engine project to copy exports into. Leave empty to disable copying."),
                    _config.engineProjectPath);
                if (GUILayout.Button("Browse…", GUILayout.Width(70)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Select engine project root", _config.engineProjectPath, "");
                    if (!string.IsNullOrEmpty(picked)) _config.engineProjectPath = picked;
                }
            }

            _config.engineAssetsSubdir = EditorGUILayout.TextField(
                new GUIContent("Assets Subdir", "Path inside the engine project where exports land."),
                _config.engineAssetsSubdir);

            _config.autoCopyOnExport = EditorGUILayout.Toggle(
                new GUIContent("Auto-copy on Export", "Copy the staging dir into the engine project after Export Active Scene."),
                _config.autoCopyOnExport);

            EditorGUILayout.Space();

            string dest = _config.ResolvedEngineDir();
            EditorGUILayout.HelpBox(
                dest == null
                    ? "No engine project set — exports stay local; copying is disabled."
                    : $"Exports copy to:\n{dest}",
                dest == null ? MessageType.Info : MessageType.None);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save", GUILayout.Height(28)))
                {
                    _config.Save();
                    Debug.Log("[HZExporterConfig] saved.");
                }
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_config.engineProjectPath)))
                {
                    if (GUILayout.Button("Save & Copy Now", GUILayout.Height(28)))
                    {
                        _config.Save();
                        HZExportCommands.CopyToEngine();
                    }
                }
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_config.engineProjectPath)))
            {
                if (HZEnginePlayer.IsRunning)
                {
                    GUI.backgroundColor = new Color(0.9f, 0.5f, 0.5f);
                    if (GUILayout.Button("■ Stop Engine", GUILayout.Height(32)))
                        HZEnginePlayer.Stop();
                }
                else
                {
                    GUI.backgroundColor = new Color(0.5f, 0.9f, 0.5f);
                    if (GUILayout.Button("▶ Play Engine", GUILayout.Height(32)))
                    {
                        _config.Save();
                        HZEnginePlayer.Play();
                    }
                }
                GUI.backgroundColor = Color.white;
            }
        }

        void OnEnable()
        {
            // Reload working copy and keep the Play/Stop button state fresh.
            _config = JsonUtility.FromJson<HZExporterConfig>(JsonUtility.ToJson(HZExporterConfig.Instance));
            EditorApplication.update += Repaint;
        }

        void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }
    }
}
