using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity Mesh as a ModelBlobAsset (.hza) for hz-engine-v5.
    /// v5 layout: ModelBlobAsset → MeshBlobAsset[] → SubMeshBlobAsset[]
    /// Supports both static and skinned meshes.
    /// </summary>
    public static class HZMeshExporter
    {
        // C struct sizes (must match engine exactly)
        // ModelBlobAsset: header(48) + mat4(64) + meshes(16) + joints(16) + joint_names(16) = 160
        const int SIZEOF_MODEL_BLOB_ASSET = 160;
        // MeshBlobAsset: name(8) + submeshes(16) + blendshape_names(16) = 40
        const int SIZEOF_MESH_BLOB_ASSET = 40;
        // SubMeshBlobAsset: material BlobAssetRef(24) + index_format(4) + index_count(4) + vertex_count(4)
        //   + aabb_min(12) + aabb_max(12) + indices(16) + positions(16) + normals(16)
        //   + tangents(16) + uvs(16) + has_skin(4) + joint_indices(16) + joint_weights(16)
        //   + blendshapes(16) = 192
        const int SIZEOF_SUBMESH_BLOB_ASSET = 192;
        // JointBlobAsset: mat4(64) + parent_index(4) + children(16) = 84
        const int SIZEOF_JOINT_BLOB_ASSET = 84;
        const int SIZEOF_STRING_BLOB = 8;
        // BlendshapeBlobAsset: position_deltas(16) + normal_deltas(16) = 32
        const int SIZEOF_BLENDSHAPE_BLOB_ASSET = 32;
        // MaterialBlobAsset: header(48) + material_shader BlobAssetRef(24) + depth_test(4) + depth_write(4)
        //   + depth_compare(4) + cull_mode(4) + is_transparent(4) + alpha_cutoff(4) + properties(16) = 112
        const int SIZEOF_MATERIAL_BLOB_ASSET = 112;

        /// <summary>Static mesh export (no skinning).</summary>
        public static void Export(Mesh mesh, string materialPath, string outputPath)
        {
            ExportInternal(mesh, materialPath, outputPath, null);
        }

        /// <summary>Skinned mesh export with skeleton data (single SMR).</summary>
        public static void Export(SkinnedMeshRenderer smr, string materialPath, string outputPath)
        {
            var mesh = smr.sharedMesh;
            ExportInternal(mesh, materialPath, outputPath, smr);
        }

        /// <summary>Multi-mesh skinned export. All SMRs share the same skeleton (from primarySmr).</summary>
        public static void ExportMultiSkinned(SkinnedMeshRenderer[] smrs, string[] materialPaths,
            SkinnedMeshRenderer primarySmr, string outputPath)
        {
            // Use primary SMR for skeleton data
            var bones = primarySmr.bones;
            int jointCount = bones.Length;
            var bindPoses = primarySmr.sharedMesh.bindposes;

            // Build hierarchy
            var boneToIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < jointCount; i++)
                boneToIndex[bones[i]] = i;

            int[] parentIndices = new int[jointCount];
            List<int>[] childrenLists = new List<int>[jointCount];
            for (int i = 0; i < jointCount; i++)
                childrenLists[i] = new List<int>();

            for (int i = 0; i < jointCount; i++)
            {
                var parent = bones[i].parent;
                if (parent != null && boneToIndex.TryGetValue(parent, out int pi))
                {
                    parentIndices[i] = pi;
                    childrenLists[pi].Add(i);
                }
                else
                {
                    parentIndices[i] = -1;
                }
            }

            int meshCount = smrs.Length;

            var w = new HZBlobWriter(1024 * 1024 * 4);
            int modelBase = w.Position;

            // === ModelBlobAsset (160 bytes) ===
            w.WriteHeader("ModelBlobAsset");
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob"); // no deps
            w.WriteMat4Identity(); // armature_transform

            int meshesPtrPos = w.ReserveBlobPtr();
            int jointsPtrPos = w.ReserveBlobPtr();
            int jointNamesPtrPos = w.ReserveBlobPtr();

            Debug.Assert(w.Position == SIZEOF_MODEL_BLOB_ASSET,
                $"ModelBlobAsset size mismatch: expected {SIZEOF_MODEL_BLOB_ASSET}, got {w.Position}");

            // === MeshBlobAsset array (one per SMR) ===
            int meshArrayBase = w.Position;
            w.PatchBlobPtr(meshesPtrPos, meshArrayBase, meshCount, SIZEOF_MESH_BLOB_ASSET, "MeshBlobAsset");

            int[] meshNamePositions = new int[meshCount];
            int[] submeshPtrPositions = new int[meshCount];
            int[] blendshapeNamePtrPositions = new int[meshCount];

            for (int mi = 0; mi < meshCount; mi++)
            {
                meshNamePositions[mi] = w.ReserveStringBlob();
                submeshPtrPositions[mi] = w.ReserveBlobPtr();
                blendshapeNamePtrPositions[mi] = w.ReserveBlobPtr();
                PatchEmptyBlobPtr(w, blendshapeNamePtrPositions[mi], SIZEOF_STRING_BLOB, "StringBlob");
            }

            // === SubMeshBlobAsset array (one per MeshBlobAsset, one submesh each) ===
            int[][] subMeshFieldPositions = new int[meshCount][];
            int[] subMeshBases = new int[meshCount];

            for (int mi = 0; mi < meshCount; mi++)
            {
                int subBase = w.Position;
                subMeshBases[mi] = subBase;
                w.PatchBlobPtr(submeshPtrPositions[mi], subBase, 1, SIZEOF_SUBMESH_BLOB_ASSET, "SubMeshBlobAsset");

                // SubMeshBlobAsset fields
                int matPos = w.ReserveBlobAssetRef(); // material
                var mesh = smrs[mi].sharedMesh;
                int vertexCount = mesh.vertexCount;
                int indexCount = mesh.triangles.Length;
                bool useU16 = vertexCount <= 65535;

                w.WriteU32(useU16 ? 0u : 1u);
                w.WriteU32((uint)indexCount);
                w.WriteU32((uint)vertexCount);

                var bounds = mesh.bounds;
                w.WriteVec3(-bounds.max.x, bounds.min.y, bounds.min.z);
                w.WriteVec3(-bounds.min.x, bounds.max.y, bounds.max.z);

                int indicesPtrPos = w.ReserveBlobPtr();
                int positionsPtrPos = w.ReserveBlobPtr();
                int normalsPtrPos = w.ReserveBlobPtr();
                int tangentsPtrPos = w.ReserveBlobPtr();
                int uvsPtrPos = w.ReserveBlobPtr();

                w.WriteU32(1u); // has_skin = true

                int jointIdxPtrPos = w.ReserveBlobPtr();
                int jointWtPtrPos = w.ReserveBlobPtr();
                int blendshapesPtrPos = w.ReserveBlobPtr();
                PatchEmptyBlobPtr(w, blendshapesPtrPos, SIZEOF_BLENDSHAPE_BLOB_ASSET, "BlendshapeBlobAsset");

                Debug.Assert(w.Position == subBase + SIZEOF_SUBMESH_BLOB_ASSET,
                    $"SubMeshBlobAsset size mismatch");

                subMeshFieldPositions[mi] = new int[] {
                    matPos, indicesPtrPos, positionsPtrPos, normalsPtrPos,
                    tangentsPtrPos, uvsPtrPos, jointIdxPtrPos, jointWtPtrPos
                };
            }

            // === JointBlobAsset array ===
            int jointsArrayPos = w.Position;
            w.PatchBlobPtr(jointsPtrPos, jointsArrayPos, jointCount, SIZEOF_JOINT_BLOB_ASSET, "JointBlobAsset");

            int[] jointChildrenPtrPositions = new int[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                WriteMat4Converted(w, bindPoses[i]);
                w.WriteI32(parentIndices[i]);
                jointChildrenPtrPositions[i] = w.ReserveBlobPtr();
            }

            // === Joint names ===
            int jointNamesArrayPos = w.Position;
            w.PatchBlobPtr(jointNamesPtrPos, jointNamesArrayPos, jointCount, SIZEOF_STRING_BLOB, "StringBlob");

            int[] jointNameSBPositions = new int[jointCount];
            for (int i = 0; i < jointCount; i++)
                jointNameSBPositions[i] = w.ReserveStringBlob();

            // === String data (mesh names, joint names) ===
            for (int mi = 0; mi < meshCount; mi++)
                w.WriteAndPatchStringBlob(meshNamePositions[mi], smrs[mi].sharedMesh.name ?? "mesh");

            for (int i = 0; i < jointCount; i++)
                w.WriteAndPatchStringBlob(jointNameSBPositions[i], bones[i].name);

            // === Material paths ===
            for (int mi = 0; mi < meshCount; mi++)
            {
                string mp = materialPaths[mi] ?? "";
                if (!string.IsNullOrEmpty(mp))
                    w.WriteAndPatchBlobAssetRef(subMeshFieldPositions[mi][0], mp,
                        SIZEOF_MATERIAL_BLOB_ASSET, "MaterialBlobAsset");
                else
                    w.PatchEmptyBlobAssetRef(subMeshFieldPositions[mi][0],
                        SIZEOF_MATERIAL_BLOB_ASSET, "MaterialBlobAsset");
            }

            // === Joint children data ===
            for (int i = 0; i < jointCount; i++)
            {
                var children = childrenLists[i];
                if (children.Count > 0)
                {
                    int childDataPos = w.Position;
                    w.PatchBlobPtr(jointChildrenPtrPositions[i], childDataPos, children.Count, 4, "u32");
                    for (int c = 0; c < children.Count; c++)
                        w.WriteU32((uint)children[c]);
                }
                else
                {
                    PatchEmptyBlobPtr(w, jointChildrenPtrPositions[i], 4, "u32");
                }
            }

            // === Align to 16 before vertex data ===
            w.AlignTo(16);

            // === Per-mesh vertex/index data ===
            for (int mi = 0; mi < meshCount; mi++)
            {
                var mesh = smrs[mi].sharedMesh;
                var smr = smrs[mi];
                int vertexCount = mesh.vertexCount;
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var tangents = mesh.tangents;
                var uvs = mesh.uv;
                HZGlbWriter.WarnExtraUVs(mesh);
                var triangles = mesh.triangles;
                int indexCount = triangles.Length;
                bool useU16 = vertexCount <= 65535;

                // Convert vertex data
                float[] positions = new float[vertexCount * 3];
                float[] norms = new float[vertexCount * 3];
                float[] tans = new float[vertexCount * 4];
                float[] uv = new float[vertexCount * 2];

                for (int i = 0; i < vertexCount; i++)
                {
                    positions[i * 3 + 0] = -vertices[i].x;
                    positions[i * 3 + 1] = vertices[i].y;
                    positions[i * 3 + 2] = vertices[i].z;
                    if (i < normals.Length) { norms[i*3+0] = -normals[i].x; norms[i*3+1] = normals[i].y; norms[i*3+2] = normals[i].z; }
                    if (i < tangents.Length) { tans[i*4+0] = -tangents[i].x; tans[i*4+1] = tangents[i].y; tans[i*4+2] = tangents[i].z; tans[i*4+3] = tangents[i].w; }
                    if (i < uvs.Length) { uv[i*2+0] = uvs[i].x; uv[i*2+1] = uvs[i].y; }
                }

                // Flip winding
                int[] indices = new int[indexCount];
                for (int i = 0; i < indexCount; i += 3)
                {
                    indices[i + 0] = triangles[i + 0];
                    indices[i + 1] = triangles[i + 2];
                    indices[i + 2] = triangles[i + 1];
                }

                // Index data
                byte[] indexData;
                int indexTypeSize;
                string indexTypeName;
                if (useU16)
                {
                    indexData = new byte[indexCount * 2];
                    for (int i = 0; i < indexCount; i++)
                    {
                        ushort v = (ushort)indices[i];
                        indexData[i * 2 + 0] = (byte)(v & 0xFF);
                        indexData[i * 2 + 1] = (byte)(v >> 8);
                    }
                    indexTypeSize = 2; indexTypeName = "u16";
                }
                else
                {
                    indexData = new byte[indexCount * 4];
                    for (int i = 0; i < indexCount; i++)
                    {
                        uint v = (uint)indices[i];
                        indexData[i*4+0] = (byte)(v & 0xFF); indexData[i*4+1] = (byte)((v>>8)&0xFF);
                        indexData[i*4+2] = (byte)((v>>16)&0xFF); indexData[i*4+3] = (byte)(v>>24);
                    }
                    indexTypeSize = 4; indexTypeName = "u32";
                }

                // Skinning data — remap bone indices from this SMR's bone array to primary's
                int[] boneRemap = new int[smr.bones.Length];
                for (int bi = 0; bi < smr.bones.Length; bi++)
                {
                    boneRemap[bi] = 0;
                    if (smr.bones[bi] != null && boneToIndex.TryGetValue(smr.bones[bi], out int primaryIdx))
                        boneRemap[bi] = primaryIdx;
                }

                var boneWeights = mesh.boneWeights;
                byte[] jointIndicesData = new byte[vertexCount * 4];
                float[] jointWeightsData = new float[vertexCount * 4];
                for (int i = 0; i < vertexCount; i++)
                {
                    var bw = boneWeights[i];
                    jointIndicesData[i*4+0] = (byte)boneRemap[bw.boneIndex0];
                    jointIndicesData[i*4+1] = (byte)boneRemap[bw.boneIndex1];
                    jointIndicesData[i*4+2] = (byte)boneRemap[bw.boneIndex2];
                    jointIndicesData[i*4+3] = (byte)boneRemap[bw.boneIndex3];
                    jointWeightsData[i*4+0] = bw.weight0; jointWeightsData[i*4+1] = bw.weight1;
                    jointWeightsData[i*4+2] = bw.weight2; jointWeightsData[i*4+3] = bw.weight3;
                }

                int[] fps = subMeshFieldPositions[mi];

                int idxPos = w.Position; w.WriteBytes(indexData);
                w.PatchBlobPtr(fps[1], idxPos, indexData.Length / indexTypeSize, indexTypeSize, indexTypeName);

                int posPos = w.Position; WriteFloatArray(w, positions);
                w.PatchBlobPtr(fps[2], posPos, vertexCount * 3, 4, "f32");

                int normPos = w.Position; WriteFloatArray(w, norms);
                w.PatchBlobPtr(fps[3], normPos, vertexCount * 3, 4, "f32");

                int tanPos = w.Position; WriteFloatArray(w, tans);
                w.PatchBlobPtr(fps[4], tanPos, vertexCount * 4, 4, "f32");

                int uvPos = w.Position; WriteFloatArray(w, uv);
                w.PatchBlobPtr(fps[5], uvPos, vertexCount * 2, 4, "f32");

                int jiPos = w.Position; w.WriteBytes(jointIndicesData);
                w.PatchBlobPtr(fps[6], jiPos, vertexCount * 4, 1, "u8");

                int jwPos = w.Position; WriteFloatArray(w, jointWeightsData);
                w.PatchBlobPtr(fps[7], jwPos, vertexCount * 4, 4, "f32");
            }

            // Patch header.asset_size
            int finalSize = w.Position;
            w.PatchU64(modelBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            w.WriteToFile(outputPath);
            Debug.Log($"[HZMeshExporter] Exported multi-skinned → {outputPath} ({finalSize} bytes, {meshCount} meshes, {jointCount} joints)");
        }

        static void ExportInternal(Mesh mesh, string materialPath, string outputPath, SkinnedMeshRenderer smr)
        {
            bool skinned = smr != null;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uvs = mesh.uv;
            HZGlbWriter.WarnExtraUVs(mesh);
            var triangles = mesh.triangles;
            int vertexCount = mesh.vertexCount;
            int indexCount = triangles.Length;
            bool useU16 = vertexCount <= 65535;
            string meshName = mesh.name ?? "mesh";
            if (materialPath == null) materialPath = "";

            // --- Convert vertex data (negate X for LH→RH) ---
            float[] positions = new float[vertexCount * 3];
            float[] norms = new float[vertexCount * 3];
            float[] tans = new float[vertexCount * 4];
            float[] uv = new float[vertexCount * 2];

            for (int i = 0; i < vertexCount; i++)
            {
                positions[i * 3 + 0] = -vertices[i].x;
                positions[i * 3 + 1] = vertices[i].y;
                positions[i * 3 + 2] = vertices[i].z;

                if (i < normals.Length)
                {
                    norms[i * 3 + 0] = -normals[i].x;
                    norms[i * 3 + 1] = normals[i].y;
                    norms[i * 3 + 2] = normals[i].z;
                }

                if (i < tangents.Length)
                {
                    tans[i * 4 + 0] = -tangents[i].x;
                    tans[i * 4 + 1] = tangents[i].y;
                    tans[i * 4 + 2] = tangents[i].z;
                    tans[i * 4 + 3] = tangents[i].w;
                }

                if (i < uvs.Length)
                {
                    uv[i * 2 + 0] = uvs[i].x;
                    uv[i * 2 + 1] = uvs[i].y;
                }
            }

            // Flip winding
            int[] indices = new int[indexCount];
            for (int i = 0; i < indexCount; i += 3)
            {
                indices[i + 0] = triangles[i + 0];
                indices[i + 1] = triangles[i + 2];
                indices[i + 2] = triangles[i + 1];
            }

            // AABB
            var bounds = mesh.bounds;
            float aabbMinX = -bounds.max.x;
            float aabbMinY = bounds.min.y;
            float aabbMinZ = bounds.min.z;
            float aabbMaxX = -bounds.min.x;
            float aabbMaxY = bounds.max.y;
            float aabbMaxZ = bounds.max.z;

            // Build index data bytes
            byte[] indexData;
            int indexTypeSize;
            string indexTypeName;
            if (useU16)
            {
                indexData = new byte[indexCount * 2];
                for (int i = 0; i < indexCount; i++)
                {
                    ushort v = (ushort)indices[i];
                    indexData[i * 2 + 0] = (byte)(v & 0xFF);
                    indexData[i * 2 + 1] = (byte)(v >> 8);
                }
                indexTypeSize = 2;
                indexTypeName = "u16";
            }
            else
            {
                indexData = new byte[indexCount * 4];
                for (int i = 0; i < indexCount; i++)
                {
                    uint v = (uint)indices[i];
                    indexData[i * 4 + 0] = (byte)(v & 0xFF);
                    indexData[i * 4 + 1] = (byte)((v >> 8) & 0xFF);
                    indexData[i * 4 + 2] = (byte)((v >> 16) & 0xFF);
                    indexData[i * 4 + 3] = (byte)(v >> 24);
                }
                indexTypeSize = 4;
                indexTypeName = "u32";
            }

            // --- Skinning data ---
            Transform[] bones = null;
            int jointCount = 0;
            byte[] jointIndicesData = null;
            float[] jointWeightsData = null;
            Matrix4x4[] bindPoses = null;
            int[] parentIndices = null;
            List<int>[] childrenLists = null;

            if (skinned)
            {
                bones = smr.bones;
                jointCount = bones.Length;
                bindPoses = mesh.bindposes;

                // Bone weights: 4 indices (u8) + 4 weights (f32) per vertex
                var boneWeights = mesh.boneWeights;
                jointIndicesData = new byte[vertexCount * 4];
                jointWeightsData = new float[vertexCount * 4];

                for (int i = 0; i < vertexCount; i++)
                {
                    var bw = boneWeights[i];
                    jointIndicesData[i * 4 + 0] = (byte)bw.boneIndex0;
                    jointIndicesData[i * 4 + 1] = (byte)bw.boneIndex1;
                    jointIndicesData[i * 4 + 2] = (byte)bw.boneIndex2;
                    jointIndicesData[i * 4 + 3] = (byte)bw.boneIndex3;

                    jointWeightsData[i * 4 + 0] = bw.weight0;
                    jointWeightsData[i * 4 + 1] = bw.weight1;
                    jointWeightsData[i * 4 + 2] = bw.weight2;
                    jointWeightsData[i * 4 + 3] = bw.weight3;
                }

                // Build hierarchy: parent index + children list per bone
                var boneToIndex = new Dictionary<Transform, int>();
                for (int i = 0; i < jointCount; i++)
                    boneToIndex[bones[i]] = i;

                parentIndices = new int[jointCount];
                childrenLists = new List<int>[jointCount];
                for (int i = 0; i < jointCount; i++)
                    childrenLists[i] = new List<int>();

                for (int i = 0; i < jointCount; i++)
                {
                    var parent = bones[i].parent;
                    if (parent != null && boneToIndex.TryGetValue(parent, out int pi))
                    {
                        parentIndices[i] = pi;
                        childrenLists[pi].Add(i);
                    }
                    else
                    {
                        parentIndices[i] = -1;
                    }
                }
            }

            // --- Write blob ---
            var w = new HZBlobWriter(1024 * 1024);
            int modelBase = w.Position;

            // === ModelBlobAsset (160 bytes) ===
            // header(48) + mat4(64) + meshes(16) + joints(16) + joint_names(16)
            w.WriteHeader("ModelBlobAsset");
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob"); // no deps

            // armature_transform — identity for Unity exports.
            w.WriteMat4Identity();

            int meshesPtrPos = w.ReserveBlobPtr();     // meshes
            int jointsPtrPos = w.ReserveBlobPtr();     // joints
            int jointNamesPtrPos = w.ReserveBlobPtr(); // joint_names

            Debug.Assert(w.Position == SIZEOF_MODEL_BLOB_ASSET,
                $"ModelBlobAsset size mismatch: expected {SIZEOF_MODEL_BLOB_ASSET}, got {w.Position}");

            // === MeshBlobAsset (40 bytes) — one mesh node ===
            // name(8) + submeshes(16) + blendshape_names(16)
            int meshBase = w.Position;
            w.PatchBlobPtr(meshesPtrPos, meshBase, 1, SIZEOF_MESH_BLOB_ASSET, "MeshBlobAsset");

            int meshNamePos = w.ReserveStringBlob();        // name
            int submeshesPtrPos = w.ReserveBlobPtr();        // submeshes
            int blendshapeNamesPtrPos = w.ReserveBlobPtr();  // blendshape_names (empty)

            PatchEmptyBlobPtr(w, blendshapeNamesPtrPos, SIZEOF_STRING_BLOB, "StringBlob");

            Debug.Assert(w.Position == meshBase + SIZEOF_MESH_BLOB_ASSET,
                $"MeshBlobAsset size mismatch: expected {SIZEOF_MESH_BLOB_ASSET}, got {w.Position - meshBase}");

            // === SubMeshBlobAsset (176 bytes) — one submesh ===
            int subBase = w.Position;
            w.PatchBlobPtr(submeshesPtrPos, subBase, 1, SIZEOF_SUBMESH_BLOB_ASSET, "SubMeshBlobAsset");

            int matPos = w.ReserveBlobAssetRef();  // material (BlobAssetRef)

            w.WriteU32(useU16 ? 0u : 1u);       // index_format
            w.WriteU32((uint)indexCount);         // index_count
            w.WriteU32((uint)vertexCount);        // vertex_count
            w.WriteVec3(aabbMinX, aabbMinY, aabbMinZ);
            w.WriteVec3(aabbMaxX, aabbMaxY, aabbMaxZ);

            int indicesPtrPos = w.ReserveBlobPtr();
            int positionsPtrPos = w.ReserveBlobPtr();
            int normalsPtrPos = w.ReserveBlobPtr();
            int tangentsPtrPos = w.ReserveBlobPtr();
            int uvsPtrPos = w.ReserveBlobPtr();

            w.WriteU32(skinned ? 1u : 0u); // has_skin

            int jointIdxPtrPos = w.ReserveBlobPtr();
            int jointWtPtrPos = w.ReserveBlobPtr();
            int blendshapesPtrPos = w.ReserveBlobPtr(); // blendshapes (empty)

            if (!skinned)
            {
                PatchEmptyBlobPtr(w, jointIdxPtrPos, 1, "u8");
                PatchEmptyBlobPtr(w, jointWtPtrPos, 4, "f32");
            }
            PatchEmptyBlobPtr(w, blendshapesPtrPos, SIZEOF_BLENDSHAPE_BLOB_ASSET, "BlendshapeBlobAsset");

            Debug.Assert(w.Position == subBase + SIZEOF_SUBMESH_BLOB_ASSET,
                $"SubMeshBlobAsset size mismatch: expected {SIZEOF_SUBMESH_BLOB_ASSET}, got {w.Position - subBase}");

            // === JointBlobAsset array (84 bytes each) ===
            int jointsArrayPos = w.Position;
            int[] jointChildrenPtrPositions = null;

            if (skinned)
            {
                w.PatchBlobPtr(jointsPtrPos, jointsArrayPos, jointCount, SIZEOF_JOINT_BLOB_ASSET, "JointBlobAsset");

                jointChildrenPtrPositions = new int[jointCount];
                for (int i = 0; i < jointCount; i++)
                {
                    int jointBase = w.Position;

                    // inverse_bind_matrix (64 bytes) — convert coordinate system
                    WriteMat4Converted(w, bindPoses[i]);

                    // parent_index (4 bytes)
                    w.WriteI32(parentIndices[i]);

                    // children BlobPtr (16 bytes) — reserve, patch later
                    jointChildrenPtrPositions[i] = w.ReserveBlobPtr();

                    // No trailing padding in v5
                    Debug.Assert(w.Position == jointBase + SIZEOF_JOINT_BLOB_ASSET,
                        $"JointBlobAsset[{i}] size mismatch: expected {SIZEOF_JOINT_BLOB_ASSET}, got {w.Position - jointBase}");
                }
            }
            else
            {
                PatchEmptyBlobPtr(w, jointsPtrPos, SIZEOF_JOINT_BLOB_ASSET, "JointBlobAsset");
            }

            // === Joint name StringBlobs (8 bytes each, just the structs) ===
            int jointNamesArrayPos = w.Position;
            int[] jointNameSBPositions = null;

            if (skinned)
            {
                w.PatchBlobPtr(jointNamesPtrPos, jointNamesArrayPos, jointCount, SIZEOF_STRING_BLOB, "StringBlob");

                jointNameSBPositions = new int[jointCount];
                for (int i = 0; i < jointCount; i++)
                    jointNameSBPositions[i] = w.ReserveStringBlob();
            }
            else
            {
                PatchEmptyBlobPtr(w, jointNamesPtrPos, SIZEOF_STRING_BLOB, "StringBlob");
            }

            // === String data (mesh name, material path, joint names) ===
            w.WriteAndPatchStringBlob(meshNamePos, meshName);
            if (!string.IsNullOrEmpty(materialPath))
                w.WriteAndPatchBlobAssetRef(matPos, materialPath, SIZEOF_MATERIAL_BLOB_ASSET, "MaterialBlobAsset");
            else
                w.PatchEmptyBlobAssetRef(matPos, SIZEOF_MATERIAL_BLOB_ASSET, "MaterialBlobAsset");

            if (skinned)
            {
                for (int i = 0; i < jointCount; i++)
                    w.WriteAndPatchStringBlob(jointNameSBPositions[i], bones[i].name);
            }

            // === Joint children data ===
            if (skinned)
            {
                for (int i = 0; i < jointCount; i++)
                {
                    var children = childrenLists[i];
                    if (children.Count > 0)
                    {
                        int childDataPos = w.Position;
                        w.PatchBlobPtr(jointChildrenPtrPositions[i], childDataPos, children.Count, 4, "u32");

                        for (int c = 0; c < children.Count; c++)
                            w.WriteU32((uint)children[c]);
                    }
                    else
                    {
                        PatchEmptyBlobPtr(w, jointChildrenPtrPositions[i], 4, "u32");
                    }
                }
            }

            // === Align to 16 before vertex data ===
            w.AlignTo(16);

            // === Vertex/index data ===
            int indicesDataPos = w.Position;
            w.WriteBytes(indexData);
            w.PatchBlobPtr(indicesPtrPos, indicesDataPos, indexData.Length / indexTypeSize, indexTypeSize, indexTypeName);

            int posDataPos = w.Position;
            WriteFloatArray(w, positions);
            w.PatchBlobPtr(positionsPtrPos, posDataPos, vertexCount * 3, 4, "f32");

            int normDataPos = w.Position;
            WriteFloatArray(w, norms);
            w.PatchBlobPtr(normalsPtrPos, normDataPos, vertexCount * 3, 4, "f32");

            int tanDataPos = w.Position;
            WriteFloatArray(w, tans);
            w.PatchBlobPtr(tangentsPtrPos, tanDataPos, vertexCount * 4, 4, "f32");

            int uvDataPos = w.Position;
            WriteFloatArray(w, uv);
            w.PatchBlobPtr(uvsPtrPos, uvDataPos, vertexCount * 2, 4, "f32");

            // === Skinning vertex data ===
            if (skinned)
            {
                int jiDataPos = w.Position;
                w.WriteBytes(jointIndicesData);
                w.PatchBlobPtr(jointIdxPtrPos, jiDataPos, vertexCount * 4, 1, "u8");

                int jwDataPos = w.Position;
                WriteFloatArray(w, jointWeightsData);
                w.PatchBlobPtr(jointWtPtrPos, jwDataPos, vertexCount * 4, 4, "f32");
            }

            // Patch header.asset_size
            int finalSize = w.Position;
            w.PatchU64(modelBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            w.WriteToFile(outputPath);

            string skinLabel = skinned ? $", skinned with {jointCount} joints" : "";
            Debug.Log($"[HZMeshExporter] Exported '{meshName}' → {outputPath} ({finalSize} bytes, {vertexCount} verts, {indexCount} indices{skinLabel})");
        }

        /// <summary>
        /// Write a mat4 with X-axis coordinate conversion (negate X row and column).
        /// Column-major order matching cglm.
        /// </summary>
        static void WriteMat4Converted(HZBlobWriter w, Matrix4x4 m)
        {
            // Unity Matrix4x4 is column-major: m[row, col]
            // cglm mat4 is column-major: mat4[col][row]
            // Negate X: negate row 0 and col 0, but double-negate [0,0] cancels out
            for (int col = 0; col < 4; col++)
            {
                for (int row = 0; row < 4; row++)
                {
                    float v = m[row, col];
                    bool negateRow = (row == 0);
                    bool negateCol = (col == 0);
                    if (negateRow != negateCol)
                        v = -v;
                    w.WriteF32(v);
                }
            }
        }

        static void PatchEmptyBlobPtr(HZBlobWriter w, int pos, int typeSize, string typeName)
        {
            w.PatchI32(pos, 0);
            w.PatchU32(pos + 4, 0);
            w.PatchU32(pos + 8, (uint)typeSize);
            w.PatchU32(pos + 12, (uint)HZBlobWriter.TypeHash(typeName, typeSize));
        }

        static void WriteFloatArray(HZBlobWriter w, float[] data)
        {
            for (int i = 0; i < data.Length; i++)
                w.WriteF32(data[i]);
        }
    }
}
