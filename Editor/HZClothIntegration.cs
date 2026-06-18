using System.Collections.Generic;
using UnityEngine;

namespace HZExporter
{
    public static class HZClothIntegration
    {
        public delegate List<string> ClothExportDelegate(GameObject go, SkinnedMeshRenderer[] smrs, string outDir);
        public static ClothExportDelegate ExportMeshCloths;
    }
}
