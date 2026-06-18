"""
Dump and validate a ModelBlobAsset .hza file (hz-engine-v5 format).
Usage: python dump_model_hza.py <path_to.hza>

v5 layout:
  ModelBlobAsset (160): header(48) + mat4(64) + meshes(16) + joints(16) + joint_names(16)
  MeshBlobAsset (40):   name(8) + submeshes(16) + blendshape_names(16)
  SubMeshBlobAsset (176): material(8) + format(4) + idx_count(4) + vtx_count(4) + aabb(24)
                          + indices(16) + positions(16) + normals(16) + tangents(16) + uvs(16)
                          + has_skin(4) + joint_indices(16) + joint_weights(16) + blendshapes(16)
  JointBlobAsset (84):  mat4(64) + parent_index(4) + children(16)
"""
import sys
from hza import HzaReader

SIZEOF_MODEL = 160
SIZEOF_MESH = 40
SIZEOF_SUBMESH = 192
SIZEOF_JOINT = 84


def dump_model(path: str):
    r = HzaReader(path)
    r.validate_header("ModelBlobAsset")
    r.dump_header()

    # === ModelBlobAsset (160 bytes) ===
    off = 48  # after header
    # mat4 armature_transform
    mat4 = r.read_mat4(off); off += 64
    meshes_bp = r.read_blob_ptr(off); off += 16
    joints_bp = r.read_blob_ptr(off); off += 16
    joint_names_bp = r.read_blob_ptr(off); off += 16

    r.check(off == SIZEOF_MODEL, f"ModelBlobAsset size: expected {SIZEOF_MODEL}, got {off}")

    is_identity = all(abs(mat4[i * 4 + j] - (1.0 if i == j else 0.0)) < 1e-6
                      for i in range(4) for j in range(4))

    mesh_count = meshes_bp.count
    joint_count = joints_bp.count if not joints_bp.empty else 0

    print(f"\n=== ModelBlobAsset (160 bytes) ===")
    print(f"  armature_xform:  {'identity' if is_identity else [f'{v:.4f}' for v in mat4]}")
    print(f"  meshes:          {meshes_bp}")
    r.check_blob_ptr(meshes_bp, "MeshBlobAsset", SIZEOF_MESH, "meshes")
    print(f"  joints:          {joints_bp}")
    if joint_count > 0:
        r.check_blob_ptr(joints_bp, "JointBlobAsset", SIZEOF_JOINT, "joints")
    print(f"  joint_names:     {joint_names_bp}")
    if joint_count > 0:
        r.check_blob_ptr(joint_names_bp, "StringBlob", 8, "joint_names")

    # === MeshBlobAsset(s) ===
    for mi in range(mesh_count):
        mesh_off = meshes_bp.data_pos + mi * SIZEOF_MESH

        name = r.read_string_blob(mesh_off)
        submeshes_bp = r.read_blob_ptr(mesh_off + 8)
        blendshape_names_bp = r.read_blob_ptr(mesh_off + 24)

        print(f"\n=== MeshBlobAsset[{mi}] (at byte {mesh_off}) ===")
        print(f"  name:             \"{name}\"")
        print(f"  submeshes:        {submeshes_bp}")
        r.check_blob_ptr(submeshes_bp, "SubMeshBlobAsset", SIZEOF_SUBMESH, f"mesh[{mi}].submeshes")
        print(f"  blendshape_names: {blendshape_names_bp}")

        # === SubMeshBlobAsset(s) ===
        for si in range(submeshes_bp.count):
            sub_off = submeshes_bp.data_pos + si * SIZEOF_SUBMESH
            sub_base = sub_off

            mat_path, mat_ts, mat_th = r.read_blob_asset_ref(sub_off); sub_off += 24
            index_format = r.read_u32(sub_off); sub_off += 4
            index_count = r.read_u32(sub_off); sub_off += 4
            vertex_count = r.read_u32(sub_off); sub_off += 4
            aabb_min = r.read_vec3(sub_off); sub_off += 12
            aabb_max = r.read_vec3(sub_off); sub_off += 12

            indices_bp = r.read_blob_ptr(sub_off); sub_off += 16
            positions_bp = r.read_blob_ptr(sub_off); sub_off += 16
            normals_bp = r.read_blob_ptr(sub_off); sub_off += 16
            tangents_bp = r.read_blob_ptr(sub_off); sub_off += 16
            uvs_bp = r.read_blob_ptr(sub_off); sub_off += 16

            has_skin = r.read_u32(sub_off); sub_off += 4
            joint_indices_bp = r.read_blob_ptr(sub_off); sub_off += 16
            joint_weights_bp = r.read_blob_ptr(sub_off); sub_off += 16
            blendshapes_bp = r.read_blob_ptr(sub_off); sub_off += 16

            r.check(sub_off == sub_base + SIZEOF_SUBMESH,
                    f"SubMeshBlobAsset size: expected {SIZEOF_SUBMESH}, got {sub_off - sub_base}")

            print(f"\n  --- SubMesh[{mi}.{si}] (at byte {sub_base}) ---")
            print(f"    material:       \"{mat_path}\" (type_size={mat_ts}, typehash=0x{mat_th:08x})")
            print(f"    index_format:   {index_format} ({'U16' if index_format == 0 else 'U32'})")
            print(f"    index_count:    {index_count}")
            print(f"    vertex_count:   {vertex_count}")
            print(f"    aabb_min:       ({aabb_min[0]:.4f}, {aabb_min[1]:.4f}, {aabb_min[2]:.4f})")
            print(f"    aabb_max:       ({aabb_max[0]:.4f}, {aabb_max[1]:.4f}, {aabb_max[2]:.4f})")
            print(f"    has_skin:       {has_skin}")

            idx_ts = 2 if index_format == 0 else 4
            idx_tn = "u16" if index_format == 0 else "u32"

            print(f"    indices:        {indices_bp}")
            r.check_blob_ptr(indices_bp, idx_tn, idx_ts, "indices")
            r.check(indices_bp.count == index_count,
                    f"indices count {indices_bp.count} != index_count {index_count}")

            print(f"    positions:      {positions_bp}")
            r.check_blob_ptr(positions_bp, "f32", 4, "positions")
            r.check(positions_bp.count == vertex_count * 3,
                    f"positions: expected {vertex_count * 3} floats, got {positions_bp.count}")

            print(f"    normals:        {normals_bp}")
            r.check_blob_ptr(normals_bp, "f32", 4, "normals")

            print(f"    tangents:       {tangents_bp}")
            r.check_blob_ptr(tangents_bp, "f32", 4, "tangents")

            print(f"    uvs:            {uvs_bp}")
            r.check_blob_ptr(uvs_bp, "f32", 4, "uvs")

            if has_skin:
                r.check_blob_ptr(joint_indices_bp, "u8", 1, "joint_indices")
                r.check_blob_ptr(joint_weights_bp, "f32", 4, "joint_weights")
                print(f"    joint_indices:  {joint_indices_bp}")
                print(f"    joint_weights:  {joint_weights_bp}")

            print(f"    blendshapes:    {blendshapes_bp}")

            # Sample vertex data
            if positions_bp.count >= 3:
                print(f"\n    First 4 vertices (positions):")
                for vi in range(min(4, vertex_count)):
                    px, py, pz = r.read_vec3(positions_bp.data_pos + vi * 12)
                    print(f"      [{vi}] ({px:.4f}, {py:.4f}, {pz:.4f})")

            if index_count >= 3:
                print(f"    First 6 indices:")
                for ii in range(min(6, index_count)):
                    if index_format == 0:
                        val = r.read(indices_bp.data_pos + ii * 2, "H")[0]
                    else:
                        val = r.read_u32(indices_bp.data_pos + ii * 4)
                    print(f"      [{ii}] {val}")

    # === JointBlobAsset(s) ===
    if joint_count > 0:
        print(f"\n=== Skeleton ({joint_count} joints) ===")

        # Read joint names
        names = []
        for ji in range(joint_count):
            sb_off = joint_names_bp.data_pos + ji * 8
            name = r.read_string_blob(sb_off)
            names.append(name)

        for ji in range(joint_count):
            j_off = joints_bp.data_pos + ji * SIZEOF_JOINT

            # inverse_bind_matrix (64 bytes)
            ibm = r.read_mat4(j_off)
            # parent_index (4 bytes)
            parent_idx = r.read_i32(j_off + 64)
            # children BlobPtr (16 bytes)
            children_bp = r.read_blob_ptr(j_off + 68)

            r.check_blob_ptr(children_bp, "u32", 4, f"joint[{ji}].children")

            if parent_idx != -1:
                r.check(0 <= parent_idx < joint_count,
                        f"joint[{ji}].parent_index={parent_idx} out of range [0,{joint_count})")

            if not children_bp.empty:
                for ci in range(children_bp.count):
                    child_idx = r.read_u32(children_bp.data_pos + ci * 4)
                    r.check(child_idx < joint_count,
                            f"joint[{ji}].children[{ci}]={child_idx} out of range [0,{joint_count})")

            # Print first few and last few joints
            if ji < 4 or ji >= joint_count - 2:
                jname = names[ji] if ji < len(names) else "?"
                parent_name = names[parent_idx] if 0 <= parent_idx < len(names) else "ROOT"
                n_children = children_bp.count if not children_bp.empty else 0
                print(f"  [{ji:3d}] \"{jname}\" parent={parent_idx}({parent_name}) children={n_children}")
            elif ji == 4:
                print(f"  ... ({joint_count - 6} more joints) ...")

    r.print_result()
    sys.exit(0 if r.ok else 1)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(f"Usage: python {sys.argv[0]} <file.hza>")
        sys.exit(1)
    dump_model(sys.argv[1])
