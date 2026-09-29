using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using MagicaCloth2;

namespace HZExporter
{
    /// <summary>
    /// Exports MagicaCloth component authoring data as hz-engine .hzt TEXT
    /// (type ClothAuthoringBlobAsset). The engine's generic hzt cooker turns it
    /// into the binary .hza via reflection — no binary layout lives here.
    ///
    /// Field names/order mirror cloth_authoring_blob.h + ClothParams (cloth.h).
    /// Curves bake to the engine's 16 absolute samples (value * curve(i/15)).
    /// Selection points are written in RENDERER-LOCAL (mesh.vertices) space
    /// with the GLB X-negation — the same space as the exported GLB submesh.
    /// </summary>
    public static class HZClothExporter
    {
        const string OutDir = "ExportedAssets/cloth";
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        // --- exec entrypoints -----------------------------------------------------------

        public static void ExportKaguraCloths()
        {
            const string scenePath =
                "Assets/MagicaCloth2/Example (Can be deleted)/DemoScenes/URP/UnityChanKAGURA_URP.unity";
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            string[] wanted = { "Magica Cloth (skirt)", "Magica Cloth (sode L)", "Magica Cloth (sode R)" };
            int exported = 0;
            foreach (var cloth in Object.FindObjectsByType<MagicaCloth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                foreach (var w in wanted)
                {
                    if (cloth.gameObject.name.Trim() == w)
                    {
                        ExportCloth(cloth);
                        exported++;
                    }
                }
            }
            Debug.Log($"[HZClothExporter] exported {exported} cloths -> {OutDir}");
        }

        public static void ExportClothsInActiveScene()
        {
            int exported = 0;
            foreach (var cloth in Object.FindObjectsByType<MagicaCloth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (cloth.SerializeData.clothType != ClothProcess.ClothType.MeshCloth) continue;
                ExportCloth(cloth);
                exported++;
            }
            Debug.Log($"[HZClothExporter] exported {exported} MeshCloths -> {OutDir}");
        }

        /// <summary>
        /// Exports every Mesh/Bone cloth under `root` belonging to this character to
        /// `outDir`, returning the written .hzt file names. MeshCloths are filtered to
        /// `validRenderers` (the character's skinned meshes); BoneCloths are scoped by
        /// being under `root`. BoneSpring (Phase 3) is skipped. Used by the scene
        /// exporter to attach a character's cloths as an EcsClothRefs component.
        /// </summary>
        public static List<string> ExportMeshClothsUnder(GameObject root, Renderer[] validRenderers, string outDir)
        {
            var names = new List<string>();
            if (root == null) return names;
            var valid = new HashSet<Renderer>(validRenderers ?? new Renderer[0]);
            HZGlbWriter.ResetJointNames();
            // The set of joints the model export actually emits (the primary SMR's non-null bones,
            // by HZGlbWriter.ExportJointName — the same strings the GLB nodes carry). Cloth bone_names and collider
            // bone_names MUST be members; the engine resolves by exact string match and silently
            // nukes a whole cloth on one miss.
            var jointSet = JointSetFor(validRenderers);
            // Keep the cloth chains' own bones (incl. VRoid _end terminators that
            // aren't skin-weighted). The model exporter adds these same bones to the
            // GLB skeleton (via HZGlbWriter.ClothBoneCollector), so they resolve
            // in-engine and every chain keeps its full length. Without this the
            // chains are short a segment and the new leaf bones freeze.
            foreach (var t in CollectClothChainBones(root))
                jointSet.Add(HZGlbWriter.ExportJointName(t));
            foreach (var cloth in root.GetComponentsInChildren<MagicaCloth>(true))
            {
                var ct = cloth.SerializeData.clothType;
                if (ct == ClothProcess.ClothType.MeshCloth)
                {
                    var srcs = cloth.SerializeData.sourceRenderers;
                    if (valid.Count > 0 && (srcs.Count == 0 || srcs[0] == null || !valid.Contains(srcs[0]))) continue;
                }
                else if (ct != ClothProcess.ClothType.BoneCloth &&
                         ct != ClothProcess.ClothType.BoneSpring)
                {
                    continue;
                }
                string fileName = ExportClothToDir(cloth, outDir, jointSet);
                if (fileName != null) names.Add(fileName);
            }
            return names;
        }

        // Joints the model export emits = the non-null bones of the SMR with the most bones
        // (the same "primary" the GLB skeleton is built from), keyed by exported joint name.
        static HashSet<string> JointSetFor(Renderer[] renderers)
        {
            var set = new HashSet<string>();
            SkinnedMeshRenderer primary = null;
            if (renderers != null)
                foreach (var r in renderers)
                    if (r is SkinnedMeshRenderer s && s.bones != null &&
                        (primary == null || s.bones.Length > primary.bones.Length))
                        primary = s;
            if (primary != null)
                foreach (var b in primary.bones)
                    if (b != null) set.Add(HZGlbWriter.ExportJointName(b));
            return set;
        }

        // All bone Transforms simulated by BoneCloth/BoneSpring chains under `root`
        // (each rootBone + its descendants — exactly what MagicaCloth simulates,
        // including the _end terminator leaves). The model exporter adds these to
        // the GLB skeleton and the cloth exporter keeps them; both must agree.
        public static HashSet<Transform> CollectClothChainBones(GameObject root)
        {
            var set = new HashSet<Transform>();
            if (root == null) return set;
            foreach (var cloth in root.GetComponentsInChildren<MagicaCloth>(true))
            {
                var ct = cloth.SerializeData.clothType;
                if (ct != ClothProcess.ClothType.BoneCloth &&
                    ct != ClothProcess.ClothType.BoneSpring) continue;
                var roots = cloth.SerializeData.rootBones;
                if (roots == null) continue;
                foreach (var rb in roots)
                {
                    if (rb == null) continue;
                    foreach (var t in rb.GetComponentsInChildren<Transform>(true))
                        if (t != null) set.Add(t);
                }
            }
            return set;
        }

        // Bridge into the (MagicaCloth-agnostic) model exporter: lets HZGlbWriter
        // include cloth-chain bones in the GLB skeleton. Runs once at editor load.
        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterClothBoneCollector()
        {
            HZGlbWriter.ClothBoneCollector = (Transform t) =>
                CollectClothChainBones(t != null ? t.gameObject : null);
        }

        // --- .hzt writer helpers ---------------------------------------------------------

        static string F(float v) => v.ToString("F6", CI);

        static void Field(StringBuilder sb, int indent, string name, string value)
        {
            sb.Append(' ', indent * 2).Append(name).Append(' ').Append(value).Append('\n');
        }

        static void FieldF(StringBuilder sb, int indent, string name, float v) =>
            Field(sb, indent, name, F(v));

        static void FieldB(StringBuilder sb, int indent, string name, bool v) =>
            Field(sb, indent, name, v ? "true" : "false");

        static void FieldU(StringBuilder sb, int indent, string name, uint v) =>
            Field(sb, indent, name, v.ToString(CI));

        static void FieldI(StringBuilder sb, int indent, string name, int v) =>
            Field(sb, indent, name, v.ToString(CI));

        static void FieldS(StringBuilder sb, int indent, string name, string v) =>
            Field(sb, indent, name, "\"" + v + "\"");

        static void FieldV3(StringBuilder sb, int indent, string name, Vector3 v)
        {
            sb.Append(' ', indent * 2).Append(name).Append('\n');
            FieldF(sb, indent + 1, "x", v.x);
            FieldF(sb, indent + 1, "y", v.y);
            FieldF(sb, indent + 1, "z", v.z);
        }

        static void FieldQuat(StringBuilder sb, int indent, string name, Quaternion q)
        {
            sb.Append(' ', indent * 2).Append(name).Append('\n');
            FieldF(sb, indent + 1, "x", q.x);
            FieldF(sb, indent + 1, "y", q.y);
            FieldF(sb, indent + 1, "z", q.z);
            FieldF(sb, indent + 1, "w", q.w);
        }

        // ClothCurve: { f32 s[16] } — fixed array on one line: `s 16 v0 .. v15`
        static void FieldCurve(StringBuilder sb, int indent, string name, CurveSerializeData c)
        {
            sb.Append(' ', indent * 2).Append(name).Append('\n');
            sb.Append(' ', (indent + 1) * 2).Append("s 16");
            for (int i = 0; i < 16; i++)
                sb.Append(' ').Append(F(c.Evaluate(i / 15f)));
            sb.Append('\n');
        }

        // --- export one component -------------------------------------------------------

        static void ExportCloth(MagicaCloth cloth) => ExportClothToDir(cloth, OutDir, null);

        /// <summary>
        /// Exports one MeshCloth's authoring data to outDir as &lt;name&gt;_cloth.hzt and
        /// returns the file name (e.g. "skirt_cloth.hzt"), or null if the cloth has no
        /// valid source renderer/mesh. Used by both the standalone exec entry points and
        /// the scene exporter (which passes its per-scene cloth dir).
        /// </summary>
        public static string ExportClothToDir(MagicaCloth cloth, string outDir, HashSet<string> jointSet = null)
        {
            var sd = cloth.SerializeData;
            if (sd.clothType == ClothProcess.ClothType.BoneCloth ||
                sd.clothType == ClothProcess.ClothType.BoneSpring)
                return ExportBoneClothToDir(cloth, outDir, jointSet);
            var sel = cloth.GetSerializeData2().selectionData;
            if (sd.sourceRenderers.Count == 0 || sd.sourceRenderers[0] == null)
            {
                Debug.LogError($"[HZClothExporter] {cloth.name}: no source renderer");
                return null;
            }
            var ren = sd.sourceRenderers[0];
            Mesh mesh = ren is SkinnedMeshRenderer smr ? smr.sharedMesh
                       : ren.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null)
            {
                Debug.LogError($"[HZClothExporter] {cloth.name}: no mesh");
                return null;
            }

            var sb = new StringBuilder(64 * 1024);
            sb.Append("type ClothAuthoringBlobAsset\n");
            sb.Append("version 3\n");
            sb.Append("---\n");

            FieldU(sb, 0, "cloth_type", (uint)sd.clothType);
            FieldS(sb, 0, "target_mesh_name", mesh.name);

            sb.Append("params\n");
            WriteClothParams(sb, 1, sd);

            sb.Append("reduction\n");
            FieldF(sb, 1, "simple_distance", sd.reductionSetting.simpleDistance);
            FieldF(sb, 1, "shape_distance", sd.reductionSetting.shapeDistance);

            float maxConn = sel != null && sel.IsValid() ? sel.maxConnectionDistance : 0f;
            FieldF(sb, 0, "selection_max_connection_distance", maxConn);

            // MC2 NormalAlignmentSettings: None / BoundingBoxCenter / Transform.
            // For Transform mode the center is the adjustment transform (fallback:
            // the cloth's own transform, ClothProcess.cs:129-132), converted to
            // renderer-local mesh space like the selection points (negate X).
            var na = sd.normalAlignmentSetting;
            FieldU(sb, 0, "normal_alignment_mode", (uint)na.alignmentMode);
            Vector3 naPos = Vector3.zero;
            if (na.alignmentMode == NormalAlignmentSettings.AlignmentMode.Transform)
            {
                Transform naT = na.adjustmentTransform ? na.adjustmentTransform : cloth.ClothTransform;
                Vector3 lp = ren.transform.worldToLocalMatrix.MultiplyPoint3x4(naT.position);
                naPos = new Vector3(-lp.x, lp.y, lp.z);
            }
            sb.Append("normal_alignment_pos\n");
            FieldF(sb, 1, "x", naPos.x);
            FieldF(sb, 1, "y", naPos.y);
            FieldF(sb, 1, "z", naPos.z);

            FieldU(sb, 0, "target_vertex_count", (uint)mesh.vertexCount);

            // selection points: cloth local -> world -> RENDERER-LOCAL (mesh)
            // space -> GLB (negate X). The GLB stores mesh.vertices verbatim
            // (X negated); the export scene is in edit mode (bind pose).
            Matrix4x4 toMesh = ren.transform.worldToLocalMatrix *
                               cloth.ClothTransform.localToWorldMatrix;
            int selCount = sel != null && sel.IsValid() ? sel.positions.Length : 0;
            sb.Append("selection_positions ").Append(selCount).Append('\n');
            for (int i = 0; i < selCount; i++)
            {
                Vector3 p = toMesh.MultiplyPoint3x4(sel.positions[i]);
                if (i > 0) sb.Append('\n');
                FieldF(sb, 1, "x", -p.x);
                FieldF(sb, 1, "y", p.y);
                FieldF(sb, 1, "z", p.z);
            }

            sb.Append("selection_attributes ").Append(selCount);
            for (int i = 0; i < selCount; i++)
            {
                var a = sel.attributes[i];
                sb.Append(' ').Append(a.IsFixed() ? 1 : (a.IsMove() ? 2 : 0));
            }
            sb.Append('\n');

            int colCount = WriteColliders(sb, sd, jointSet);

            Directory.CreateDirectory(outDir);
            string fileName = SanitizeName(cloth.gameObject.name) + ".hzt";
            string outPath = Path.Combine(outDir, fileName);
            File.WriteAllText(outPath, sb.ToString());
            Debug.Log($"[HZClothExporter] {cloth.name}: mesh='{mesh.name}' verts={mesh.vertexCount} " +
                      $"sel={selCount} colliders={colCount} -> {outPath}");
            return fileName;
        }

        // Writes the `colliders <N>` block from a cloth's colliderList. Returns N. Colliders whose
        // bone can't be resolved to an exported joint are dropped with a loud error (never emitted
        // with a non-joint bone_name, which the engine can't resolve).
        static int WriteColliders(StringBuilder sb, ClothSerializeData sd, HashSet<string> jointSet)
        {
            var colliders = sd.colliderCollisionConstraint.colliderList;
            var emit = new List<ColliderComponent>();
            var bones = new List<Transform>();
            if (colliders != null)
                foreach (var c in colliders)
                {
                    if (c == null) continue;
                    // bone_name must be an EXPORTED joint. Walk up from the collider (incl. itself)
                    // to the nearest ancestor that's in the joint set. The local transform below is
                    // taken relative to that joint, so the offset stays correct even across pruned
                    // intermediates.
                    Transform bone = c.transform;
                    while (bone != null && jointSet != null && !jointSet.Contains(HZGlbWriter.ExportJointName(bone))) bone = bone.parent;
                    if (bone == null)
                    {
                        Debug.LogError($"[HZClothExporter] collider '{c.gameObject.name}' has no exported-joint " +
                                       "ancestor — dropped (engine can't resolve it).");
                        continue;
                    }
                    emit.Add(c);
                    bones.Add(bone);
                }

            sb.Append("colliders ").Append(emit.Count).Append('\n');
            for (int ci = 0; ci < emit.Count; ci++)
            {
                var col = emit[ci];
                var bone = bones[ci];
                if (ci > 0) sb.Append('\n');

                // Collider transform relative to its bound joint (same node-local Unity→glTF
                // conversion as joint locals — applied at the field write below). For a direct
                // child of the bone this is just its local pose; the matrix form also handles the
                // bone==collider (identity) and ancestor (composed) cases uniformly.
                Matrix4x4 rel = bone.worldToLocalMatrix * col.transform.localToWorldMatrix;
                Vector3 lp = rel.GetColumn(3);
                Quaternion lr = rel.rotation;
                lp += lr * new Vector3(col.center.x, col.center.y, col.center.z);

                uint type;
                uint direction = 0;
                bool alignedCenter = false;
                Vector3 size = col.GetSize();
                if (col is MagicaCapsuleCollider cap)
                {
                    type = 2; // ClothCollider_Capsule
                    direction = (uint)cap.direction;
                    alignedCenter = cap.alignedOnCenter;
                    // GLB X-negation flips the local X axis: swap the end radii
                    // so size.x is always the +axis-end radius in GLB space
                    if (cap.direction == MagicaCapsuleCollider.Direction.X)
                    {
                        float t = size.x; size.x = size.y; size.y = t;
                    }
                }
                else if (col is MagicaPlaneCollider)
                {
                    type = 3; // ClothCollider_Plane
                }
                else
                {
                    type = 1; // ClothCollider_Sphere
                }

                FieldU(sb, 1, "type", type);
                FieldS(sb, 1, "bone_name", HZGlbWriter.ExportJointName(bone));
                FieldV3(sb, 1, "local_pos", new Vector3(-lp.x, lp.y, lp.z));
                FieldQuat(sb, 1, "local_rot", new Quaternion(lr.x, -lr.y, -lr.z, lr.w));
                FieldU(sb, 1, "direction", direction);
                FieldV3(sb, 1, "size", size);
                FieldB(sb, 1, "aligned_center", alignedCenter);
            }
            return emit.Count;
        }

        /// <summary>
        /// Exports a BoneCloth (clothType==BoneCloth): the simulated bones are each
        /// rootBone + its Transform descendants. Attributes mirror MC2
        /// ClothProcess.cs:709-742 — default all Move with rootBones Fixed, then the
        /// painted selectionData (if userEdit) overrides per-bone by position.
        /// Bone names map to the model's GLB joint names. selection_* stay empty
        /// (the per-bone resolution happens here; the engine consumes bone_attributes).
        /// </summary>
        static string ExportBoneClothToDir(MagicaCloth cloth, string outDir, HashSet<string> jointSet = null)
        {
            var sd = cloth.SerializeData;
            // Collect bones in MC2's EXACT capture order so the per-bone painted
            // attribute mapping below (by index) lines up.
            //
            // CRITICAL: MagicaCloth orders its bones with a STACK / LIFO depth-first
            // walk (RenderSetupData.cs: push every rootBone, then pop; on each pop
            // push child 0..N so they pop in REVERSE order). selectionData.attributes
            // is indexed in THIS order (ClothProcess fills it via the same setup-data
            // transform index). Using Unity's GetComponentsInChildren() here instead
            // produces a DIFFERENT depth-first order (children forward, root first),
            // so attributes[i] referred to a different bone — which scattered the
            // "roots Fixed" flags onto mid-chain bones and pinned the strands (the
            // hair froze below each mis-pinned bone). Mirror MC2's walk 1:1.
            var boneTransforms = new List<Transform>();
            var boneNames = new List<string>();
            var boneAttrs = new List<int>();
            var seen = new HashSet<Transform>();
            // any rootBone is Fixed, even when reached as another root's descendant
            var rootSet = new HashSet<Transform>();
            foreach (var r in sd.rootBones) if (r != null) rootSet.Add(r);
            {
                var stack = new Stack<Transform>(256);
                foreach (var r in sd.rootBones) if (r != null) stack.Push(r);
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    if (!seen.Add(t)) continue;
                    boneTransforms.Add(t);
                    boneNames.Add(HZGlbWriter.ExportJointName(t));
                    boneAttrs.Add(rootSet.Contains(t) ? 1 : 2); // 1=Fixed root, 2=Move
                    for (int i = 0; i < t.childCount; i++) stack.Push(t.GetChild(i));
                }
            }

            // Painted override (MC2: valid selectionData attributes win over the
            // root-default). selectionData is the serialized paint; boneAttributeDict
            // is [NonSerialized] runtime-only, so the paint lives here. attributes[i]
            // is bone i in MC2's setup-data order — which boneTransforms now mirrors,
            // so the index mapping is valid. We VERIFY that below by position.
            var bsel = cloth.GetSerializeData2().selectionData;
            if (bsel != null && bsel.IsValid() &&
                bsel.attributes.Length == boneTransforms.Count)
            {
                VerifyBoneAttributeOrder(cloth, bsel, boneTransforms);
                int fixedN = 0, moveN = 0, invalidN = 0;
                for (int b = 0; b < boneTransforms.Count; b++)
                {
                    var a = bsel.attributes[b];
                    int attr = a.IsFixed() ? 1 : (a.IsMove() ? 2 : 0);
                    boneAttrs[b] = attr;
                    if (attr == 1) fixedN++; else if (attr == 2) moveN++; else invalidN++;
                }
                Debug.Log($"[HZClothExporter] {cloth.name}: BoneCloth painted attributes " +
                          $"fixed={fixedN} move={moveN} invalid={invalidN}");
                // Safety net: a Fixed bone that is NOT a chain root pins the strand
                // mid-way (this was the long-hair freeze bug). Legal in MC2 (painting)
                // but rare — surface it loudly so an ordering regression can't pass
                // silently again.
                for (int b = 0; b < boneTransforms.Count; b++)
                    if (boneAttrs[b] == 1 && !rootSet.Contains(boneTransforms[b]))
                        Debug.LogWarning($"[HZClothExporter] {cloth.name}: non-root bone " +
                            $"'{boneNames[b]}' is Fixed (pins the strand here). If the hair " +
                            "is meant to swing freely this indicates a bone-order mismatch.");

                // MC2 excludes Invalid-painted verts from the proxy. Drop leaf Invalid
                // bones (attr 0); warn if an Invalid bone has simulated children, since
                // dropping it mid-chain would orphan them (keep it as Move in that case).
                for (int b = boneTransforms.Count - 1; b >= 0; b--)
                {
                    if (boneAttrs[b] != 0) continue;
                    bool hasSimChild = false;
                    for (int c = 0; c < boneTransforms.Count; c++)
                        if (c != b && boneTransforms[c].parent == boneTransforms[b]) { hasSimChild = true; break; }
                    if (hasSimChild)
                    {
                        Debug.LogWarning($"[HZClothExporter] {cloth.name}: bone '{boneNames[b]}' " +
                                         "painted Invalid but has simulated children; keeping as Move.");
                        boneAttrs[b] = 2;
                    }
                    else
                    {
                        boneTransforms.RemoveAt(b);
                        boneNames.RemoveAt(b);
                        boneAttrs.RemoveAt(b);
                    }
                }
            }
            // Intersect the chain with the model export's actual joint set: keep only bones that
            // exist in the exported skeleton (by exact name). This drops the '_end' tip markers
            // (the model prunes them) and anything else the model didn't emit, so every bone_name
            // the engine sees resolves — no silent whole-cloth nuke. When no joint set is supplied
            // (standalone exec entry points) fall back to dropping '_end' leaf tips.
            if (jointSet != null)
            {
                for (int b = boneTransforms.Count - 1; b >= 0; b--)
                {
                    if (jointSet.Contains(boneNames[b])) continue;
                    boneTransforms.RemoveAt(b);
                    boneNames.RemoveAt(b);
                    boneAttrs.RemoveAt(b);
                }
            }
            else
            {
                for (int b = boneTransforms.Count - 1; b >= 0; b--)
                {
                    if (!boneNames[b].EndsWith("_end", System.StringComparison.Ordinal)) continue;
                    bool hasChild = false;
                    for (int c = 0; c < boneTransforms.Count; c++)
                        if (c != b && boneTransforms[c].parent == boneTransforms[b]) { hasChild = true; break; }
                    if (hasChild) continue;
                    boneTransforms.RemoveAt(b);
                    boneNames.RemoveAt(b);
                    boneAttrs.RemoveAt(b);
                }
            }

            if (boneNames.Count == 0)
            {
                Debug.LogError($"[HZClothExporter] {cloth.name}: BoneCloth has no bones in the exported skeleton");
                return null;
            }

            var sb = new StringBuilder(16 * 1024);
            sb.Append("type ClothAuthoringBlobAsset\n");
            sb.Append("version 3\n");
            sb.Append("---\n");

            FieldU(sb, 0, "cloth_type", (uint)sd.clothType); // 1
            FieldS(sb, 0, "target_mesh_name", "");
            sb.Append("params\n");
            WriteClothParams(sb, 1, sd);
            sb.Append("reduction\n");
            FieldF(sb, 1, "simple_distance", 0f);
            FieldF(sb, 1, "shape_distance", 0f);
            FieldF(sb, 0, "selection_max_connection_distance", 0f);
            FieldU(sb, 0, "normal_alignment_mode", 0u);
            sb.Append("normal_alignment_pos\n");
            FieldF(sb, 1, "x", 0f);
            FieldF(sb, 1, "y", 0f);
            FieldF(sb, 1, "z", 0f);
            FieldU(sb, 0, "target_vertex_count", 0u);
            sb.Append("selection_positions 0\n");
            sb.Append("selection_attributes 0\n");
            int colCount = WriteColliders(sb, sd, jointSet);

            // BlobArray(StringBlob): inline space-separated quoted tokens on the field line
            sb.Append("bone_names ").Append(boneNames.Count);
            foreach (var n in boneNames)
                sb.Append(" \"").Append(n).Append('"');
            sb.Append('\n');
            sb.Append("bone_attributes ").Append(boneAttrs.Count);
            foreach (var a in boneAttrs)
                sb.Append(' ').Append(a);
            sb.Append('\n');

            Directory.CreateDirectory(outDir);
            string fileName = SanitizeName(cloth.gameObject.name) + ".hzt";
            string outPath = Path.Combine(outDir, fileName);
            File.WriteAllText(outPath, sb.ToString());
            Debug.Log($"[HZClothExporter] {cloth.name}: BoneCloth bones={boneNames.Count} " +
                      $"colliders={colCount} -> {outPath}");
            return fileName;
        }

        // Space-invariant guard that boneTransforms[i] really is selectionData entry i.
        // The selection positions and the bone Transforms live in different (rigid,
        // ~uniform-scale) spaces, so absolute coords differ — but the gap between
        // consecutive entries must agree up to ONE constant scale IF the ordering
        // matches. A scattered/mismatched order makes those ratios vary wildly. This
        // only logs (never alters output); it exists so a future MC2 traversal change
        // can't silently re-scatter the Fixed/Move flags again.
        static void VerifyBoneAttributeOrder(MagicaCloth cloth, SelectionData bsel, List<Transform> boneTransforms)
        {
            int n = boneTransforms.Count;
            if (bsel.positions == null || bsel.positions.Length != n || n < 3) return;
            var ratios = new List<float>(n);
            for (int i = 0; i + 1 < n; i++)
            {
                Vector3 ps = (Vector3)bsel.positions[i] - (Vector3)bsel.positions[i + 1];
                Vector3 pw = boneTransforms[i].position - boneTransforms[i + 1].position;
                float ds = ps.magnitude, dw = pw.magnitude;
                if (ds < 1e-6f || dw < 1e-6f) continue;
                ratios.Add(ds / dw);
            }
            if (ratios.Count < 3) return;
            ratios.Sort();
            float median = ratios[ratios.Count / 2];
            if (median < 1e-6f) return;
            int outliers = 0;
            foreach (var r in ratios)
                if (Mathf.Abs(r - median) / median > 0.25f) outliers++;
            if (outliers > ratios.Count * 0.1f)
                Debug.LogError($"[HZClothExporter] {cloth.name}: BoneCloth attribute ORDER MISMATCH — " +
                    $"{outliers}/{ratios.Count} consecutive bone gaps disagree with selectionData order. " +
                    "Painted Fixed/Move flags would land on the WRONG bones and pin strands. " +
                    "MC2's bone traversal order likely changed — re-sync boneTransforms with RenderSetupData's walk.");
        }

        static string SanitizeName(string n)
        {
            n = n.Trim().ToLowerInvariant();
            n = n.Replace("magica cloth", "").Replace("(", "").Replace(")", "").Trim();
            return n.Replace(" ", "_") + "_cloth";
        }

        // --- ClothParams (field names/order mirror cloth.h ClothParams) -----------------

        static void WriteClothParams(StringBuilder sb, int ind, ClothSerializeData sd)
        {
            var inertia = sd.inertiaConstraint;
            var dist = sd.distanceConstraint;
            var ar = sd.angleRestorationConstraint;
            var al = sd.angleLimitConstraint;
            var mo = sd.motionConstraint;
            var cc = sd.colliderCollisionConstraint;
            var sc = sd.selfCollisionConstraint;
            var spring = sd.springConstraint;
            var wind = sd.wind;
            bool isSpring = sd.clothType == ClothProcess.ClothType.BoneSpring;

            Vector3 g = sd.gravityDirection;
            FieldV3(sb, ind, "gravity_dir", new Vector3(-g.x, g.y, g.z));
            FieldF(sb, ind, "gravity", sd.gravity);
            FieldF(sb, ind, "gravity_falloff", sd.gravityFalloff);
            FieldF(sb, ind, "damping", sd.damping.value);
            FieldB(sb, ind, "use_damping_curve", sd.damping.useCurve);
            FieldCurve(sb, ind, "damping_curve", sd.damping);
            FieldF(sb, ind, "stabilization_time", sd.stablizationTimeAfterReset);
            FieldF(sb, ind, "animation_pose_ratio", sd.animationPoseRatio);
            FieldF(sb, ind, "distance_stiffness", dist.stiffness.value);
            FieldB(sb, ind, "use_distance_curve", dist.stiffness.useCurve);
            FieldCurve(sb, ind, "distance_stiffness_curve", dist.stiffness);
            FieldI(sb, ind, "distance_iterations", 0);
            FieldF(sb, ind, "distance_velocity_attenuation", 0.3f); // MC2 Define.System
            FieldF(sb, ind, "tether_compression", sd.tetherConstraint.distanceCompression);
            FieldB(sb, ind, "use_angle_restoration", ar.useAngleRestoration);
            FieldF(sb, ind, "angle_restoration_stiffness", ar.stiffness.value);
            FieldB(sb, ind, "use_angle_restoration_curve", ar.stiffness.useCurve);
            FieldCurve(sb, ind, "angle_restoration_curve", ar.stiffness);
            FieldF(sb, ind, "angle_restoration_velocity_attenuation", ar.velocityAttenuation);
            FieldF(sb, ind, "angle_restoration_gravity_falloff", ar.gravityFalloff);
            FieldB(sb, ind, "use_angle_limit", al.useAngleLimit);
            FieldF(sb, ind, "angle_limit_deg", al.limitAngle.value);
            FieldF(sb, ind, "angle_limit_stiffness", al.stiffness);
            FieldB(sb, ind, "use_angle_limit_curve", al.limitAngle.useCurve);
            FieldCurve(sb, ind, "angle_limit_curve", al.limitAngle);
            FieldB(sb, ind, "use_max_distance", mo.useMaxDistance);
            FieldF(sb, ind, "max_distance", mo.maxDistance.value);
            FieldB(sb, ind, "use_max_distance_curve", mo.maxDistance.useCurve);
            FieldCurve(sb, ind, "max_distance_curve", mo.maxDistance);
            FieldF(sb, ind, "motion_stiffness", mo.stiffness);
            FieldB(sb, ind, "use_backstop", mo.useBackstop);
            FieldF(sb, ind, "backstop_radius", mo.backstopRadius);
            FieldF(sb, ind, "backstop_distance", mo.backstopDistance.value);
            FieldB(sb, ind, "use_backstop_distance_curve", mo.backstopDistance.useCurve);
            FieldCurve(sb, ind, "backstop_distance_curve", mo.backstopDistance);
            // enum mapping: MC2 ClothNormalAxis {Right=0, Up=1, Forward=2,...}
            // vs engine ClothNormalAxis {Up=0, Right=1, Forward=2,...} — only
            // Right/Up swap
            uint normalAxis = (uint)sd.normalAxis;
            if (normalAxis == 0) normalAxis = 1;
            else if (normalAxis == 1) normalAxis = 0;
            FieldU(sb, ind, "normal_axis", normalAxis);
            FieldB(sb, ind, "is_spring", isSpring);
            FieldF(sb, ind, "spring_power", spring.springPower);
            FieldF(sb, ind, "spring_limit_distance", spring.limitDistance);
            FieldF(sb, ind, "spring_normal_limit_ratio", spring.normalLimitRatio);
            FieldF(sb, ind, "spring_noise", spring.springNoise);
            FieldF(sb, ind, "radius", sd.radius.value);
            FieldB(sb, ind, "use_radius_curve", sd.radius.useCurve);
            FieldCurve(sb, ind, "radius_curve", sd.radius);
            FieldB(sb, ind, "use_collision", cc.mode != ColliderCollisionConstraint.Mode.None);
            FieldF(sb, ind, "collision_friction", cc.friction);
            FieldB(sb, ind, "use_self_collision", sc.selfMode != SelfCollisionConstraint.SelfCollisionMode.None);
            FieldF(sb, ind, "self_collision_thickness", sc.surfaceThickness.value);
            FieldB(sb, ind, "use_self_thickness_curve", sc.surfaceThickness.useCurve);
            FieldCurve(sb, ind, "self_thickness_curve", sc.surfaceThickness);
            FieldF(sb, ind, "self_collision_cloth_mass", sc.clothMass);
            FieldF(sb, ind, "world_inertia", inertia.worldInertia);
            FieldF(sb, ind, "depth_inertia", inertia.depthInertia);
            FieldF(sb, ind, "movement_inertia_smoothing", inertia.movementInertiaSmoothing);
            FieldF(sb, ind, "movement_speed_limit", LimitValue(inertia.movementSpeedLimit));
            FieldF(sb, ind, "rotation_speed_limit", LimitValue(inertia.rotationSpeedLimit));
            FieldF(sb, ind, "local_inertia", inertia.localInertia);
            FieldF(sb, ind, "local_movement_speed_limit", LimitValue(inertia.localMovementSpeedLimit));
            FieldF(sb, ind, "local_rotation_speed_limit", LimitValue(inertia.localRotationSpeedLimit));
            FieldF(sb, ind, "particle_speed_limit", LimitValue(inertia.particleSpeedLimit));
            FieldF(sb, ind, "centrifugal_acceleration", inertia.centrifualAcceleration);
            FieldU(sb, ind, "teleport_mode", (uint)inertia.teleportMode);
            FieldF(sb, ind, "teleport_distance", inertia.teleportDistance);
            FieldU(sb, ind, "wind_mode", 0); // wind zones are scene objects
            FieldV3(sb, ind, "wind_dir", Vector3.zero);
            FieldF(sb, ind, "wind_main", 0f);
            FieldF(sb, ind, "wind_influence", wind.influence);
            FieldF(sb, ind, "wind_turbulence", wind.turbulence);
            FieldF(sb, ind, "wind_depth_weight", wind.depthWeight);
            FieldF(sb, ind, "wind_frequency", wind.frequency);
            FieldF(sb, ind, "wind_blend", wind.blend);
            FieldF(sb, ind, "wind_synchronization", wind.synchronization);
            FieldV3(sb, ind, "wind_zone_center", Vector3.zero);
            FieldF(sb, ind, "wind_zone_radius", 0f);
            FieldB(sb, ind, "use_bending", sd.triangleBendingConstraint.stiffness > 0f &&
                                           sd.clothType == ClothProcess.ClothType.MeshCloth);
            FieldF(sb, ind, "bending_stiffness", sd.triangleBendingConstraint.stiffness);
            FieldF(sb, ind, "blend_weight", sd.blendWeight);
            FieldF(sb, ind, "rotational_interpolation", sd.rotationalInterpolation);
            FieldF(sb, ind, "root_rotation", sd.rootRotation);
        }

        static float LimitValue(CheckSliderSerializeData s) => s.use ? s.value : 0f;
    }
}
