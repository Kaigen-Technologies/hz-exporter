using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using MagicaCloth2;

namespace HZExporter
{
    // Records ~8s of MagicaCloth BoneCloth simulation (per editor/play tick, per
    // bone: world position [GLB X-negated] + local rotation) from the start of
    // Play, keyed by elapsed time, and dumps a CSV to compare 1:1 vs the engine.
    // Pure editor class (no runtime MonoBehaviour): samples in
    // EditorApplication.update while playing, which fires after the play frame so
    // bone transforms are final. Auto-starts on entering Play.
    [InitializeOnLoad]
    public static class HZClothRecorder
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        const string OutPath = "D:/dev/gabrieldechichi/hz-engine-v5-2/out/unity_sim_rec.csv";
        const float Duration = 8.0f;

        struct Chain { public string name; public List<Transform> bones; }
        static List<Chain> chains;
        static StringBuilder sb;
        static float t0;
        static bool recording, collected, dumped;

        static HZClothRecorder()
        {
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.EnteredPlayMode)
                {
                    recording = true; collected = false; dumped = false;
                    chains = new List<Chain>();
                    sb = new StringBuilder(8 * 1024 * 1024);
                    sb.Append("t,cloth,bone,wx,wy,wz,lqx,lqy,lqz,lqw\n");
                    t0 = Time.realtimeSinceStartup;
                }
                else if (s == PlayModeStateChange.ExitingPlayMode)
                {
                    if (recording && !dumped) Dump();
                    recording = false;
                }
            };
            EditorApplication.update += OnUpdate;
        }

        static void Collect()
        {
            foreach (var c in Object.FindObjectsByType<MagicaCloth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var ct = c.SerializeData.clothType;
                if (ct != ClothProcess.ClothType.BoneCloth &&
                    ct != ClothProcess.ClothType.BoneSpring) continue;
                var seen = new HashSet<Transform>();
                var bones = new List<Transform>();
                foreach (var rb in c.SerializeData.rootBones)
                {
                    if (rb == null) continue;
                    foreach (var tr in rb.GetComponentsInChildren<Transform>(true))
                        if (seen.Add(tr)) bones.Add(tr);
                }
                if (bones.Count > 0)
                    chains.Add(new Chain { name = c.gameObject.name, bones = bones });
            }
            collected = true;
            Debug.Log($"[HZClothRecorder] recording {chains.Count} cloths for {Duration}s");
        }

        static void OnUpdate()
        {
            if (!recording || !EditorApplication.isPlaying) return;
            if (!collected) Collect();
            float t = Time.realtimeSinceStartup - t0;
            if (t > Duration) { Dump(); recording = false; return; }
            foreach (var ch in chains)
                foreach (var b in ch.bones)
                {
                    if (b == null) continue;
                    Vector3 p = b.position;
                    Quaternion q = b.localRotation;
                    sb.Append(t.ToString("F4", CI)).Append(',')
                      .Append(ch.name).Append(',').Append(b.name).Append(',')
                      .Append((-p.x).ToString("F4", CI)).Append(',')
                      .Append(p.y.ToString("F4", CI)).Append(',')
                      .Append(p.z.ToString("F4", CI)).Append(',')
                      .Append(q.x.ToString("F5", CI)).Append(',')
                      .Append(q.y.ToString("F5", CI)).Append(',')
                      .Append(q.z.ToString("F5", CI)).Append(',')
                      .Append(q.w.ToString("F5", CI)).Append('\n');
                }
        }

        static void Dump()
        {
            if (dumped || sb == null) return;
            dumped = true;
            Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
            File.WriteAllText(OutPath, sb.ToString());
            Debug.Log($"[HZClothRecorder] wrote {OutPath} ({sb.Length} bytes)");
        }
    }
}
