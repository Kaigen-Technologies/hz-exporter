using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity AnimationClip as an AnimationBlobAsset (.hza) for hz-engine-v5.
    /// Uses baked sampling at a fixed rate.
    /// </summary>
    public static class HZAnimationExporter
    {
        // C struct sizes (must match engine exactly)
        // AnimationBlobAsset: header(48) + length(4) + joint_count(4) + keyframes BlobPtr(16) = 72
        const int SIZEOF_ANIMATION_BLOB_ASSET = 72;
        const int SIZEOF_KEYFRAME_BLOB_ASSET = 24;  // 4 timestamp + 4 transform_count + 16 BlobPtr
        // JointTransformBlob: joint_index(4) + translation(12) + rotation(16) + scale(12) = 44 (no padding in v5)
        const int SIZEOF_JOINT_TRANSFORM_BLOB = 44;
        const int SIZEOF_STRING_BLOB = 8;

        const float SAMPLE_RATE = 30f;

        /// <summary>
        /// Export an AnimationClip using a reference skeleton.
        /// </summary>
        public static void Export(AnimationClip clip, GameObject skeletonRoot,
            Dictionary<string, int> boneNameToIndex, int jointCount, string outputPath)
        {
            float duration = clip.length;
            int keyframeCount = Mathf.Max(1, Mathf.RoundToInt(duration * SAMPLE_RATE) + 1);

            // Build a name → Transform lookup from the skeleton hierarchy
            var transformByName = new Dictionary<string, Transform>();
            foreach (var t in skeletonRoot.GetComponentsInChildren<Transform>())
            {
                // First match wins — avoids duplicates from nested objects
                if (!transformByName.ContainsKey(t.name))
                    transformByName[t.name] = t;
            }

            // Match bones by name, ordered by joint index (not hierarchy traversal order)
            var boneTransforms = new List<(Transform t, int jointIndex)>();
            foreach (var kvp in boneNameToIndex)
            {
                if (transformByName.TryGetValue(kvp.Key, out Transform t))
                    boneTransforms.Add((t, kvp.Value));
            }

            if (boneTransforms.Count == 0)
            {
                Debug.LogError($"[HZAnimationExporter] No matching bones found for clip '{clip.name}'");
                return;
            }

            Debug.Log($"[HZAnimationExporter] Matched {boneTransforms.Count}/{boneNameToIndex.Count} bones by name");

            // Sample the animation
            var keyframes = new KeyframeData[keyframeCount];
            for (int ki = 0; ki < keyframeCount; ki++)
            {
                float time = (ki < keyframeCount - 1)
                    ? ki / SAMPLE_RATE
                    : duration; // ensure last frame is exactly at duration

                clip.SampleAnimation(skeletonRoot, time);

                var transforms = new JointTransformData[boneTransforms.Count];
                for (int bi = 0; bi < boneTransforms.Count; bi++)
                {
                    var (t, jointIndex) = boneTransforms[bi];
                    var pos = t.localPosition;
                    var rot = t.localRotation;
                    var scl = t.localScale;

                    transforms[bi] = new JointTransformData
                    {
                        jointIndex = (uint)jointIndex,
                        // Coordinate conversion: negate X (undo Unity's LH import flip)
                        tx = -pos.x, ty = pos.y, tz = pos.z,
                        // Quaternion X-mirror: (qx, -qy, -qz, qw)
                        rx = rot.x, ry = -rot.y, rz = -rot.z, rw = rot.w,
                        sx = scl.x, sy = scl.y, sz = scl.z,
                    };
                }

                keyframes[ki] = new KeyframeData { timestamp = time, transforms = transforms };
            }

            // Write binary
            WriteBinary(clip.name, duration, jointCount, keyframes, outputPath);
        }

        struct JointTransformData
        {
            public uint jointIndex;
            public float tx, ty, tz;
            public float rx, ry, rz, rw;
            public float sx, sy, sz;
        }

        struct KeyframeData
        {
            public float timestamp;
            public JointTransformData[] transforms;
        }

        static void WriteBinary(string clipName, float duration, int jointCount,
            KeyframeData[] keyframes, string outputPath)
        {
            int keyframeCount = keyframes.Length;

            // Calculate total joint transforms across all keyframes
            int totalTransforms = 0;
            foreach (var kf in keyframes)
                totalTransforms += kf.transforms.Length;

            var w = new HZBlobWriter(SIZEOF_ANIMATION_BLOB_ASSET +
                keyframeCount * SIZEOF_KEYFRAME_BLOB_ASSET +
                totalTransforms * SIZEOF_JOINT_TRANSFORM_BLOB + 256);

            int animBase = w.Position;

            // === AnimationBlobAsset (72 bytes) ===
            // header(48) + length(4) + joint_count(4) + keyframes BlobPtr(16)
            w.WriteHeader("AnimationBlobAsset");
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob"); // no dependencies

            w.WriteF32(duration);             // length
            w.WriteU32((uint)jointCount);     // joint_count
            // No keyframe_count field in v5 — count is implicit in BlobArray

            int keyframesPtrPos = w.ReserveBlobPtr(); // keyframes BlobPtr

            // No trailing padding in v5
            Debug.Assert(w.Position == SIZEOF_ANIMATION_BLOB_ASSET,
                $"AnimationBlobAsset size mismatch: expected {SIZEOF_ANIMATION_BLOB_ASSET}, got {w.Position}");

            // === KeyframeBlobAsset array ===
            int keyframesArrayPos = w.Position;
            w.PatchBlobPtr(keyframesPtrPos, keyframesArrayPos,
                keyframeCount, SIZEOF_KEYFRAME_BLOB_ASSET, "KeyframeBlobAsset");

            int[] kfTransformPtrPositions = new int[keyframeCount];
            int[] kfBasePositions = new int[keyframeCount];

            for (int ki = 0; ki < keyframeCount; ki++)
            {
                kfBasePositions[ki] = w.Position;

                w.WriteF32(keyframes[ki].timestamp);
                w.WriteU32((uint)keyframes[ki].transforms.Length);
                kfTransformPtrPositions[ki] = w.ReserveBlobPtr();

                Debug.Assert(w.Position == kfBasePositions[ki] + SIZEOF_KEYFRAME_BLOB_ASSET,
                    $"KeyframeBlobAsset[{ki}] size mismatch");
            }

            // === Align to 16 before transform data ===
            w.AlignTo(16);

            // === JointTransformBlob data (packed per keyframe) ===
            for (int ki = 0; ki < keyframeCount; ki++)
            {
                var transforms = keyframes[ki].transforms;
                int transformDataPos = w.Position;

                w.PatchBlobPtr(kfTransformPtrPositions[ki], transformDataPos,
                    transforms.Length, SIZEOF_JOINT_TRANSFORM_BLOB, "JointTransformBlob");

                for (int ti = 0; ti < transforms.Length; ti++)
                {
                    var jt = transforms[ti];
                    w.WriteU32(jt.jointIndex);              // 4
                    w.WriteVec3(jt.tx, jt.ty, jt.tz);      // 12
                    w.WriteVec4(jt.rx, jt.ry, jt.rz, jt.rw); // 16
                    w.WriteVec3(jt.sx, jt.sy, jt.sz);      // 12
                    // No trailing padding in v5 (44 bytes, not 48)
                }
            }

            // Patch header.asset_size
            int finalSize = w.Position;
            w.PatchU64(animBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            w.WriteToFile(outputPath);
            Debug.Log($"[HZAnimationExporter] Exported '{clipName}' → {outputPath} ({finalSize} bytes, {keyframeCount} keyframes, {jointCount} joints)");
        }
    }
}
