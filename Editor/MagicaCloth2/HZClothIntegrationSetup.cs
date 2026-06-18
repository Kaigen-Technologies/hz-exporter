using UnityEditor;

namespace HZExporter
{
    [InitializeOnLoad]
    static class HZClothIntegrationSetup
    {
        static HZClothIntegrationSetup()
        {
            HZClothIntegration.ExportMeshCloths = HZClothExporter.ExportMeshClothsUnder;
        }
    }
}
