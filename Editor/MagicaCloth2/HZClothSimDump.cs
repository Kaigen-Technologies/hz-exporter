using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using MagicaCloth2;

namespace HZExporter
{
    // Dumps the LIVE simulated cloth-bone transforms (Play Mode) so we can diff
    // MagicaCloth's actual runtime output against the engine's. Bones are walked
    // in the SAME order the exporter writes bone_names (rootBones + descendants,
    // dedup), so line N here == bone N in the .hzt.
    public static class HZClothSimDump
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        const string OutPath = "D:/dev/gabrieldechichi/hz-engine-v5-2/out/cloth_unity_sim.txt";
        static string F(float v) => v.ToString("F4", CI);

        [MenuItem("HZ/Dump Cloth Sim State")]
        public static void DumpClothSimState()
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("playing=").Append(Application.isPlaying).Append('\n');
            foreach (var cloth in Object.FindObjectsByType<MagicaCloth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var sd = cloth.SerializeData;
                if (sd.clothType != ClothProcess.ClothType.BoneCloth &&
                    sd.clothType != ClothProcess.ClothType.BoneSpring) continue;

                var seen = new HashSet<Transform>();
                var bones = new List<Transform>();
                foreach (var root in sd.rootBones)
                {
                    if (root == null) continue;
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (seen.Add(t)) bones.Add(t);
                }
                sb.Append("=== ").Append(cloth.gameObject.name)
                  .Append(" type=").Append((int)sd.clothType)
                  .Append(" bones=").Append(bones.Count).Append(" ===\n");
                foreach (var t in bones)
                {
                    Vector3 lp = t.localPosition;
                    Vector3 le = t.localEulerAngles;
                    Vector3 wp = t.position;
                    sb.Append(t.name)
                      .Append(" | lpos ").Append(F(lp.x)).Append(' ').Append(F(lp.y)).Append(' ').Append(F(lp.z))
                      .Append(" | leuler ").Append(F(le.x)).Append(' ').Append(F(le.y)).Append(' ').Append(F(le.z))
                      .Append(" | wpos ").Append(F(wp.x)).Append(' ').Append(F(wp.y)).Append(' ').Append(F(wp.z))
                      .Append('\n');
                }
                sb.Append('\n');
            }
            Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
            File.WriteAllText(OutPath, sb.ToString());
            Debug.Log($"[HZClothSimDump] wrote {OutPath} (playing={Application.isPlaying})");
        }
    }
}
