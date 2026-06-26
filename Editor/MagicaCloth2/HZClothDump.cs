using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using MagicaCloth2;

namespace HZExporter
{
    // Dumps the LIVE MagicaCloth SerializeData of every cloth in the active scene
    // (raw Unity values, no conversion) so we can diff against the exported .hzt.
    public static class HZClothDump
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        const string OutPath = "D:/dev/gabrieldechichi/hz-engine-v5-2/out/cloth_unity_dump.txt";
        const string ReexportDir = "D:/dev/gabrieldechichi/hz-engine-v5-2/out/cloth_reexport";

        static string F(float v) => v.ToString("F6", CI);

        public static void DumpActiveSceneCloths()
        {
            var sb = new StringBuilder(64 * 1024);
            int n = 0;
            foreach (var cloth in Object.FindObjectsByType<MagicaCloth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var sd = cloth.SerializeData;
                var inertia = sd.inertiaConstraint;
                var dist = sd.distanceConstraint;
                var ar = sd.angleRestorationConstraint;
                var al = sd.angleLimitConstraint;
                var mo = sd.motionConstraint;
                var cc = sd.colliderCollisionConstraint;
                var sc = sd.selfCollisionConstraint;
                var tet = sd.tetherConstraint;

                sb.Append("=== ").Append(cloth.gameObject.name).Append(" ===\n");
                sb.Append("clothType ").Append(sd.clothType).Append('\n');
                sb.Append("gravity ").Append(F(sd.gravity)).Append('\n');
                sb.Append("gravityDirection ").Append(sd.gravityDirection.ToString("F6", CI)).Append('\n');
                sb.Append("gravityFalloff ").Append(F(sd.gravityFalloff)).Append('\n');
                sb.Append("damping.value ").Append(F(sd.damping.value)).Append(" useCurve ").Append(sd.damping.useCurve).Append('\n');
                sb.Append("stablizationTimeAfterReset ").Append(F(sd.stablizationTimeAfterReset)).Append('\n');
                sb.Append("animationPoseRatio ").Append(F(sd.animationPoseRatio)).Append('\n');
                sb.Append("distance.stiffness.value ").Append(F(dist.stiffness.value)).Append(" useCurve ").Append(dist.stiffness.useCurve).Append('\n');
                sb.Append("tether.distanceCompression ").Append(F(tet.distanceCompression)).Append('\n');
                sb.Append("angleRestoration.use ").Append(ar.useAngleRestoration).Append('\n');
                sb.Append("angleRestoration.stiffness.value ").Append(F(ar.stiffness.value)).Append(" useCurve ").Append(ar.stiffness.useCurve).Append('\n');
                sb.Append("angleRestoration.velocityAttenuation ").Append(F(ar.velocityAttenuation)).Append('\n');
                sb.Append("angleRestoration.gravityFalloff ").Append(F(ar.gravityFalloff)).Append('\n');
                sb.Append("angleLimit.use ").Append(al.useAngleLimit).Append('\n');
                sb.Append("angleLimit.limitAngle.value ").Append(F(al.limitAngle.value)).Append(" useCurve ").Append(al.limitAngle.useCurve).Append('\n');
                sb.Append("angleLimit.stiffness ").Append(F(al.stiffness)).Append('\n');
                sb.Append("motion.useMaxDistance ").Append(mo.useMaxDistance).Append('\n');
                sb.Append("motion.maxDistance.value ").Append(F(mo.maxDistance.value)).Append('\n');
                sb.Append("motion.stiffness ").Append(F(mo.stiffness)).Append('\n');
                sb.Append("motion.useBackstop ").Append(mo.useBackstop).Append('\n');
                sb.Append("motion.backstopRadius ").Append(F(mo.backstopRadius)).Append('\n');
                sb.Append("motion.backstopDistance.value ").Append(F(mo.backstopDistance.value)).Append('\n');
                sb.Append("normalAxis ").Append(sd.normalAxis).Append('\n');
                sb.Append("radius.value ").Append(F(sd.radius.value)).Append(" useCurve ").Append(sd.radius.useCurve).Append('\n');
                sb.Append("collider.mode ").Append(cc.mode).Append('\n');
                sb.Append("collider.friction ").Append(F(cc.friction)).Append('\n');
                sb.Append("colliderList.count ").Append(cc.colliderList != null ? cc.colliderList.Count : 0).Append('\n');
                sb.Append("selfCollision.mode ").Append(sc.selfMode).Append('\n');
                sb.Append("inertia.worldInertia ").Append(F(inertia.worldInertia)).Append('\n');
                sb.Append("inertia.depthInertia ").Append(F(inertia.depthInertia)).Append('\n');
                sb.Append("inertia.movementInertiaSmoothing ").Append(F(inertia.movementInertiaSmoothing)).Append('\n');
                sb.Append("inertia.movementSpeedLimit use ").Append(inertia.movementSpeedLimit.use).Append(" value ").Append(F(inertia.movementSpeedLimit.value)).Append('\n');
                sb.Append("inertia.rotationSpeedLimit use ").Append(inertia.rotationSpeedLimit.use).Append(" value ").Append(F(inertia.rotationSpeedLimit.value)).Append('\n');
                sb.Append("inertia.localInertia ").Append(F(inertia.localInertia)).Append('\n');
                sb.Append("inertia.particleSpeedLimit use ").Append(inertia.particleSpeedLimit.use).Append(" value ").Append(F(inertia.particleSpeedLimit.value)).Append('\n');
                sb.Append("inertia.centrifualAcceleration ").Append(F(inertia.centrifualAcceleration)).Append('\n');
                sb.Append("inertia.teleportMode ").Append(inertia.teleportMode).Append('\n');
                sb.Append("inertia.teleportDistance ").Append(F(inertia.teleportDistance)).Append('\n');
                sb.Append("blendWeight ").Append(F(sd.blendWeight)).Append('\n');
                sb.Append("rotationalInterpolation ").Append(F(sd.rotationalInterpolation)).Append('\n');
                sb.Append("rootRotation ").Append(F(sd.rootRotation)).Append('\n');
                int rb = sd.rootBones != null ? sd.rootBones.Count : 0;
                sb.Append("rootBones ").Append(rb);
                for (int i = 0; i < rb; i++) sb.Append(' ').Append(sd.rootBones[i] ? sd.rootBones[i].name : "null");
                sb.Append('\n');
                sb.Append('\n');
                n++;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
            File.WriteAllText(OutPath, sb.ToString());
            Debug.Log($"[HZClothDump] dumped {n} cloths -> {OutPath}");

            // also re-export with the live exporter so we can diff vs committed .hzt
            Directory.CreateDirectory(ReexportDir);
            int ex = 0;
            foreach (var cloth in Object.FindObjectsByType<MagicaCloth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var ct = cloth.SerializeData.clothType;
                if (ct != ClothProcess.ClothType.BoneCloth &&
                    ct != ClothProcess.ClothType.BoneSpring &&
                    ct != ClothProcess.ClothType.MeshCloth) continue;
                var fn = HZClothExporter.ExportClothToDir(cloth, ReexportDir, null);
                if (fn != null) ex++;
            }
            Debug.Log($"[HZClothDump] re-exported {ex} cloths -> {ReexportDir}");
        }
    }
}
