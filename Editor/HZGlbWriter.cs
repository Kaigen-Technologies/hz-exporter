using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

//
namespace HZExporter
{
    /// <summary>
    /// Writes glTF 2.0 binary (.glb) files for hz-engine-v5.
    /// Engine auto-cooks .glb to compressed .hza at load time.
    /// </summary>
    public static class HZGlbWriter
    {
        const uint GLB_MAGIC = 0x46546C67;
        const uint GLB_VERSION = 2;
        const uint CHUNK_JSON = 0x4E4F534A;
        const uint CHUNK_BIN = 0x004E4942;

        // glTF component types
        const int BYTE = 5120;
        const int UNSIGNED_BYTE = 5121;
        const int SHORT = 5122;
        const int UNSIGNED_SHORT = 5123;
        const int UNSIGNED_INT = 5125;
        const int FLOAT = 5126;

        // session-scoped dedup: warn once per mesh per editor domain reload so
        // re-exports don't drown the console in identical messages. clears on
        // domain reload (which is when new content would have been imported).
        static readonly HashSet<string> _uvWarnedMeshes = new HashSet<string>();
        public static void WarnExtraUVs(Mesh mesh)
        {
            if (mesh == null || !_uvWarnedMeshes.Add(mesh.name)) return;
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord2))
                Debug.LogError($"[HZGlbWriter] Mesh '{mesh.name}' has UV2 (Unity mesh.uv3) data — dropping; engine vertex layout only carries UV0/UV1.");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord3))
                Debug.LogError($"[HZGlbWriter] Mesh '{mesh.name}' has UV3 (Unity mesh.uv4) data — dropping; engine vertex layout only carries UV0/UV1.");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord4))
                Debug.LogError($"[HZGlbWriter] Mesh '{mesh.name}' has UV4 (Unity mesh.uv5) data — dropping; engine vertex layout only carries UV0/UV1.");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord5))
                Debug.LogError($"[HZGlbWriter] Mesh '{mesh.name}' has UV5 (Unity mesh.uv6) data — dropping; engine vertex layout only carries UV0/UV1.");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord6))
                Debug.LogError($"[HZGlbWriter] Mesh '{mesh.name}' has UV6 (Unity mesh.uv7) data — dropping; engine vertex layout only carries UV0/UV1.");
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord7))
                Debug.LogError($"[HZGlbWriter] Mesh '{mesh.name}' has UV7 (Unity mesh.uv8) data — dropping; engine vertex layout only carries UV0/UV1.");
        }

        // Unity URP `_Cull`: 0=Off (double-sided), 1=Front, 2=Back. Defaults to Back
        // when the material doesn't expose the property.
        static int CullValue(Material m)
        {
            if (m == null) return 2;
            return m.HasProperty("_Cull") ? (int)m.GetFloat("_Cull") : 2;
        }

        // Unity multi-pass: a renderer can carry more materials than the mesh has
        // sub-meshes; the extra materials re-draw the LAST sub-mesh (one extra pass
        // each). The two-sided toon idiom is exactly this — one single-sided front
        // pass (cull back) plus one single-sided back pass (cull front) over the same
        // geometry. glTF can't express cull-front and the engine can't additive-
        // multipass, so when the extra pass is the opposite-cull complement of the
        // base material we collapse the pair into a single double-sided primitive
        // (visually identical when the two materials differ only in cull). The
        // collapsed primitive is emitted under the COMPLEMENT material's own name so
        // it doesn't collide (by name) with the bulk single-sided material in the
        // engine's name-keyed material cook. Returns the complement material to emit
        // double-sided for sub-mesh `s`, or null when `s` is not such a pass.
        static Material TwoSidedComplement(Material[] row, int subCount, int s)
        {
            if (row == null || s != subCount - 1 || row.Length <= subCount) return null;
            int baseCull = CullValue(row.Length > 0 ? row[Math.Min(s, row.Length - 1)] : null);
            if (baseCull == 0) return null;
            for (int k = subCount; k < row.Length; k++)
            {
                int c = CullValue(row[k]);
                if (c != 0 && c != baseCull) return row[k];
            }
            return null;
        }

        /// <summary>Export a single static mesh as .glb. Overload for single-material callers.</summary>
        public static void ExportStaticMesh(Mesh mesh, Material material, string outputPath)
        {
            ExportStaticMesh(mesh, new[] { material }, outputPath);
        }

        /// <summary>
        /// Export a static mesh as .glb. Supports meshes with multiple submeshes — each submesh
        /// becomes a separate glTF primitive sharing the same vertex buffers, with its own
        /// material. `materials[i]` pairs with submesh `i`; if fewer materials are provided than
        /// submeshes, the last material repeats (matching Unity's MeshRenderer behavior).
        /// </summary>
        public static void ExportStaticMesh(Mesh mesh, Material[] materials, string outputPath)
        {
            ExportStaticMesh(mesh, materials, outputPath, grassShader: false);
        }

        /// <summary>
        /// Export a static mesh as .glb. Supports meshes with multiple submeshes — each submesh
        /// becomes a separate glTF primitive sharing the same vertex buffers, with its own
        /// material. `materials[i]` pairs with submesh `i`; if fewer materials are provided than
        /// submeshes, the last material repeats (matching Unity's MeshRenderer behavior).
        ///
        /// When <paramref name="grassShader"/> is true (terrain grass detail meshes), every
        /// custom-shader material's engine shader path is rewritten to its `_grass` variant
        /// (e.g. vegetation.hz.glsl → vegetation_grass.hz.glsl). PBR/default materials are left
        /// untouched (they have no per-material shader in the GLB).
        /// </summary>
        public static void ExportStaticMesh(Mesh mesh, Material[] materials, string outputPath, bool grassShader)
        {
            if (materials == null || materials.Length == 0) materials = new Material[] { null };

            var bin = new MemoryStream();
            var bufferViews = new List<object>();

            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uvs = mesh.uv;
            var uvs1 = mesh.uv2;   // Unity's mesh.uv2 == UV1 / glTF TEXCOORD_1 / engine uvs1
            WarnExtraUVs(mesh);
            var colors = mesh.colors32;  // glTF COLOR_0 — engine fills (1,1,1,1) when absent
            int vertexCount = mesh.vertexCount;
            bool useU16 = vertexCount <= 65535;
            int submeshCount = Math.Max(1, mesh.subMeshCount);

            // --- Per-submesh indices (flip winding: swap [1] and [2]) ---
            var submeshIndexInfo = new (int offset, int size, int count)[submeshCount];
            int totalIndexCount = 0;
            for (int s = 0; s < submeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                {
                    Debug.LogWarning($"[HZGlbWriter] Mesh '{mesh.name}' submesh {s} is not Triangles topology — skipping");
                    submeshIndexInfo[s] = (0, 0, 0);
                    continue;
                }
                var subTris = mesh.GetTriangles(s);
                int indicesOffset = (int)bin.Position;
                if (useU16)
                {
                    for (int i = 0; i < subTris.Length; i += 3)
                    {
                        WriteU16(bin, (ushort)subTris[i + 0]);
                        WriteU16(bin, (ushort)subTris[i + 2]);
                        WriteU16(bin, (ushort)subTris[i + 1]);
                    }
                }
                else
                {
                    for (int i = 0; i < subTris.Length; i += 3)
                    {
                        WriteU32(bin, (uint)subTris[i + 0]);
                        WriteU32(bin, (uint)subTris[i + 2]);
                        WriteU32(bin, (uint)subTris[i + 1]);
                    }
                }
                int indicesSize = (int)bin.Position - indicesOffset;
                PadTo4(bin);
                submeshIndexInfo[s] = (indicesOffset, indicesSize, subTris.Length);
                totalIndexCount += subTris.Length;
            }

            // Positions (negate X for LH→RH)
            int posOffset = (int)bin.Position;
            float[] posMin = { float.MaxValue, float.MaxValue, float.MaxValue };
            float[] posMax = { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < vertexCount; i++)
            {
                float x = -vertices[i].x, y = vertices[i].y, z = vertices[i].z;
                WriteF32(bin, x); WriteF32(bin, y); WriteF32(bin, z);
                posMin[0] = Math.Min(posMin[0], x); posMin[1] = Math.Min(posMin[1], y); posMin[2] = Math.Min(posMin[2], z);
                posMax[0] = Math.Max(posMax[0], x); posMax[1] = Math.Max(posMax[1], y); posMax[2] = Math.Max(posMax[2], z);
            }
            int posSize = (int)bin.Position - posOffset;

            // Normals (negate X)
            int normOffset = (int)bin.Position;
            for (int i = 0; i < vertexCount; i++)
            {
                float x = (i < normals.Length) ? -normals[i].x : 0;
                float y = (i < normals.Length) ? normals[i].y : 0;
                float z = (i < normals.Length) ? normals[i].z : 1;
                WriteF32(bin, x); WriteF32(bin, y); WriteF32(bin, z);
            }
            int normSize = (int)bin.Position - normOffset;

            // Tangents (negate X, keep W)
            int tanOffset = (int)bin.Position;
            bool hasTangents = tangents != null && tangents.Length > 0;
            if (hasTangents)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    float x = (i < tangents.Length) ? -tangents[i].x : 1;
                    float y = (i < tangents.Length) ? tangents[i].y : 0;
                    float z = (i < tangents.Length) ? tangents[i].z : 0;
                    float w = (i < tangents.Length) ? tangents[i].w : 1;
                    WriteF32(bin, x); WriteF32(bin, y); WriteF32(bin, z); WriteF32(bin, w);
                }
            }
            int tanSize = (int)bin.Position - tanOffset;

            // UVs
            int uvOffset = (int)bin.Position;
            bool hasUVs = uvs != null && uvs.Length > 0;
            if (hasUVs)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    float u = (i < uvs.Length) ? uvs[i].x : 0;
                    float v = (i < uvs.Length) ? 1.0f - uvs[i].y : 0;
                    WriteF32(bin, u); WriteF32(bin, v);
                }
            }
            int uvSize = (int)bin.Position - uvOffset;

            // UV1 (Unity mesh.uv2) — defensive: only emit when length matches vertexCount.
            int uv1Offset = (int)bin.Position;
            bool hasUV1 = uvs1 != null && uvs1.Length == vertexCount;
            if (hasUV1)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    float u = uvs1[i].x;
                    float v = 1.0f - uvs1[i].y;
                    WriteF32(bin, u); WriteF32(bin, v);
                }
            }
            int uv1Size = (int)bin.Position - uv1Offset;

            // COLOR_0 — vertex colors, RGBA u8 normalized. Only emit when the
            // mesh actually has per-vertex colors authored; engine path fills
            // (1,1,1,1) by default when the attribute is absent.
            int colorOffset = (int)bin.Position;
            bool hasColors = colors != null && colors.Length == vertexCount;
            if (hasColors)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    bin.WriteByte(colors[i].r);
                    bin.WriteByte(colors[i].g);
                    bin.WriteByte(colors[i].b);
                    bin.WriteByte(colors[i].a);
                }
                // 4-byte stride — already padded by definition (RGBA u8 = 4 bytes per vert).
            }
            int colorSize = (int)bin.Position - colorOffset;

            // --- Embedded textures (per material) ---
            // Materials route either via PBR (shader_mapping.json default) or, when the entry is
            // marked customShader:true, via HZShaderMap.EmbedCustomMaterial — the latter yields a
            // CustomMaterial payload that gets serialized into materials[i].extras.hz_material.
            // role: 0=albedo, 1=normal, 2=orm, 3=emissive, -1=non-PBR (custom-shader uniform).
            var imageEntries = new List<(int off, int size, string name, int matIdx, int role)>();
            var matFactors = new HZShaderMap.Factors[materials.Length];
            var customMats = new HZShaderMap.CustomMaterial[materials.Length];
            // Unity multi-pass: when the renderer carries more materials than the mesh
            // has sub-meshes, the extra (opposite-cull) pass over the last sub-mesh is the
            // two-sided idiom. Collapse it to a double-sided primitive (see ExportSkinnedMesh).
            // The last sub-mesh's primitive is redirected to the complement material so it
            // doesn't collide (by name) with the bulk single-sided material in the cook.
            var matDoubleSided = new bool[materials.Length];
            int twoSidedSubmesh = -1;
            int twoSidedMatIndex = -1;
            {
                Material comp = TwoSidedComplement(materials, submeshCount, submeshCount - 1);
                if (comp != null)
                {
                    for (int k = submeshCount; k < materials.Length; k++)
                        if (materials[k] == comp) { twoSidedMatIndex = k; break; }
                    if (twoSidedMatIndex >= 0)
                    {
                        matDoubleSided[twoSidedMatIndex] = true;
                        twoSidedSubmesh = submeshCount - 1;
                        Debug.Log($"[HZGlbWriter] '{mesh.name}' has a front/back multi-pass material pair (mesh has {submeshCount} sub-mesh(es), {materials.Length} materials) — emitting '{comp.name}' as a single double-sided primitive.");
                    }
                }
            }
            for (int mi = 0; mi < materials.Length; mi++)
            {
                int capturedMi = mi;
                var entry = HZShaderMap.Lookup(materials[mi]);
                if (entry != null && entry.customShader)
                {
                    customMats[mi] = HZShaderMap.EmbedCustomMaterial(materials[mi], false, bin, img =>
                    {
                        imageEntries.Add((img.off, img.size, img.name, capturedMi, img.role));
                        return imageEntries.Count - 1;
                    });
                    // Custom-shader grass meshes use the shader's `_grass` variant
                    // (e.g. vegetation.hz.glsl → vegetation_grass.hz.glsl).
                    if (grassShader && customMats[mi] != null)
                        customMats[mi].shaderPath = GrassShaderVariant(customMats[mi].shaderPath);
                }
                else if (grassShader)
                {
                    // Default/PBR material on a grass mesh: synthesize a grass_terrain
                    // custom material (the grass analogue of the engine's standard_static
                    // default) rather than falling through to the PBR path, which would
                    // leave the shader unspecified → standard_static.
                    customMats[mi] = HZShaderMap.EmbedGrassTerrainMaterial(materials[mi], bin, img =>
                    {
                        imageEntries.Add((img.off, img.size, img.name, capturedMi, img.role));
                        return imageEntries.Count - 1;
                    });
                }
                else
                {
                    matFactors[mi] = HZShaderMap.EmbedMaterial(materials[mi], bin, img =>
                    {
                        imageEntries.Add((img.off, img.size, img.name, capturedMi, img.role));
                    });
                }
            }

            byte[] binData = bin.ToArray();

            // --- Build buffer views and accessors ---
            int bvIdx = 0;

            // BV0..N-1: one indices bufferView per submesh (skip empty/non-triangle submeshes).
            int[] submeshIndicesBV = new int[submeshCount];
            for (int s = 0; s < submeshCount; s++)
            {
                if (submeshIndexInfo[s].size == 0) { submeshIndicesBV[s] = -1; continue; }
                bufferViews.Add(new { buffer = 0, byteOffset = submeshIndexInfo[s].offset, byteLength = submeshIndexInfo[s].size });
                submeshIndicesBV[s] = bvIdx++;
            }

            bufferViews.Add(new { buffer = 0, byteOffset = posOffset, byteLength = posSize, byteStride = 12 });
            int posBV = bvIdx++;

            bufferViews.Add(new { buffer = 0, byteOffset = normOffset, byteLength = normSize, byteStride = 12 });
            int normBV = bvIdx++;

            int tanBV = -1;
            if (hasTangents)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = tanOffset, byteLength = tanSize, byteStride = 16 });
                tanBV = bvIdx++;
            }

            int uvBV = -1;
            if (hasUVs)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = uvOffset, byteLength = uvSize, byteStride = 8 });
                uvBV = bvIdx++;
            }

            int uv1BV = -1;
            if (hasUV1)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = uv1Offset, byteLength = uv1Size, byteStride = 8 });
                uv1BV = bvIdx++;
            }

            int colorBV = -1;
            if (hasColors)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = colorOffset, byteLength = colorSize, byteStride = 4 });
                colorBV = bvIdx++;
            }

            int[] imageBVs = new int[imageEntries.Count];
            for (int ii = 0; ii < imageEntries.Count; ii++)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = imageEntries[ii].off, byteLength = imageEntries[ii].size });
                imageBVs[ii] = bvIdx++;
            }

            // --- Build JSON ---
            var json = new StringBuilder();
            json.Append("{\n");
            json.Append("  \"asset\": { \"version\": \"2.0\", \"generator\": \"HZExporter\" },\n");

            json.Append($"  \"buffers\": [{{ \"byteLength\": {binData.Length} }}],\n");

            // Buffer views
            json.Append("  \"bufferViews\": [\n");
            for (int i = 0; i < bufferViews.Count; i++)
            {
                var bv = bufferViews[i];
                var t = bv.GetType();
                int bo = (int)t.GetProperty("byteOffset").GetValue(bv);
                int bl = (int)t.GetProperty("byteLength").GetValue(bv);
                var bsProp = t.GetProperty("byteStride");
                json.Append($"    {{ \"buffer\": 0, \"byteOffset\": {bo}, \"byteLength\": {bl}");
                if (bsProp != null) json.Append($", \"byteStride\": {bsProp.GetValue(bv)}");
                json.Append(" }");
                if (i < bufferViews.Count - 1) json.Append(",");
                json.Append("\n");
            }
            json.Append("  ],\n");

            // Accessors: per-submesh indices first, then shared pos/normal/tan/uv.
            int accIdx = 0;
            int[] submeshIndicesAcc = new int[submeshCount];
            json.Append("  \"accessors\": [\n");
            for (int s = 0; s < submeshCount; s++)
            {
                if (submeshIndicesBV[s] < 0) { submeshIndicesAcc[s] = -1; continue; }
                json.Append($"    {{ \"bufferView\": {submeshIndicesBV[s]}, \"componentType\": {(useU16 ? UNSIGNED_SHORT : UNSIGNED_INT)}, \"count\": {submeshIndexInfo[s].count}, \"type\": \"SCALAR\" }},\n");
                submeshIndicesAcc[s] = accIdx++;
            }
            // Build remaining accessors into a list so we can comma-join without
            // re-counting which ones are still to come.
            var trailingAcc = new List<string>();
            int posAcc = accIdx++;
            trailingAcc.Add($"    {{ \"bufferView\": {posBV}, \"componentType\": {FLOAT}, \"count\": {vertexCount}, \"type\": \"VEC3\", \"min\": [{F(posMin[0])}, {F(posMin[1])}, {F(posMin[2])}], \"max\": [{F(posMax[0])}, {F(posMax[1])}, {F(posMax[2])}] }}");
            int normAcc = accIdx++;
            trailingAcc.Add($"    {{ \"bufferView\": {normBV}, \"componentType\": {FLOAT}, \"count\": {vertexCount}, \"type\": \"VEC3\" }}");
            int tanAcc = -1;
            if (hasTangents)
            {
                tanAcc = accIdx++;
                trailingAcc.Add($"    {{ \"bufferView\": {tanBV}, \"componentType\": {FLOAT}, \"count\": {vertexCount}, \"type\": \"VEC4\" }}");
            }
            int uvAcc = -1;
            if (hasUVs)
            {
                uvAcc = accIdx++;
                trailingAcc.Add($"    {{ \"bufferView\": {uvBV}, \"componentType\": {FLOAT}, \"count\": {vertexCount}, \"type\": \"VEC2\" }}");
            }
            int uv1Acc = -1;
            if (hasUV1)
            {
                uv1Acc = accIdx++;
                trailingAcc.Add($"    {{ \"bufferView\": {uv1BV}, \"componentType\": {FLOAT}, \"count\": {vertexCount}, \"type\": \"VEC2\" }}");
            }
            int colorAcc = -1;
            if (hasColors)
            {
                colorAcc = accIdx++;
                trailingAcc.Add($"    {{ \"bufferView\": {colorBV}, \"componentType\": {UNSIGNED_BYTE}, \"normalized\": true, \"count\": {vertexCount}, \"type\": \"VEC4\" }}");
            }
            json.Append(string.Join(",\n", trailingAcc));
            json.Append("\n  ],\n");

            // Images + textures
            if (imageEntries.Count > 0)
            {
                json.Append("  \"images\": [\n");
                for (int ii = 0; ii < imageEntries.Count; ii++)
                {
                    if (ii > 0) json.Append(",\n");
                    json.Append($"    {{ \"bufferView\": {imageBVs[ii]}, \"mimeType\": \"image/png\", \"name\": \"{imageEntries[ii].name}\" }}");
                }
                json.Append("\n  ],\n");
                json.Append("  \"textures\": [\n");
                for (int ii = 0; ii < imageEntries.Count; ii++)
                {
                    if (ii > 0) json.Append(",\n");
                    json.Append($"    {{ \"source\": {ii} }}");
                }
                json.Append("\n  ],\n");
            }

            // Per-material texture index map: [matIdx] → [albedo, normal, orm, emissive]
            var matTexMap = new Dictionary<int, int[]>();
            for (int ii = 0; ii < imageEntries.Count; ii++)
            {
                int mi = imageEntries[ii].matIdx;
                if (!matTexMap.ContainsKey(mi)) matTexMap[mi] = new int[] { -1, -1, -1, -1 };
                int role = imageEntries[ii].role;
                if (role >= 0 && role < 4) matTexMap[mi][role] = ii;
            }

            // Materials — factors + slotted texture refs (from HZShaderMap).
            json.Append("  \"materials\": [\n");
            for (int mi = 0; mi < materials.Length; mi++)
            {
                if (mi > 0) json.Append(",\n");
                string matName = SanitizeName(materials[mi]?.name ?? $"material_{mi}");
                // Grass meshes get a distinct material name so the cooked material
                // (and any references to it) are identifiable as the grass variant,
                // matching the `_grass` GLB/shader naming.
                if (grassShader) matName += "_grass";
                if (customMats[mi] != null)
                {
                    WriteCustomMaterialJson(json, matName, customMats[mi]);
                }
                else
                {
                    var f = matFactors[mi];
                    int[] ti = matTexMap.ContainsKey(mi) ? matTexMap[mi] : new int[] { -1, -1, -1, -1 };
                    json.Append($"    {{\n      \"name\": \"{EscapeJson(matName)}\",\n      \"pbrMetallicRoughness\": {{\n");
                    json.Append($"        \"baseColorFactor\": [{F(f.baseColor.r)}, {F(f.baseColor.g)}, {F(f.baseColor.b)}, {F(f.baseColor.a)}]");
                    if (ti[0] >= 0) json.Append($",\n        \"baseColorTexture\": {{ \"index\": {ti[0]} }}");
                    if (ti[2] >= 0) json.Append($",\n        \"metallicRoughnessTexture\": {{ \"index\": {ti[2]} }}");
                    json.Append($",\n        \"metallicFactor\": {F(f.metallic)}");
                    json.Append($",\n        \"roughnessFactor\": {F(f.roughness)}");
                    json.Append("\n      }");
                    if (ti[1] >= 0) json.Append($",\n      \"normalTexture\": {{ \"index\": {ti[1]} }}");
                    if (ti[2] >= 0) json.Append($",\n      \"occlusionTexture\": {{ \"index\": {ti[2]} }}");
                    if (f.emissive.r > 0 || f.emissive.g > 0 || f.emissive.b > 0)
                        json.Append($",\n      \"emissiveFactor\": [{F(f.emissive.r)}, {F(f.emissive.g)}, {F(f.emissive.b)}]");
                    if (ti[3] >= 0) json.Append($",\n      \"emissiveTexture\": {{ \"index\": {ti[3]} }}");
                    if (matDoubleSided[mi]) json.Append(",\n      \"doubleSided\": true");
                    json.Append("\n    }");
                }
            }
            json.Append("\n  ],\n");

            // Meshes — one primitive per submesh, sharing the vertex attributes.
            // Submeshes past the materials[] list clamp to the last material (Unity behavior).
            json.Append("  \"meshes\": [{\n");
            json.Append($"    \"name\": \"{EscapeJson(mesh.name ?? "mesh")}\",\n");
            json.Append("    \"primitives\": [\n");
            bool firstPrim = true;
            for (int s = 0; s < submeshCount; s++)
            {
                if (submeshIndicesAcc[s] < 0) continue;
                if (!firstPrim) json.Append(",\n");
                firstPrim = false;
                int matRef = (s == twoSidedSubmesh && twoSidedMatIndex >= 0)
                    ? twoSidedMatIndex
                    : Math.Min(s, materials.Length - 1);
                json.Append("      {\n");
                json.Append($"        \"indices\": {submeshIndicesAcc[s]},\n");
                json.Append($"        \"attributes\": {{ \"POSITION\": {posAcc}, \"NORMAL\": {normAcc}");
                if (tanAcc >= 0) json.Append($", \"TANGENT\": {tanAcc}");
                if (uvAcc >= 0) json.Append($", \"TEXCOORD_0\": {uvAcc}");
                if (uv1Acc >= 0) json.Append($", \"TEXCOORD_1\": {uv1Acc}");
                if (colorAcc >= 0) json.Append($", \"COLOR_0\": {colorAcc}");
                json.Append(" },\n");
                json.Append($"        \"material\": {matRef}\n");
                json.Append("      }");
            }
            json.Append("\n    ]\n  }],\n");

            // Nodes
            json.Append("  \"nodes\": [{ \"mesh\": 0 }],\n");

            // Scene
            json.Append("  \"scene\": 0,\n");
            json.Append("  \"scenes\": [{ \"nodes\": [0] }]\n");
            json.Append("}");

            // --- Write GLB ---
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json.ToString());
            // Pad JSON to 4 bytes with spaces
            int jsonPadded = (jsonBytes.Length + 3) & ~3;
            byte[] jsonChunk = new byte[jsonPadded];
            Array.Copy(jsonBytes, jsonChunk, jsonBytes.Length);
            for (int i = jsonBytes.Length; i < jsonPadded; i++) jsonChunk[i] = 0x20;

            // Pad BIN to 4 bytes with zeros
            int binPadded = (binData.Length + 3) & ~3;
            byte[] binChunk = new byte[binPadded];
            Array.Copy(binData, binChunk, binData.Length);

            uint totalLength = 12 + 8 + (uint)jsonChunk.Length + 8 + (uint)binChunk.Length;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (var fs = new FileStream(outputPath, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                // Header
                w.Write(GLB_MAGIC);
                w.Write(GLB_VERSION);
                w.Write(totalLength);
                // JSON chunk
                w.Write((uint)jsonChunk.Length);
                w.Write(CHUNK_JSON);
                w.Write(jsonChunk);
                // BIN chunk
                w.Write((uint)binChunk.Length);
                w.Write(CHUNK_BIN);
                w.Write(binChunk);
            }

            Debug.Log($"[HZGlbWriter] Exported → {outputPath} ({totalLength} bytes, {vertexCount} verts, {totalIndexCount} indices, {submeshCount} submesh(es), {materials.Length} mat(s))");
        }

        /// <summary>Animation clip info for GLB export</summary>
        public struct AnimClipInfo
        {
            public AnimationClip clip;
            public string name;
            public GameObject skeletonRoot;
        }

        /// <summary>Export multi-mesh skinned model as .glb with skeleton and optional animations.
        /// `entityRoot` is the GameObject that becomes the entity in the engine (typically the
        /// Animator-owning GO). It anchors the bone hierarchy: any root bone whose Unity parent
        /// is outside `bones[]` (e.g. an attached sub-character whose root bone hangs off the
        /// outer character's skeleton) has the chain above it baked into its rest TRS so the
        /// engine places it at the correct bind-pose location relative to the entity.</summary>
        public static void ExportSkinnedMesh(SkinnedMeshRenderer[] smrs, Material[][] materialsPerSmr,
            SkinnedMeshRenderer primarySmr, Transform entityRoot, string outputPath,
            AnimClipInfo[] animClips = null)
        {
            // Flatten per-SMR material arrays into a single materials[] (one entry per
            // (smr, submesh) pair). matIndexPerSmr[smrIdx][submeshIdx] = index into flat materials[].
            // Per-submesh because a single SMR with multiple sub-meshes has one material per sub-mesh.
            var flatMatList = new List<Material>();
            var flatDoubleSided = new List<bool>();
            int[][] matIndexPerSmr = new int[smrs.Length][];
            for (int si = 0; si < smrs.Length; si++)
            {
                var row = (materialsPerSmr != null && si < materialsPerSmr.Length && materialsPerSmr[si] != null)
                    ? materialsPerSmr[si] : new Material[] { null };
                int subCount = Math.Max(1, smrs[si].sharedMesh != null ? smrs[si].sharedMesh.subMeshCount : 1);
                matIndexPerSmr[si] = new int[subCount];
                for (int s = 0; s < subCount; s++)
                {
                    Material m = (row.Length > 0) ? row[Math.Min(s, row.Length - 1)] : null;
                    Material comp = TwoSidedComplement(row, subCount, s);
                    bool ds = comp != null;
                    if (ds)
                    {
                        m = comp;
                        Debug.Log($"[HZGlbWriter] '{smrs[si].sharedMesh?.name}' has a front/back multi-pass material pair (mesh has {subCount} sub-mesh(es), {row.Length} materials) — emitting '{comp.name}' as a single double-sided primitive.");
                    }
                    matIndexPerSmr[si][s] = flatMatList.Count;
                    flatMatList.Add(m);
                    flatDoubleSided.Add(ds);
                }
            }
            var materials = flatMatList.ToArray();
            var matDoubleSided = flatDoubleSided.ToArray();

            var bones = primarySmr.bones;
            int jointCount = bones.Length;
            var bindPoses = primarySmr.sharedMesh.bindposes;

            // bones[] may contain null slots (e.g. MagicaCloth2 deletes leaf _end terminators
            // that the SMR still references by index). Skin weights never point at these slots,
            // so we keep the indexing intact and emit identity placeholders for the nulls.
            var boneToIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < jointCount; i++)
                if (bones[i] != null) boneToIndex[bones[i]] = i;

            int[] parentIndices = new int[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                if (bones[i] == null) { parentIndices[i] = -1; continue; }
                var parent = bones[i].parent;
                parentIndices[i] = (parent != null && boneToIndex.TryGetValue(parent, out int pi)) ? pi : -1;
            }

            var bin = new MemoryStream();
            var json = new StringBuilder();

            // Track buffer views and accessors as we go
            var bvList = new List<string>();  // JSON fragments for bufferViews
            var accList = new List<string>(); // JSON fragments for accessors

            int AddBV(int offset, int length, int stride = 0)
            {
                int idx = bvList.Count;
                string s = $"    {{ \"buffer\": 0, \"byteOffset\": {offset}, \"byteLength\": {length}";
                if (stride > 0) s += $", \"byteStride\": {stride}";
                s += " }";
                bvList.Add(s);
                return idx;
            }

            int AddAcc(int bv, int compType, int count, string type, float[] min = null, float[] max = null, bool normalized = false)
            {
                int idx = accList.Count;
                string s = $"    {{ \"bufferView\": {bv}, \"componentType\": {compType}, \"count\": {count}, \"type\": \"{type}\"";
                if (normalized) s += ", \"normalized\": true";
                if (min != null && max != null)
                {
                    s += ", \"min\": [" + string.Join(", ", Array.ConvertAll(min, v => F(v))) + "]";
                    s += ", \"max\": [" + string.Join(", ", Array.ConvertAll(max, v => F(v))) + "]";
                }
                s += " }";
                accList.Add(s);
                return idx;
            }

            // --- Write per-mesh primitive data ---
            int meshCount = smrs.Length;
            // Per SMR: shared vertex accessors (pos/normal/tan/uv/uv1/joints/weights) +
            // one indices accessor per sub-mesh (multi-material SMRs have N sub-meshes).
            // sharedAccs[mi] = [posAcc, normAcc, tanAcc, uvAcc, jointsAcc, weightsAcc, uv1Acc]
            // submeshIndicesAccs[mi][s] = index accessor for sub-mesh s of SMR mi.
            int[][] sharedAccs = new int[meshCount][];
            int[][] submeshIndicesAccs = new int[meshCount][];

            for (int mi = 0; mi < meshCount; mi++)
            {
                var smr = smrs[mi];
                var mesh = smr.sharedMesh;
                int vertexCount = mesh.vertexCount;
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var tangents = mesh.tangents;
                var uvs = mesh.uv;
                var uvs1 = mesh.uv2;   // UV1 / glTF TEXCOORD_1 / engine uvs1
                WarnExtraUVs(mesh);
                var colors = mesh.colors32;  // glTF COLOR_0
                int submeshCount = Math.Max(1, mesh.subMeshCount);
                bool useU16 = vertexCount <= 65535;

                // Build bone remap for this SMR
                int[] boneRemap = new int[smr.bones.Length];
                for (int bi = 0; bi < smr.bones.Length; bi++)
                    boneRemap[bi] = (smr.bones[bi] != null && boneToIndex.TryGetValue(smr.bones[bi], out int pi)) ? pi : 0;

                // Each SMR has its own mesh.bindposes, but glTF stores ONE inverseBindMatrices list
                // per skin (we emit primary's). If this SMR's bindposes differ from primary's, its
                // vertices must be pre-transformed so primary's IBM applies correctly.
                // Math: primary_bp · v' = smr_bp · v  →  v' = primary_bp^-1 · smr_bp · v, which is
                // bone-independent because both bindposes factor as (bone.worldToLocal · M_bind).
                // IMPORTANT: derive from bindposes directly, NOT from current Transform values — if
                // the SMR GameObject was moved after authoring (common with cloth/proxy setups), the
                // current Transform no longer reflects bind-time state, but bindposes still do.
                Matrix4x4 preXform = Matrix4x4.identity;
                if (smr != primarySmr && bindPoses.Length > 0 && mesh.bindposes.Length > 0)
                {
                    // Find a bone index where both bindposes reference a live Transform (safest).
                    int refIdx = 0;
                    for (int bi = 0; bi < System.Math.Min(bindPoses.Length, mesh.bindposes.Length); bi++)
                    {
                        if (bi < primarySmr.bones.Length && bi < smr.bones.Length
                            && primarySmr.bones[bi] != null && smr.bones[bi] != null)
                        { refIdx = bi; break; }
                    }
                    preXform = bindPoses[refIdx].inverse * mesh.bindposes[refIdx];
                }
                Matrix4x4 preXformNormal = preXform.inverse.transpose; // for normals/tangents (no translation)

                // Indices — one buffer view + accessor per sub-mesh (winding flip swap [1]/[2]).
                // Submeshes share the SMR's vertex attributes; only the index range differs.
                var smrSubmeshInfo = new (int off, int size, int count)[submeshCount];
                for (int s = 0; s < submeshCount; s++)
                {
                    int[] tri = mesh.GetTriangles(s);
                    int triLen = tri.Length;
                    int subOff = (int)bin.Position;
                    for (int i = 0; i < triLen; i += 3)
                    {
                        if (useU16) { WriteU16(bin, (ushort)tri[i]); WriteU16(bin, (ushort)tri[i+2]); WriteU16(bin, (ushort)tri[i+1]); }
                        else        { WriteU32(bin, (uint)tri[i]);   WriteU32(bin, (uint)tri[i+2]);   WriteU32(bin, (uint)tri[i+1]);   }
                    }
                    smrSubmeshInfo[s] = (subOff, (int)bin.Position - subOff, triLen);
                }
                PadTo4(bin);

                // Positions
                int posOff = (int)bin.Position;
                float[] posMin = { float.MaxValue, float.MaxValue, float.MaxValue };
                float[] posMax = { float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3 p = preXform.MultiplyPoint3x4(vertices[i]);
                    float x = -p.x, y = p.y, z = p.z;
                    WriteF32(bin, x); WriteF32(bin, y); WriteF32(bin, z);
                    posMin[0] = Math.Min(posMin[0], x); posMin[1] = Math.Min(posMin[1], y); posMin[2] = Math.Min(posMin[2], z);
                    posMax[0] = Math.Max(posMax[0], x); posMax[1] = Math.Max(posMax[1], y); posMax[2] = Math.Max(posMax[2], z);
                }
                int posSize = (int)bin.Position - posOff;

                // Normals
                int normOff = (int)bin.Position;
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3 n = (i < normals.Length)
                        ? preXformNormal.MultiplyVector(normals[i]).normalized
                        : new Vector3(0, 0, 1);
                    float nx = -n.x, ny = n.y, nz = n.z;
                    WriteF32(bin, nx); WriteF32(bin, ny); WriteF32(bin, nz);
                }
                int normSize = (int)bin.Position - normOff;

                // Tangents (xyz is a direction, w is handedness sign — don't transform w)
                int tanOff = (int)bin.Position;
                bool hasTan = tangents != null && tangents.Length > 0;
                if (hasTan)
                    for (int i = 0; i < vertexCount; i++)
                    {
                        Vector4 t4 = (i < tangents.Length) ? tangents[i] : new Vector4(1, 0, 0, 1);
                        Vector3 td = preXformNormal.MultiplyVector(new Vector3(t4.x, t4.y, t4.z)).normalized;
                        WriteF32(bin, -td.x);
                        WriteF32(bin, td.y);
                        WriteF32(bin, td.z);
                        WriteF32(bin, t4.w);
                    }
                int tanSize = (int)bin.Position - tanOff;

                // UVs
                int uvOff = (int)bin.Position;
                bool hasUV = uvs != null && uvs.Length > 0;
                if (hasUV)
                    for (int i = 0; i < vertexCount; i++)
                    {
                        WriteF32(bin, (i < uvs.Length) ? uvs[i].x : 0);
                        WriteF32(bin, (i < uvs.Length) ? 1.0f - uvs[i].y : 0);
                    }
                int uvSize = (int)bin.Position - uvOff;

                // UV1 (Unity mesh.uv2) — defensive: only emit when length matches vertexCount.
                int uv1Off = (int)bin.Position;
                bool hasUV1 = uvs1 != null && uvs1.Length == vertexCount;
                if (hasUV1)
                    for (int i = 0; i < vertexCount; i++)
                    {
                        WriteF32(bin, uvs1[i].x);
                        WriteF32(bin, 1.0f - uvs1[i].y);
                    }
                int uv1Size = (int)bin.Position - uv1Off;

                // Vertex colors (RGBA u8 normalized) — only emit when authored.
                int colorOff = (int)bin.Position;
                bool hasColors = colors != null && colors.Length == vertexCount;
                if (hasColors)
                    for (int i = 0; i < vertexCount; i++)
                    {
                        bin.WriteByte(colors[i].r);
                        bin.WriteByte(colors[i].g);
                        bin.WriteByte(colors[i].b);
                        bin.WriteByte(colors[i].a);
                    }
                int colorSize = (int)bin.Position - colorOff;

                // Joint indices: u8 when skeleton has ≤256 joints, else u16 (glTF spec allows both).
                // Unity meshes with many bones (e.g. characters with dynamic bone chains) blow past 255
                // and a (byte) cast silently wraps, mis-skinning every vertex weighted to a high-index bone.
                bool jointsU16 = jointCount > 255;
                int jiOff = (int)bin.Position;
                var boneWeights = mesh.boneWeights;
                for (int i = 0; i < vertexCount; i++)
                {
                    var bw = boneWeights[i];
                    int j0 = boneRemap[bw.boneIndex0];
                    int j1 = boneRemap[bw.boneIndex1];
                    int j2 = boneRemap[bw.boneIndex2];
                    int j3 = boneRemap[bw.boneIndex3];
                    if (jointsU16)
                    {
                        WriteU16(bin, (ushort)j0); WriteU16(bin, (ushort)j1);
                        WriteU16(bin, (ushort)j2); WriteU16(bin, (ushort)j3);
                    }
                    else
                    {
                        bin.WriteByte((byte)j0); bin.WriteByte((byte)j1);
                        bin.WriteByte((byte)j2); bin.WriteByte((byte)j3);
                    }
                }
                int jiSize = (int)bin.Position - jiOff;

                // Joint weights (f32 x 4)
                int jwOff = (int)bin.Position;
                for (int i = 0; i < vertexCount; i++)
                {
                    var bw = boneWeights[i];
                    WriteF32(bin, bw.weight0); WriteF32(bin, bw.weight1);
                    WriteF32(bin, bw.weight2); WriteF32(bin, bw.weight3);
                }
                int jwSize = (int)bin.Position - jwOff;

                // Register buffer views and accessors — per-submesh indices, shared vertex attrs.
                int posBV = AddBV(posOff, posSize, 12);
                int normBV = AddBV(normOff, normSize, 12);
                int tanBV = hasTan ? AddBV(tanOff, tanSize, 16) : -1;
                int uvBV = hasUV ? AddBV(uvOff, uvSize, 8) : -1;
                int uv1BV = hasUV1 ? AddBV(uv1Off, uv1Size, 8) : -1;
                int colorBV = hasColors ? AddBV(colorOff, colorSize, 4) : -1;
                int jiBV = AddBV(jiOff, jiSize, jointsU16 ? 8 : 4);
                int jwBV = AddBV(jwOff, jwSize, 16);

                int posAcc = AddAcc(posBV, FLOAT, vertexCount, "VEC3", posMin, posMax);
                int normAcc = AddAcc(normBV, FLOAT, vertexCount, "VEC3");
                int tanAcc = hasTan ? AddAcc(tanBV, FLOAT, vertexCount, "VEC4") : -1;
                int uvAcc = hasUV ? AddAcc(uvBV, FLOAT, vertexCount, "VEC2") : -1;
                int uv1Acc = hasUV1 ? AddAcc(uv1BV, FLOAT, vertexCount, "VEC2") : -1;
                int colorAcc = hasColors ? AddAcc(colorBV, UNSIGNED_BYTE, vertexCount, "VEC4", null, null, normalized: true) : -1;
                int jiAcc = AddAcc(jiBV, jointsU16 ? UNSIGNED_SHORT : UNSIGNED_BYTE, vertexCount, "VEC4");
                int jwAcc = AddAcc(jwBV, FLOAT, vertexCount, "VEC4");

                int[] subAccs = new int[submeshCount];
                for (int s = 0; s < submeshCount; s++)
                {
                    int subBV = AddBV(smrSubmeshInfo[s].off, smrSubmeshInfo[s].size);
                    subAccs[s] = AddAcc(subBV, useU16 ? UNSIGNED_SHORT : UNSIGNED_INT, smrSubmeshInfo[s].count, "SCALAR");
                }

                sharedAccs[mi] = new int[] { posAcc, normAcc, tanAcc, uvAcc, jiAcc, jwAcc, uv1Acc, colorAcc };
                submeshIndicesAccs[mi] = subAccs;
            }

            // --- Inverse bind matrices ---
            PadTo4(bin);
            int ibmOff = (int)bin.Position;
            for (int i = 0; i < jointCount; i++)
            {
                // Convert bind pose: negate X row and X column
                var m = bindPoses[i];
                for (int col = 0; col < 4; col++)
                    for (int row = 0; row < 4; row++)
                    {
                        float v = m[row, col];
                        bool negRow = (row == 0), negCol = (col == 0);
                        if (negRow != negCol) v = -v;
                        WriteF32(bin, v);
                    }
            }
            int ibmSize = (int)bin.Position - ibmOff;
            int ibmBV = AddBV(ibmOff, ibmSize);
            int ibmAcc = AddAcc(ibmBV, FLOAT, jointCount, "MAT4");

            // --- Force T-pose and save rest pose before animation baking ---
            // Disable all Animators to reset skeleton to import pose
            var animators = primarySmr.GetComponentsInParent<Animator>(true);
            foreach (var a in animators) a.enabled = false;
            // Also check the root's animators
            if (primarySmr.rootBone != null)
            {
                foreach (var a in primarySmr.rootBone.root.GetComponentsInChildren<Animator>(true))
                    a.enabled = false;
            }
            // Force Unity to update transforms with Animator disabled (T-pose)
            primarySmr.transform.root.gameObject.SetActive(false);
            primarySmr.transform.root.gameObject.SetActive(true);

            var restPositions = new Vector3[jointCount];
            var restRotations = new Quaternion[jointCount];
            var restScales = new Vector3[jointCount];
            // Build set of joint Transforms once, for "is this bone's parent another joint?" lookups.
            var bonesSetStatic = new HashSet<Transform>();
            for (int i = 0; i < jointCount; i++)
                if (bones[i] != null) bonesSetStatic.Add(bones[i]);
            for (int i = 0; i < jointCount; i++)
            {
                if (bones[i] == null)
                {
                    restPositions[i] = Vector3.zero;
                    restRotations[i] = Quaternion.identity;
                    restScales[i] = Vector3.one;
                    continue;
                }
                var bp = bones[i].parent;
                if (bp != null && bonesSetStatic.Contains(bp))
                {
                    restPositions[i] = bones[i].localPosition;
                    restRotations[i] = bones[i].localRotation;
                    restScales[i] = bones[i].localScale;
                }
                else
                {
                    // Root joint: bake the chain above (outer-character bones, prefab wrappers,
                    // etc.) so the rest TRS is relative to entityRoot, matching the GLB joint
                    // hierarchy convention.
                    Matrix4x4 m = entityRoot.worldToLocalMatrix * bones[i].localToWorldMatrix;
                    Vector4 c3 = m.GetColumn(3);
                    restPositions[i] = new Vector3(c3.x, c3.y, c3.z);
                    restRotations[i] = m.rotation;
                    restScales[i] = m.lossyScale;
                }
            }
            // Re-enable animators (needed for SampleAnimation during baking)
            foreach (var a in animators) a.enabled = true;
            if (primarySmr.rootBone != null)
            {
                foreach (var a in primarySmr.rootBone.root.GetComponentsInChildren<Animator>(true))
                    a.enabled = true;
            }

            // --- Bake animations ---
            const float SAMPLE_RATE = 30f;
            // Each anim: { name, timestampAcc, channelData[] }
            // channelData: { nodeIdx, translationAcc, rotationAcc, scaleAcc }
            var animDatas = new List<(string name, int timestampAcc, List<(int node, int tAcc, int rAcc, int sAcc)> channels)>();

            if (animClips != null && animClips.Length > 0)
            {
                foreach (var animInfo in animClips)
                {
                    var clip = animInfo.clip;
                    var animRoot = animInfo.skeletonRoot;
                    float duration = clip.length;
                    int keyframeCount = Mathf.Max(1, Mathf.RoundToInt(duration * SAMPLE_RATE) + 1);

                    // Use bones[] directly — they ARE the scene transforms.
                    // SampleAnimation poses them, so we read local TRS from each bone[i].
                    // This avoids the duplicate-name problem (e.g. two "leftCalf" bones).
                    var animBones = new List<(Transform t, int jointIndex)>();
                    for (int bi = 0; bi < bones.Length; bi++)
                        if (bones[bi] != null)
                            animBones.Add((bones[bi], bi));

                    if (animBones.Count == 0)
                    {
                        Debug.LogWarning($"[HZGlbWriter] No bones for animation '{clip.name}', skipping");
                        continue;
                    }

                    // Build set of Transforms that are joints in bones[]. For any bone whose
                    // parent IS in this set, the sampled local TRS is correct as-is. For a
                    // root bone (parent NOT in set — typically Armature with a non-identity
                    // rotation above the skeleton), we bake the chain above it into the bone's
                    // sample by composing animRoot.worldToLocal × bone.localToWorldMatrix.
                    // Bind pose IBMs are derived from bone.localToWorldMatrix already, so the
                    // animated TRS lines up with the rest pose — no extra correction needed.
                    var boneSet = new HashSet<Transform>();
                    foreach (var ab in animBones) boneSet.Add(ab.t);

                    // Write timestamps
                    PadTo4(bin);
                    int tsOff = (int)bin.Position;
                    for (int ki = 0; ki < keyframeCount; ki++)
                    {
                        float time = (ki < keyframeCount - 1) ? ki / SAMPLE_RATE : duration;
                        WriteF32(bin, time);
                    }
                    int tsBV = AddBV(tsOff, keyframeCount * 4);
                    int tsAcc = AddAcc(tsBV, FLOAT, keyframeCount, "SCALAR",
                        new float[] { 0 }, new float[] { duration });

                    // Sample and write per-bone TRS data
                    // First, sample all keyframes
                    var sampledT = new Vector3[keyframeCount][];
                    var sampledR = new Quaternion[keyframeCount][];
                    var sampledS = new Vector3[keyframeCount][];

                    for (int ki = 0; ki < keyframeCount; ki++)
                    {
                        float time = (ki < keyframeCount - 1) ? ki / SAMPLE_RATE : duration;
                        clip.SampleAnimation(animRoot, time);

                        sampledT[ki] = new Vector3[animBones.Count];
                        sampledR[ki] = new Quaternion[animBones.Count];
                        sampledS[ki] = new Vector3[animBones.Count];

                        for (int bi = 0; bi < animBones.Count; bi++)
                        {
                            var bt = animBones[bi].t;
                            if (bt.parent != null && boneSet.Contains(bt.parent))
                            {
                                sampledT[ki][bi] = bt.localPosition;
                                sampledR[ki][bi] = bt.localRotation;
                                sampledS[ki][bi] = bt.localScale;
                            }
                            else
                            {
                                // Root joint: bake parent chain (Armature, etc.) into the sample.
                                Matrix4x4 m = animRoot.transform.worldToLocalMatrix * bt.localToWorldMatrix;
                                Vector4 c3 = m.GetColumn(3);
                                sampledT[ki][bi] = new Vector3(c3.x, c3.y, c3.z);
                                sampledR[ki][bi] = m.rotation;
                                sampledS[ki][bi] = m.lossyScale;
                            }
                        }
                    }

                    // Write per-bone accessors
                    var channels = new List<(int node, int tAcc, int rAcc, int sAcc)>();
                    for (int bi = 0; bi < animBones.Count; bi++)
                    {
                        int nodeIdx = animBones[bi].jointIndex; // glTF node index = bone index

                        // Translation (vec3)
                        PadTo4(bin);
                        int tOff = (int)bin.Position;
                        for (int ki = 0; ki < keyframeCount; ki++)
                        {
                            var p = sampledT[ki][bi];
                            WriteF32(bin, -p.x); WriteF32(bin, p.y); WriteF32(bin, p.z);
                        }
                        int tBV = AddBV(tOff, keyframeCount * 12);
                        int tAcc2 = AddAcc(tBV, FLOAT, keyframeCount, "VEC3");

                        // Rotation (vec4 quaternion)
                        PadTo4(bin);
                        int rOff = (int)bin.Position;
                        for (int ki = 0; ki < keyframeCount; ki++)
                        {
                            var q = sampledR[ki][bi];
                            WriteF32(bin, q.x); WriteF32(bin, -q.y); WriteF32(bin, -q.z); WriteF32(bin, q.w);
                        }
                        int rBV = AddBV(rOff, keyframeCount * 16);
                        int rAcc2 = AddAcc(rBV, FLOAT, keyframeCount, "VEC4");

                        // Scale (vec3)
                        PadTo4(bin);
                        int sOff = (int)bin.Position;
                        for (int ki = 0; ki < keyframeCount; ki++)
                        {
                            var s = sampledS[ki][bi];
                            WriteF32(bin, s.x); WriteF32(bin, s.y); WriteF32(bin, s.z);
                        }
                        int sBV = AddBV(sOff, keyframeCount * 12);
                        int sAcc2 = AddAcc(sBV, FLOAT, keyframeCount, "VEC3");

                        channels.Add((nodeIdx, tAcc2, rAcc2, sAcc2));
                    }

                    animDatas.Add((animInfo.name, tsAcc, channels));
                    Debug.Log($"[HZGlbWriter] Baked animation '{animInfo.name}': {keyframeCount} keyframes, {animBones.Count} bones");
                }
            }

            // --- Embedded images (per material) via HZShaderMap ---
            // PBR materials route through EmbedMaterial; customShader:true entries route through
            // EmbedCustomMaterial which yields a payload for materials[i].extras.hz_material.
            var skinnedImageEntries = new List<(int bv, int matIdx, string name, int role)>();
            var skinnedFactors = new HZShaderMap.Factors[materials.Length];
            var skinnedCustomMats = new HZShaderMap.CustomMaterial[materials.Length];
            for (int mi = 0; mi < materials.Length; mi++)
            {
                int capturedMi = mi;
                var entry = HZShaderMap.Lookup(materials[mi]);
                if (entry != null && entry.customShader)
                {
                    skinnedCustomMats[mi] = HZShaderMap.EmbedCustomMaterial(materials[mi], true, bin, img =>
                    {
                        skinnedImageEntries.Add((AddBV(img.off, img.size), capturedMi, img.name, img.role));
                        return skinnedImageEntries.Count - 1;
                    });
                }
                else
                {
                    skinnedFactors[mi] = HZShaderMap.EmbedMaterial(materials[mi], bin, img =>
                    {
                        skinnedImageEntries.Add((AddBV(img.off, img.size), capturedMi, img.name, img.role));
                    });
                }
            }

            byte[] binData = bin.ToArray();

            // --- Build JSON ---
            json.Append("{\n");
            json.Append("  \"asset\": { \"version\": \"2.0\", \"generator\": \"HZExporter\" },\n");

            // Buffers
            json.Append($"  \"buffers\": [{{ \"byteLength\": {binData.Length} }}],\n");

            // Buffer views
            json.Append("  \"bufferViews\": [\n");
            json.Append(string.Join(",\n", bvList));
            json.Append("\n  ],\n");

            // Accessors
            json.Append("  \"accessors\": [\n");
            json.Append(string.Join(",\n", accList));
            json.Append("\n  ],\n");

            // Images + textures + materials
            if (skinnedImageEntries.Count > 0)
            {
                json.Append("  \"images\": [\n");
                for (int i = 0; i < skinnedImageEntries.Count; i++)
                {
                    if (i > 0) json.Append(",\n");
                    json.Append($"    {{ \"bufferView\": {skinnedImageEntries[i].bv}, \"mimeType\": \"image/png\", \"name\": \"{skinnedImageEntries[i].name}\" }}");
                }
                json.Append("\n  ],\n");

                json.Append("  \"textures\": [\n");
                for (int i = 0; i < skinnedImageEntries.Count; i++)
                {
                    if (i > 0) json.Append(",\n");
                    json.Append($"    {{ \"source\": {i} }}");
                }
                json.Append("\n  ],\n");
            }

            // Build per-material texture index lookup: [matIdx] → {albedo, normal, orm, emissive}
            var matTexMap = new Dictionary<int, int[]>();
            for (int i = 0; i < skinnedImageEntries.Count; i++)
            {
                int mi2 = skinnedImageEntries[i].matIdx;
                if (!matTexMap.ContainsKey(mi2)) matTexMap[mi2] = new int[] { -1, -1, -1, -1 };
                int r = skinnedImageEntries[i].role;
                if (r >= 0 && r < 4) matTexMap[mi2][r] = i;
            }

            json.Append("  \"materials\": [\n");
            for (int mi2 = 0; mi2 < materials.Length; mi2++)
            {
                if (mi2 > 0) json.Append(",\n");
                // Skinned materials get a `_skinned` suffix so the cooked material is identifiable
                // as the skinned variant (mirrors the `_grass` naming for grass meshes).
                string smatName = SanitizeName(materials[mi2]?.name ?? $"material_{mi2}") + "_skinned";
                if (skinnedCustomMats[mi2] != null)
                {
                    WriteCustomMaterialJson(json, smatName, skinnedCustomMats[mi2]);
                }
                else
                {
                    var f = skinnedFactors[mi2];
                    int[] ti = matTexMap.ContainsKey(mi2) ? matTexMap[mi2] : new int[] { -1, -1, -1, -1 };

                    json.Append($"    {{\n      \"name\": \"{EscapeJson(smatName)}\",\n      \"pbrMetallicRoughness\": {{\n");
                    json.Append($"        \"baseColorFactor\": [{F(f.baseColor.r)}, {F(f.baseColor.g)}, {F(f.baseColor.b)}, {F(f.baseColor.a)}]");
                    if (ti[0] >= 0) json.Append($",\n        \"baseColorTexture\": {{ \"index\": {ti[0]} }}");
                    if (ti[2] >= 0) json.Append($",\n        \"metallicRoughnessTexture\": {{ \"index\": {ti[2]} }}");
                    json.Append($",\n        \"metallicFactor\": {F(f.metallic)}");
                    json.Append($",\n        \"roughnessFactor\": {F(f.roughness)}");
                    json.Append("\n      }");
                    if (ti[1] >= 0) json.Append($",\n      \"normalTexture\": {{ \"index\": {ti[1]} }}");
                    if (ti[2] >= 0) json.Append($",\n      \"occlusionTexture\": {{ \"index\": {ti[2]} }}");
                    if (f.emissive.r > 0 || f.emissive.g > 0 || f.emissive.b > 0)
                        json.Append($",\n      \"emissiveFactor\": [{F(f.emissive.r)}, {F(f.emissive.g)}, {F(f.emissive.b)}]");
                    if (ti[3] >= 0) json.Append($",\n      \"emissiveTexture\": {{ \"index\": {ti[3]} }}");
                    if (matDoubleSided[mi2]) json.Append(",\n      \"doubleSided\": true");
                    json.Append("\n    }");
                }
            }
            json.Append("\n  ],\n");

            // Meshes
            json.Append("  \"meshes\": [\n");
            for (int mi = 0; mi < meshCount; mi++)
            {
                if (mi > 0) json.Append(",\n");
                var acc = sharedAccs[mi];           // [posAcc, normAcc, tanAcc, uvAcc, jointsAcc, weightsAcc, uv1Acc, colorAcc]
                int[] subAccs = submeshIndicesAccs[mi];
                int[] matRow = matIndexPerSmr[mi];
                string meshName = smrs[mi].sharedMesh.name ?? $"mesh_{mi}";
                json.Append($"    {{ \"name\": \"{EscapeJson(meshName)}\", \"primitives\": [");
                for (int s = 0; s < subAccs.Length; s++)
                {
                    if (s > 0) json.Append(", ");
                    json.Append($"{{ \"indices\": {subAccs[s]}, \"attributes\": {{ \"POSITION\": {acc[0]}, \"NORMAL\": {acc[1]}");
                    if (acc[2] >= 0) json.Append($", \"TANGENT\": {acc[2]}");
                    if (acc[3] >= 0) json.Append($", \"TEXCOORD_0\": {acc[3]}");
                    if (acc[6] >= 0) json.Append($", \"TEXCOORD_1\": {acc[6]}");
                    if (acc[7] >= 0) json.Append($", \"COLOR_0\": {acc[7]}");
                    json.Append($", \"JOINTS_0\": {acc[4]}, \"WEIGHTS_0\": {acc[5]}");
                    int matIdx = matRow[Math.Min(s, matRow.Length - 1)];
                    json.Append($" }}, \"material\": {matIdx} }}");
                }
                json.Append("] }");
            }
            json.Append("\n  ],\n");

            // Nodes: skeleton bones + mesh nodes
            // Node 0..jointCount-1 = bones, jointCount..jointCount+meshCount-1 = mesh nodes
            json.Append("  \"nodes\": [\n");
            for (int i = 0; i < jointCount; i++)
            {
                var bone = bones[i];
                // Use saved rest pose (not live transforms which may be corrupted by animation sampling)
                var lp = restPositions[i];
                var lr = restRotations[i];
                var ls = restScales[i];
                float px = -lp.x, py = lp.y, pz = lp.z;
                float qx = lr.x, qy = -lr.y, qz = -lr.z, qw = lr.w;

                string nodeName = bone != null ? bone.name : $"__null_bone_{i}";
                json.Append($"    {{ \"name\": \"{EscapeJson(nodeName)}\"");
                json.Append($", \"translation\": [{F(px)}, {F(py)}, {F(pz)}]");
                json.Append($", \"rotation\": [{F(qx)}, {F(qy)}, {F(qz)}, {F(qw)}]");
                json.Append($", \"scale\": [{F(ls.x)}, {F(ls.y)}, {F(ls.z)}]");

                // Children (other bones that have this as parent)
                var children = new List<int>();
                for (int c = 0; c < jointCount; c++)
                    if (parentIndices[c] == i) children.Add(c);
                if (children.Count > 0)
                    json.Append($", \"children\": [{string.Join(", ", children)}]");

                json.Append(" },\n");
            }

            // Mesh nodes
            for (int mi = 0; mi < meshCount; mi++)
            {
                int nodeIdx = jointCount + mi;
                json.Append($"    {{ \"mesh\": {mi}, \"skin\": 0 }}");
                if (mi < meshCount - 1) json.Append(",");
                json.Append("\n");
            }
            json.Append("  ],\n");

            // Skins
            json.Append("  \"skins\": [{\n");
            json.Append($"    \"inverseBindMatrices\": {ibmAcc},\n");
            json.Append("    \"joints\": [");
            for (int i = 0; i < jointCount; i++)
            {
                if (i > 0) json.Append(", ");
                json.Append(i);
            }
            json.Append("]\n  }],\n");

            // Animations
            if (animDatas.Count > 0)
            {
                json.Append("  \"animations\": [\n");
                for (int ai = 0; ai < animDatas.Count; ai++)
                {
                    var (animName, tsAcc2, channels2) = animDatas[ai];
                    if (ai > 0) json.Append(",\n");
                    json.Append($"    {{ \"name\": \"{EscapeJson(animName)}\",\n");

                    // Samplers: 3 per bone (T, R, S)
                    json.Append("      \"samplers\": [\n");
                    for (int ci = 0; ci < channels2.Count; ci++)
                    {
                        var (node, tA, rA, sA) = channels2[ci];
                        if (ci > 0) json.Append(",\n");
                        json.Append($"        {{ \"input\": {tsAcc2}, \"output\": {tA}, \"interpolation\": \"LINEAR\" }},\n");
                        json.Append($"        {{ \"input\": {tsAcc2}, \"output\": {rA}, \"interpolation\": \"LINEAR\" }},\n");
                        json.Append($"        {{ \"input\": {tsAcc2}, \"output\": {sA}, \"interpolation\": \"LINEAR\" }}");
                    }
                    json.Append("\n      ],\n");

                    // Channels: 3 per bone (translation, rotation, scale)
                    json.Append("      \"channels\": [\n");
                    for (int ci = 0; ci < channels2.Count; ci++)
                    {
                        var (node, tA, rA, sA) = channels2[ci];
                        int samplerBase = ci * 3;
                        if (ci > 0) json.Append(",\n");
                        json.Append($"        {{ \"sampler\": {samplerBase}, \"target\": {{ \"node\": {node}, \"path\": \"translation\" }} }},\n");
                        json.Append($"        {{ \"sampler\": {samplerBase + 1}, \"target\": {{ \"node\": {node}, \"path\": \"rotation\" }} }},\n");
                        json.Append($"        {{ \"sampler\": {samplerBase + 2}, \"target\": {{ \"node\": {node}, \"path\": \"scale\" }} }}");
                    }
                    json.Append("\n      ]\n");
                    json.Append("    }");
                }
                json.Append("\n  ],\n");
            }

            // Scene
            var sceneNodes = new List<int>();
            // Root bones (no parent)
            for (int i = 0; i < jointCount; i++)
                if (parentIndices[i] == -1) sceneNodes.Add(i);
            // Mesh nodes
            for (int mi = 0; mi < meshCount; mi++)
                sceneNodes.Add(jointCount + mi);

            json.Append("  \"scene\": 0,\n");
            json.Append($"  \"scenes\": [{{ \"nodes\": [{string.Join(", ", sceneNodes)}] }}]\n");
            json.Append("}");

            // --- Write GLB ---
            WriteGlb(json.ToString(), binData, outputPath);

            int totalVerts = 0;
            foreach (var s in smrs) totalVerts += s.sharedMesh.vertexCount;
            Debug.Log($"[HZGlbWriter] Exported skinned → {outputPath} ({meshCount} meshes, {jointCount} joints, {totalVerts} verts)");
        }

        static void WriteGlb(string jsonStr, byte[] binData, string outputPath)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonStr);
            int jsonPadded = (jsonBytes.Length + 3) & ~3;
            byte[] jsonChunk = new byte[jsonPadded];
            Array.Copy(jsonBytes, jsonChunk, jsonBytes.Length);
            for (int i = jsonBytes.Length; i < jsonPadded; i++) jsonChunk[i] = 0x20;

            int binPadded = (binData.Length + 3) & ~3;
            byte[] binChunk = new byte[binPadded];
            Array.Copy(binData, binChunk, binData.Length);

            uint totalLength = 12 + 8 + (uint)jsonChunk.Length + 8 + (uint)binChunk.Length;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (var fs = new FileStream(outputPath, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(GLB_MAGIC);
                w.Write(GLB_VERSION);
                w.Write(totalLength);
                w.Write((uint)jsonChunk.Length);
                w.Write(CHUNK_JSON);
                w.Write(jsonChunk);
                w.Write((uint)binChunk.Length);
                w.Write(CHUNK_BIN);
                w.Write(binChunk);
            }
        }

        // Emit a glTF materials[] entry that carries a stub pbrMetallicRoughness (so dumb glTF
        // viewers still render *something*) plus an `extras.hz_material` block whose JSON shape
        // mirrors the engine's MaterialBlobAsset .hzt. The engine's GLB cooker reads this block
        // when present and emits a custom-shader MaterialBlobAsset; otherwise it falls back to
        // the standard PBR-extraction path.
        // Rewrite an engine shader path to its grass variant by inserting `_grass` before the
        // `.hz.glsl` extension: "/shaders/unity/vegetation.hz.glsl" → ".../vegetation_grass.hz.glsl".
        // Leaves an already-`_grass` path or an unrecognized extension untouched.
        static string GrassShaderVariant(string shaderPath)
        {
            if (string.IsNullOrEmpty(shaderPath)) return shaderPath;
            const string ext = ".hz.glsl";
            if (!shaderPath.EndsWith(ext, StringComparison.Ordinal)) return shaderPath;
            string stem = shaderPath.Substring(0, shaderPath.Length - ext.Length);
            if (stem.EndsWith("_grass", StringComparison.Ordinal)) return shaderPath;
            return stem + "_grass" + ext;
        }

        static void WriteCustomMaterialJson(StringBuilder json, string matName, HZShaderMap.CustomMaterial cm)
        {
            json.Append($"    {{\n      \"name\": \"{EscapeJson(matName)}\",\n");
            json.Append("      \"pbrMetallicRoughness\": { \"baseColorFactor\": [1, 1, 1, 1], \"metallicFactor\": 0, \"roughnessFactor\": 1 },\n");
            json.Append("      \"extras\": {\n        \"hz_material\": {\n");
            json.Append($"          \"material_shader\": \"{EscapeJson(cm.shaderPath ?? "")}\",\n");
            json.Append("          \"depth_test\": true,\n");
            json.Append($"          \"depth_write\": {(cm.isTransparent ? "false" : "true")},\n");
            json.Append("          \"depth_compare\": \"GPU_COMPARE_GREATER_EQUAL\",\n");
            json.Append($"          \"cull_mode\": \"{cm.cullMode ?? "GPU_CULL_BACK"}\",\n");
            json.Append($"          \"is_transparent\": {(cm.isTransparent ? "true" : "false")},\n");
            json.Append("          \"alpha_cutoff\": false,\n");
            // Emit explicit blend block when shader_mapping.json specifies one;
            // otherwise the cooker defaults from is_transparent (alpha-blend
            // for true, disabled for false). Emitting only when set keeps the
            // door open for the cooker default and avoids hardcoding policy
            // here.
            if (cm.blend != null)
            {
                json.Append("          \"blend\": {\n");
                json.Append($"            \"enabled\": {(cm.blend.enabled ? "true" : "false")},\n");
                json.Append($"            \"src_factor\": \"GPU_BLEND_{cm.blend.src_factor ?? "SRC_ALPHA"}\",\n");
                json.Append($"            \"dst_factor\": \"GPU_BLEND_{cm.blend.dst_factor ?? "ONE_MINUS_SRC_ALPHA"}\",\n");
                json.Append($"            \"op\": \"GPU_BLEND_OP_{cm.blend.op ?? "ADD"}\",\n");
                json.Append($"            \"src_factor_alpha\": \"GPU_BLEND_{cm.blend.src_factor_alpha ?? "ONE"}\",\n");
                json.Append($"            \"dst_factor_alpha\": \"GPU_BLEND_{cm.blend.dst_factor_alpha ?? "ONE_MINUS_SRC_ALPHA"}\",\n");
                json.Append($"            \"op_alpha\": \"GPU_BLEND_OP_{cm.blend.op_alpha ?? "ADD"}\"\n");
                json.Append("          },\n");
            }
            json.Append("          \"properties\": [\n");
            for (int i = 0; i < cm.properties.Count; i++)
            {
                var p = cm.properties[i];
                if (i > 0) json.Append(",\n");
                json.Append("            { \"name\": \"" + EscapeJson(p.slot ?? "") + "\", ");
                switch (p.kind)
                {
                    case "float":
                        json.Append("\"type\": \"MAT_PROP_FLOAT\", \"f\": " + F(p.f) + " }");
                        break;
                    case "vec2":
                        json.Append("\"type\": \"MAT_PROP_VEC2\", \"v2\": [" + F(p.v2.x) + ", " + F(p.v2.y) + "] }");
                        break;
                    case "vec3":
                        json.Append("\"type\": \"MAT_PROP_VEC3\", \"v3\": [" + F(p.v3.x) + ", " + F(p.v3.y) + ", " + F(p.v3.z) + "] }");
                        break;
                    case "color4":
                        json.Append("\"type\": \"MAT_PROP_COLOR4\", \"c4\": [" + F(p.c4.r) + ", " + F(p.c4.g) + ", " + F(p.c4.b) + ", " + F(p.c4.a) + "] }");
                        break;
                    case "texture":
                        json.Append("\"type\": \"MAT_PROP_TEXTURE\", \"texture_index\": " + p.textureIndex + " }");
                        break;
                    default:
                        json.Append("\"type\": \"MAT_PROP_UNKNOWN\" }");
                        break;
                }
            }
            json.Append("\n          ]");
            // Engine-side MaterialBlobAsset.keywords. Always emit the array
            // (possibly empty) so the cooker sees a deterministic shape; an
            // empty array maps to BlobArray.len = 0 with no variant selection
            // pressure.
            json.Append(",\n          \"keywords\": [");
            if (cm.keywords != null && cm.keywords.Count > 0)
            {
                json.Append("\n");
                for (int i = 0; i < cm.keywords.Count; i++)
                {
                    var k = cm.keywords[i];
                    if (i > 0) json.Append(",\n");
                    json.Append("            { \"name\": \"" + EscapeJson(k.name ?? "") +
                                "\", \"value\": \"" + EscapeJson(k.value ?? "") + "\" }");
                }
                json.Append("\n          ");
            }
            json.Append("]");
            // Optional per-material shadow caster. Only emit when the
            // shader_mapping.json entry declared a shadowShader; absence is
            // the signal to the engine to use its global standard_shadow.
            if (!string.IsNullOrEmpty(cm.shadowShaderPath))
            {
                json.Append(",\n          \"shadow_shader\": \"" + EscapeJson(cm.shadowShaderPath) + "\"");
                json.Append(",\n          \"shadow_keywords\": [");
                if (cm.shadowKeywords != null && cm.shadowKeywords.Count > 0)
                {
                    json.Append("\n");
                    for (int i = 0; i < cm.shadowKeywords.Count; i++)
                    {
                        var k = cm.shadowKeywords[i];
                        if (i > 0) json.Append(",\n");
                        json.Append("            { \"name\": \"" + EscapeJson(k.name ?? "") +
                                    "\", \"value\": \"" + EscapeJson(k.value ?? "") + "\" }");
                    }
                    json.Append("\n          ");
                }
                json.Append("]");
            }
            json.Append("\n");
            json.Append("        }\n      }\n    }");
        }

        static string F(float v) => v.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);

        static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        internal static string SanitizeName(string name) =>
            (name ?? "").Replace(" ", "_").Replace("/", "_").Replace("\\", "_");

        static void WriteU16(MemoryStream s, ushort v) { s.WriteByte((byte)(v & 0xFF)); s.WriteByte((byte)(v >> 8)); }
        static void WriteU32(MemoryStream s, uint v) { var b = BitConverter.GetBytes(v); s.Write(b, 0, 4); }
        static void WriteF32(MemoryStream s, float v) { var b = BitConverter.GetBytes(v); s.Write(b, 0, 4); }

        internal static void PadTo4(MemoryStream s)
        {
            int r = (int)(s.Position % 4);
            if (r != 0) for (int i = 0; i < 4 - r; i++) s.WriteByte(0);
        }

        // ============= Per-export-session PNG cache =============
        //
        // Texture2D → encoded PNG bytes. Hot on scenes with many GLBs (each LOD
        // re-exports the same source texture), where re-running MakeReadable +
        // EncodeToPNG per GLB dominated the export wall time.
        //
        // Owned by ExportScene: call ResetTextureCache() at the start of each
        // export so the cache doesn't outlive the run. Keyed by the source
        // texture's instanceID + an isNormal flag (DXT5nm vs straight blit
        // produce different bytes for the same texture asset).

        struct TexCacheKey : IEquatable<TexCacheKey>
        {
            public EntityId instanceId;
            public bool isNormal;
            public bool Equals(TexCacheKey o) => instanceId == o.instanceId && isNormal == o.isNormal;
            public override bool Equals(object o) => o is TexCacheKey k && Equals(k);
            public override int GetHashCode() => instanceId.GetHashCode() ^ (isNormal ? 1 : 0);
        }
        static readonly Dictionary<TexCacheKey, byte[]> s_pngCache = new Dictionary<TexCacheKey, byte[]>();

        struct OrmCacheKey : IEquatable<OrmCacheKey>
        {
            public EntityId metalSmoothId; // default when null
            public EntityId occlusionId;   // default when null
            public bool Equals(OrmCacheKey o) => metalSmoothId == o.metalSmoothId && occlusionId == o.occlusionId;
            public override bool Equals(object o) => o is OrmCacheKey k && Equals(k);
            public override int GetHashCode() => metalSmoothId.GetHashCode() * 31 + occlusionId.GetHashCode();
        }
        static readonly Dictionary<OrmCacheKey, byte[]> s_ormPngCache = new Dictionary<OrmCacheKey, byte[]>();

        public static void ResetTextureCache()
        {
            s_pngCache.Clear();
            s_ormPngCache.Clear();
        }

        // Returns PNG bytes for the given texture asset, computing+caching on first touch.
        internal static byte[] EncodeAndCachePng(Texture2D src, bool isNormal)
        {
            var key = new TexCacheKey { instanceId = src.GetEntityId(), isNormal = isNormal };
            if (s_pngCache.TryGetValue(key, out var cached)) return cached;

            var readable = MakeReadable(src, isNormal);
            byte[] png = readable.EncodeToPNG();
            if (readable != src) UnityEngine.Object.DestroyImmediate(readable);
            s_pngCache[key] = png;
            return png;
        }

        internal static byte[] EncodeAndCacheOrmPng(Texture2D metallicGloss, Texture2D occlusion)
        {
            var key = new OrmCacheKey {
                metalSmoothId = metallicGloss != null ? metallicGloss.GetEntityId() : default,
                occlusionId = occlusion != null ? occlusion.GetEntityId() : default,
            };
            if (s_ormPngCache.TryGetValue(key, out var cached)) return cached;

            var orm = MakeORM(metallicGloss, occlusion);
            if (orm == null) return null;
            byte[] png = orm.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(orm);
            s_ormPngCache[key] = png;
            return png;
        }

        internal static Texture2D MakeReadable(Texture2D src, bool isNormalMap = false)
        {
            if (isNormalMap)
                return MakeReadableNormalMap(src);
            if (src.isReadable) return src;
            RenderTexture tmp = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, tmp);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = tmp;
            Texture2D readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            return readable;
        }

        /// <summary>
        /// Unity stores normal maps as DXT5nm (X in alpha, Y in green).
        /// This unpacks to standard RGB normal map (X, Y, Z) for glTF.
        /// </summary>
        /// <summary>
        /// Pack ORM texture: R=Occlusion, G=Roughness(1-smoothness), B=Metallic.
        /// metallicGlossTex: Unity _MetallicGlossMap (R=metallic, A=smoothness). Can be null.
        /// occlusionTex: Unity _OcclusionMap (R=occlusion). Can be null.
        /// </summary>
        internal static Texture2D MakeORM(Texture2D metallicGlossTex, Texture2D occlusionTex)
        {
            // Determine size from whichever texture exists
            Texture2D sizeSrc = metallicGlossTex != null ? metallicGlossTex : occlusionTex;
            if (sizeSrc == null) return null;
            int w = sizeSrc.width, h = sizeSrc.height;

            Color[] mgPixels = null;
            if (metallicGlossTex != null)
            {
                var readable = BlitReadLinear(metallicGlossTex);
                mgPixels = readable.GetPixels();
                if (readable != metallicGlossTex) UnityEngine.Object.DestroyImmediate(readable);
            }

            Color[] aoPixels = null;
            if (occlusionTex != null)
            {
                // Resize occlusion to match if needed
                var readable = BlitReadLinear(occlusionTex);
                if (readable.width != w || readable.height != h)
                {
                    var resized = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                    RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    Graphics.Blit(readable, rt);
                    RenderTexture.active = rt;
                    resized.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    resized.Apply();
                    RenderTexture.active = null;
                    RenderTexture.ReleaseTemporary(rt);
                    if (readable != occlusionTex) UnityEngine.Object.DestroyImmediate(readable);
                    readable = resized;
                }
                aoPixels = readable.GetPixels();
                if (readable != occlusionTex) UnityEngine.Object.DestroyImmediate(readable);
            }

            int count = w * h;
            var ormPixels = new Color[count];
            for (int i = 0; i < count; i++)
            {
                float occlusion = aoPixels != null ? aoPixels[i].r : 1f;
                float metallic = mgPixels != null ? mgPixels[i].r : 0f;
                float smoothness = mgPixels != null ? mgPixels[i].a : 0.5f;
                float roughness = 1f - smoothness;
                ormPixels[i] = new Color(occlusion, roughness, metallic, 1f);
            }

            var result = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            result.SetPixels(ormPixels);
            result.Apply();
            return result;
        }

        static Texture2D BlitReadLinear(Texture2D src)
        {
            if (src.isReadable) return src;
            RenderTexture tmp = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(src, tmp);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = tmp;
            Texture2D readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
            readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            return readable;
        }

        static Texture2D MakeReadableNormalMap(Texture2D src)
        {
            // Unity imports normal maps as BC5 (X in red) OR DXT5nm (X in alpha) depending on
            // platform/format. There is NO single GPU decode that handles both robustly — that's
            // exactly why reading the imported texture (plain blit or UnpackNormal) produces garbage
            // like meanRGB=(1,0,1). The only format-independent source of truth is the authored PNG
            // on disk, which is always a straight RGB normal. So we read that.
            //
            // The catch: the on-disk PNG is at FULL native resolution and ignores the importer's
            // Max Size. To respect Max Size we resize the source down to the imported dims
            // (src.width/height — Unity has already clamped these to Max Size) before unpacking.
            int outW = src.width, outH = src.height;
            string path = UnityEditor.AssetDatabase.GetAssetPath(src);
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                Texture2D fromDisk = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                if (fromDisk.LoadImage(bytes))
                {
                    // Resize the raw RGB source down to the Max-Size-clamped dims with a linear blit
                    // (bilinear filtering of the authored channel values) when needed.
                    Texture2D sized = fromDisk;
                    if (fromDisk.width != outW || fromDisk.height != outH)
                    {
                        RenderTexture rt = RenderTexture.GetTemporary(outW, outH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                        Graphics.Blit(fromDisk, rt);
                        RenderTexture prevRt = RenderTexture.active;
                        RenderTexture.active = rt;
                        sized = new Texture2D(outW, outH, TextureFormat.RGBA32, false, true);
                        sized.ReadPixels(new Rect(0, 0, outW, outH), 0, 0);
                        sized.Apply();
                        RenderTexture.active = prevRt;
                        RenderTexture.ReleaseTemporary(rt);
                    }

                    // Source PNGs are authored in DirectX tangent-space (+Y down). The engine's TBN
                    // math uses OpenGL convention (+Y up), so flip Y and recompute Z from X/Y.
                    Color[] pixels = sized.GetPixels();
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        float nx = pixels[i].r * 2f - 1f;
                        float ny = -(pixels[i].g * 2f - 1f);
                        float nz = Mathf.Sqrt(Mathf.Max(0f, 1f - nx * nx - ny * ny));
                        pixels[i] = new Color(nx * 0.5f + 0.5f, ny * 0.5f + 0.5f, nz * 0.5f + 0.5f, 1f);
                    }
                    Texture2D unpacked = new Texture2D(outW, outH, TextureFormat.RGBA32, false, true);
                    unpacked.SetPixels(pixels);
                    unpacked.Apply();
                    if (sized != fromDisk) UnityEngine.Object.DestroyImmediate(sized);
                    UnityEngine.Object.DestroyImmediate(fromDisk);
                    return unpacked;
                }
                UnityEngine.Object.DestroyImmediate(fromDisk);
            }
            Debug.LogWarning($"[HZGlbWriter] normal-map source file unreadable for '{src.name}' — falling back to imported (alpha-packed X may be lost)");
            // Fallback path (kept for in-memory textures with no source asset).
            RenderTexture tmp = RenderTexture.GetTemporary(outW, outH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(src, tmp);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = tmp;
            Texture2D packed = new Texture2D(outW, outH, TextureFormat.RGBA32, false, true);
            packed.ReadPixels(new Rect(0, 0, outW, outH), 0, 0);
            packed.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            return packed;
        }
    }
}
